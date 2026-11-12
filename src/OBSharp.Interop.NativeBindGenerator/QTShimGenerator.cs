using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

using ClangSharp.Interop;

using Humanizer;

namespace OBSharp.Interop.NativeBindGenerator;

/// <summary>
/// Qt 专用 shim 生成器：在 <see cref="CppShimGenerator"/> 的通用 C 导出层之上，
/// 增加信号连接、<c>QString</c> 转换、<c>QObject</c> 生命周期与元对象成员过滤。
/// </summary>
/// <remarks>
/// 输出形态沿用旧 <c>CppRedirectGenerator</c> 的 Qt 约定：
/// 信号不再作为普通成员导出，而是生成 <c>connect_信号名</c> 导出与独立的连接句柄，
/// 句柄由 <c>qt_shim_disconnect</c> 断开并释放。
/// 本生成器只产出 C 导出层，C# 绑定仍由 <see cref="CBindingGenerator"/> 统一完成。
/// </remarks>
public sealed partial class QTShimGenerator(QTShimGeneratorOptions options) : CppShimGenerator(options)
{
    /// <summary>
    /// <c>Q_OBJECT</c> 宏注入的元对象成员，不属于用户 API，必须跳过。
    /// </summary>
    private static readonly HashSet<string> MetaObjectMembers =
    [
        "metaObject", "qt_metacast", "qt_metacall", "tr", "trUtf8",
        "qt_check_for_QGADGET_macro", "qt_static_metacall", "qt_metacall_declare",
    ];

    /// <summary>
    /// 源码行中的 <c>signals:</c> / <c>Q_SIGNALS:</c> 访问说明符。
    /// </summary>
    [GeneratedRegex(@"^\s*(Q_SIGNALS|Q_SIGNAL|signals)\s*:\s*$", RegexOptions.Compiled | RegexOptions.CultureInvariant)]
    private static partial Regex SignalsSpecifierRegex { get; }

    /// <summary>
    /// 已知的 Qt 值类型：没有可导出的成员，不作为宿主类型登记。
    /// </summary>
    private static readonly HashSet<string> QtValueTypes =
    [
        "QString", "QByteArray", "QVariant", "QDate", "QTime", "QDateTime",
        "QUrl", "QSize", "QSizeF", "QPoint", "QPointF", "QRect", "QRectF",
        "QColor", "QFont", "QIcon", "QImage", "QPixmap", "QBrush", "QPen",
        "QTransform", "QRegExp", "QRegularExpression", "QJsonValue",
        "QTextStream", "QDebug", "QLocale",
    ];

    private readonly Dictionary<string, string[]> _sourceLines = [];
    private readonly List<string> _callbackTypedefs = [];
    private readonly HashSet<string> _connectionDeclarations = [with(StringComparer.Ordinal)];

    // ---- 类型特化 ----

    /// <summary>
    /// Qt 值类型没有可导出的成员，不作为宿主类型。
    /// </summary>
    protected override bool IsRecordDeclaration(CXCursor cursor)
        => base.IsRecordDeclaration(cursor) is true
            && QtValueTypes.Contains(cursor.Spelling.ToString()) is false;

    /// <summary>
    /// 元对象成员、信号与运算符都不作为普通成员导出。
    /// </summary>
    /// <remarks>
    /// <c>operator=</c> 这类 C++ 特殊成员在 C 侧无意义（自赋值、移动等语义无法表达），导出只会带来无用的绑定。
    /// </remarks>
    protected override bool SkipMember(CXCursor member)
    {
        if (base.SkipMember(member))
            return true;

        if (MetaObjectMembers.Contains(member.Spelling.ToString()))
            return true;

        if (IsOperator(member))
            return true;

        // 信号不能被直接调用，只能通过 connect 订阅，故转为独立导出
        return member.kind is CXCursorKind.CXCursor_CXXMethod && IsSignal(member);
    }

    /// <summary>
    /// 是否为 C++ 运算符成员（名称以 <c>operator</c> 开头）。
    /// </summary>
    private static bool IsOperator(CXCursor member)
        => member.kind is CXCursorKind.CXCursor_CXXMethod
            && member.Spelling.ToString().StartsWith("operator", StringComparison.Ordinal);

    /// <summary>
    /// <c>QString</c> 在 C 侧表现为 <c>char</c>，与 .NET 的 UTF-8 marshaller 对应。
    /// </summary>
    protected override string? ScalarCType(CXType canonical)
        => IsQString(canonical) ? "char" : base.ScalarCType(canonical);

    /// <summary>
    /// <c>QString</c> 的 C++ 侧类型文本。
    /// </summary>
    protected override string CppTypeOf(CXType canonical)
        => IsQString(canonical) ? "QString" : base.CppTypeOf(canonical);

    /// <summary>
    /// <c>QString</c> 允许作为返回值桥接。
    /// </summary>
    protected override bool IsSupportedReturn(CXType type)
        => IsQString(Canonical(type)) || base.IsSupportedReturn(type);

    /// <summary>
    /// 返回 <c>QString</c> 的函数在 C 侧返回 <c>const char*</c>（借用，托管侧只复制不释放）。
    /// </summary>
    protected override string? ReturnCType(CXType resultType, string? handleFallback)
        => IsQString(Canonical(resultType)) ? "const char*" : base.ReturnCType(resultType, handleFallback);

    /// <summary>
    /// <c>QString</c> 参数的重载后缀用 <c>s</c>。
    /// </summary>
    protected override string ParameterSuffix(CXType canonical)
        => IsQString(canonical) ? "s" : base.ParameterSuffix(canonical);

    /// <summary>
    /// <c>QString</c> 按 UTF-8 字符串传递：值本身是 <c>char</c>，指针是 <c>char*</c>。
    /// </summary>
    protected override string? ParameterCType(CXType canonical, bool indirect)
    {
        CXType shape = Canonical(canonical);
        if (IsQString(shape))
            return "char";

        if (shape.kind is CXTypeKind.CXType_Pointer && IsQString(Canonical(shape.PointeeType)))
            return indirect ? "char**" : "char*";

        return base.ParameterCType(canonical, indirect);
    }

    /// <summary>
    /// 把 <c>QString</c> 的 C 实参还原为 <c>QString</c>。
    /// </summary>
    /// <remarks>
    /// <c>ToCpp(const CharT*)</c> 返回 <c>std::basic_string</c>，而 Qt 接口接受 <c>QString</c>，
    /// 需要显式构造，因此这里不能直接用 <c>CppShimMarshall</c> 的字符串重载。
    /// </remarks>
    protected override string BuildToCppExpression(string name, CXType canonical, string cType, string cppType)
    {
        CXType shape = Canonical(canonical);
        if (IsQString(shape))
            return $"QString::fromUtf8({name})";

        if (shape.kind is CXTypeKind.CXType_Pointer && IsQString(Canonical(shape.PointeeType)))
            return $"QString::fromUtf8(*reinterpret_cast<char**>({name}))";

        return base.BuildToCppExpression(name, canonical, cType, cppType);
    }

    // ---- 成员发现 ----

    /// <summary>
    /// 通用成员发现之后，再为 QObject 派生类型补充信号连接导出。
    /// </summary>
    protected override void DiscoverMembers(IEnumerable<CXCursor> declarations)
    {
        base.DiscoverMembers(declarations);

        foreach (ShimTypeInfo type in Types)
            AppendSignalConnections(type);

        AppendDisconnectExport();
    }

    /// <summary>
    /// 在翻译单元释放前记录哪些宿主是 <c>QObject</c> 派生：输出阶段不能再访问 cursor。
    /// </summary>
    protected override void DiscoverTypes(IEnumerable<CXCursor> declarations)
    {
        base.DiscoverTypes(declarations);

        foreach (ShimTypeInfo type in Types)
            type.IsFrameworkType = IsQObjectType(type.Declaration);
    }

    /// <summary>
    /// 为类型下的每个信号生成 <c>connect_信号名</c> 导出，返回连接句柄。
    /// </summary>
    private void AppendSignalConnections(ShimTypeInfo type)
    {
        if (type.IsFrameworkType is false)
            return;

        CXCursor definition = type.Declaration.Definition;
        if (definition.IsNull)
            return;

        int count = definition.NumMethods;
        for (int i = 0; i < count; i++)
        {
            CXCursor method = definition.GetMethod((uint)i);
            if (IsSignal(method) is false || method.IsImplicit || method.CXXMethod_IsStatic)
                continue;

            AppendSignalConnect(type, method);
        }
    }

    private void AppendSignalConnect(ShimTypeInfo type, CXCursor signal)
    {
        string signalName = signal.Spelling.ToString();
        string snake = signalName.Underscore();

        List<ShimParameterInfo> parameters = [];
        List<string> cppTypes = [];
        List<string> lambdaParameters = [];
        List<string> lambdaArguments = ["user"];

        int count = signal.NumArguments;
        for (int i = 0; i < count; i++)
        {
            CXCursor argument = signal.GetArgument((uint)i);
            ShimParameterInfo parameter = BuildParameter(argument, i);
            if (parameter.CType is null)
                return;

            parameters.Add(parameter);
            cppTypes.Add(CppTypeOf(Canonical(argument.Type)));
            lambdaParameters.Add(cppTypes[i] + " p" + i.ToString(CultureInfo.InvariantCulture));
            lambdaArguments.Add("p" + i.ToString(CultureInfo.InvariantCulture));
        }

        string callbackName = UniqueSymbol(type.SymbolPrefix + "_" + snake + "_callback");
        _callbackTypedefs.Add(BuildCallbackTypedef(callbackName, parameters));

        string connectionType = ConnectionTypeName();
        _connectionDeclarations.Add(connectionType);

        List<string> parametersDeclaration = BuildConnectParameters(type, callbackName);
        string body = BuildConnectBody(type, signal, signalName, lambdaParameters, lambdaArguments);

        AddCustomExport(
            type,
            type.SymbolPrefix + "_connect_" + snake,
            connectionType + "*",
            parametersDeclaration,
            body);
    }

    /// <summary>
    /// 回调 typedef：首参是用户数据，其余按顺序对应信号形参。
    /// </summary>
    private static string BuildCallbackTypedef(string callbackName, List<ShimParameterInfo> parameters)
    {
        List<string> parts = ["void* user"];
        for (int i = 0; i < parameters.Count; i++)
            parts.Add(parameters[i].CType + " p" + i.ToString(CultureInfo.InvariantCulture));

        return "typedef void (CPP_SHIM_CALL *" + callbackName + ")(" + string.Join(", ", parts) + ");";
    }

    private static List<string> BuildConnectParameters(ShimTypeInfo type, string callbackName)
    {
        List<string> parts =
        [
            type.HandleName + "* self",
            callbackName + " callback",
            "void* user",
        ];

        return parts;
    }

    /// <summary>
    /// 连接实现：构造 <c>QMetaObject::Connection</c> 并返回其地址作为句柄。
    /// </summary>
    private string BuildConnectBody(
        ShimTypeInfo type,
        CXCursor signal,
        string signalName,
        List<string> lambdaParameters,
        List<string> lambdaArguments)
    {
        bool overloaded = IsOverloaded(signal, signalName);
        string signalPointer = overloaded
            ? "static_cast<void (" + type.CppName + "::*)(" + string.Join(", ", lambdaParameters.Select(p => p[..p.LastIndexOf(' ')]))
                + ")>(&" + type.CppName + "::" + signalName + ")"
            : "&" + type.CppName + "::" + signalName;

        using StringWriter writer = CreateWriter();
        // 句柄类型是 shim 自定义的不透明结构体，需要从 Qt 的连接对象强转
        writer.Write("return reinterpret_cast<");
        writer.Write(ConnectionTypeName());
        writer.Write("*>(new QMetaObject::Connection(QObject::connect(");
        writer.Write("reinterpret_cast<");
        writer.Write(type.CppName);
        writer.Write("*>(self), ");
        writer.Write(signalPointer);
        writer.Write(", reinterpret_cast<");
        writer.Write(type.CppName);
        writer.Write("*>(self), [callback, user](");
        writer.Write(string.Join(", ", lambdaParameters));
        writer.Write(") { callback(");
        writer.Write(string.Join(", ", lambdaArguments));
        // 闭合 callback(...)、lambda、connect(...)、new Connection 与 reinterpret_cast
        writer.WriteLine("); })));");
        return writer.ToString();
    }

    /// <summary>
    /// 断开连接并释放连接句柄。
    /// </summary>
    private void AppendDisconnectExport()
    {
        if (_callbackTypedefs.Count is 0)
            return;

        string connectionType = ConnectionTypeName();
        _connectionDeclarations.Add(connectionType);

        using StringWriter body = CreateWriter();
        // 句柄类型与 Qt 连接对象是不相关类型，只能用 reinterpret_cast 还原
        body.Write("auto* connection = reinterpret_cast<QMetaObject::Connection*>(handle);");
        body.WriteLine();
        body.Write("if (connection == nullptr)");
        body.WriteLine();
        body.Write("    return;");
        body.WriteLine();
        body.Write("QObject::disconnect(*connection);");
        body.WriteLine();
        body.Write("delete connection;");

        AddCustomExport(
            null,
            options.DisconnectFunctionName,
            "void",
            [connectionType + "* handle"],
            body.ToString());
    }

    private string ConnectionTypeName()
        => options.ConnectionHandleTypeName;

    // ---- 输出 ----

    /// <summary>
    /// 返回 <c>QString</c> 的函数需要把 UTF-8 副本交给托管侧，避免临时对象失效。
    /// </summary>
    protected override void WriteFunctionDefinition(StringWriter writer, ShimFunctionInfo function)
    {
        if (function.Role is ShimRole.Method or ShimRole.Free && function.ReturnCType is "const char*")
        {
            WriteQStringReturn(writer, function);
            return;
        }

        if (function.Role is ShimRole.Finalize
            && options.UseDeleteLater
            && function.Owner is { IsFrameworkType: true })
        {
            WriteDeleteLater(writer, function);
            return;
        }

        base.WriteFunctionDefinition(writer, function);
    }

    /// <summary>
    /// 返回 <c>QString</c> 的函数：以 UTF-8 借用指针返回，不分配内存。
    /// </summary>
    /// <remarks>
    /// 返回的指针指向 shim 内部的静态缓冲区，由 <c>ReturnStringMarshaller</c> 在托管侧复制后丢弃，
    /// 托管侧不释放、也不调用任何释放函数；指针在下次调用同名导出前有效。
    /// 这与 C 侧返回 <c>const char*</c> 的借用语义一致，只是数据来自 Qt 字符串而非调用方提供的缓冲区。
    /// </remarks>
    private void WriteQStringReturn(StringWriter writer, ShimFunctionInfo function)
    {
        writer.Write(function.ReturnCType);
        writer.Write(" CPP_SHIM_CALL ");
        writer.Write(function.Symbol);
        writer.Write("(");
        writer.Write(DefinitionParameters(function));
        writer.WriteLine(")");
        writer.WriteLine("{");

        string invocation = function.Role is ShimRole.Method
            ? "reinterpret_cast<" + function.Owner!.CppName + "*>(self)->" + function.MethodName + "(" + ArgumentList(function) + ")"
            : function.FreeQualifiedName + "(" + ArgumentList(function) + ")";

        // 静态缓冲区保证指针在本次调用返回后仍然有效，生命周期到下一次调用为止
        writer.WriteLine("    static QByteArray borrowed;");
        writer.Write("    borrowed = ");
        writer.Write(invocation);
        writer.WriteLine(".toUtf8();");
        writer.WriteLine("    return borrowed.isEmpty() ? nullptr : borrowed.constData();");
        writer.WriteLine("}");
        writer.WriteLine();
    }

    /// <summary>
    /// <c>QObject</c> 派生类型用 <c>deleteLater</c> 释放，避免跨线程销毁崩溃。
    /// </summary>
    private void WriteDeleteLater(StringWriter writer, ShimFunctionInfo function)
    {
        writer.Write(function.ReturnCType);
        writer.Write(" CPP_SHIM_CALL ");
        writer.Write(function.Symbol);
        writer.Write("(");
        writer.Write(DefinitionParameters(function));
        writer.WriteLine(")");
        writer.WriteLine("{");
        writer.Write("    auto* target = reinterpret_cast<");
        writer.Write(function.Owner!.CppName);
        writer.WriteLine("*>(self);");
        writer.WriteLine("    if (target != nullptr)");
        writer.WriteLine("        target->deleteLater();");
        writer.WriteLine("}");
        writer.WriteLine();
    }

    /// <summary>
    /// 派生出的头文件片段必须追加在基类头文件之后，否则会出现在 <c>#pragma once</c> 之前、且落在链接块之外。
    /// </summary>
    protected override string BuildHeader()
    {
        string header = base.BuildHeader();

        // 1) 在 #pragma once 之后插入 Qt 转换头，紧跟随通用转换头
        string includeAnchor = "#include \"" + CppShimMarshal.FileName + "\"";
        int includeIndex = header.IndexOf(includeAnchor, StringComparison.Ordinal);
        if (includeIndex >= 0)
        {
            int afterLine = header.IndexOf('\r', includeIndex);
            if (afterLine >= 0)
            {
                includeIndex = afterLine + 2;
                header = header.Insert(includeIndex, "#include \"" + QtShimMarshalFileName + "\"\r\n");
            }
        }

        // 2) 在链接块内、函数声明之前插入连接句柄与回调 typedef
        string anchor = "\r\n#if defined(__cplusplus)\r\nextern \"C\" {\r\n#endif\r\n";
        int anchorIndex = header.IndexOf(anchor, StringComparison.Ordinal);
        if (anchorIndex < 0)
            return header;

        using StringWriter block = CreateWriter();
        block.WriteLine();
        block.Write("// Qt 信号连接：连接句柄与回调签名。");
        block.WriteLine();
        block.Write("struct ");
        block.Write(ConnectionTypeName());
        block.WriteLine(" { int reserved; };");

        foreach (string typedef in _callbackTypedefs.OrderBy(value => value, StringComparer.Ordinal))
            block.WriteLine(typedef);

        block.Write("typedef struct ");
        block.Write(ConnectionTypeName());
        block.Write(" ");
        block.Write(ConnectionTypeName());
        block.Write(';');
        block.WriteLine();

        int insertAt = anchorIndex + anchor.Length;
        header = header.Insert(insertAt, block.ToString());
        return header;
    }

    /// <summary>
    /// 在基类产物之外，额外释放 Qt 专用的转换头文件。
    /// </summary>
    protected override void WriteOutputs()
    {
        base.WriteOutputs();

        if (ShimOptions.WriteMarshaller)
            QtShimMarshal.WriteTo(ShimOptions.Output.FullName);
    }

    /// <summary>
    /// Qt 专用转换头文件名。
    /// </summary>
    private const string QtShimMarshalFileName = "qt_shim_marshal.hpp";

    // ---- Qt 识别 ----

    /// <summary>
    /// <c>QString</c>：去掉 <c>const</c> 限定后拼写就是 <c>QString</c>。
    /// </summary>
    private static bool IsQString(CXType canonical)
    {
        string spelling = canonical.Spelling.ToString().Trim();
        while (spelling.StartsWith("const ", StringComparison.Ordinal))
            spelling = spelling["const ".Length..].TrimStart();

        return spelling is "QString";
    }

    /// <summary>
    /// 类型（或其任一基类）是否为 <c>QObject</c> 派生。
    /// </summary>
    private static bool IsQObjectType(CXCursor cursor)
    {
        if (cursor.Spelling.ToString() is "QObject")
            return true;

        CXCursor definition = cursor.Definition;
        if (definition.IsNull)
            return false;

        int baseCount = definition.NumBases;
        for (int i = 0; i < baseCount; i++)
        {
            CXType baseType = Canonical(definition.GetBase((uint)i).Type);
            if (baseType.kind is CXTypeKind.CXType_Record && IsQObjectType(baseType.Declaration))
                return true;
        }

        return false;
    }

    /// <summary>
    /// 方法是否为信号：所在访问区是 <c>signals:</c>，或带 <c>qt_signal</c> 注解。
    /// </summary>
    private bool IsSignal(CXCursor member)
    {
        if (member.kind is not CXCursorKind.CXCursor_CXXMethod)
            return false;

        // Qt 把 signals 视作 protected，因此不能用访问修饰符直接判断，先按注解
        if (HasSignalAnnotation(member))
            return true;

        return IsInsideSignalsBlock(member);
    }

    /// <summary>
    /// 通过 <c>-DQT_ANNOTATE_ACCESS_SPECIFIER</c> 时，clang 会把 <c>signals:</c> 记为 <c>qt_signal</c> 注解。
    /// </summary>
    private static bool HasSignalAnnotation(CXCursor member)
    {
        CXCursor specifier = FindAccessSpecifier(member);
        if (specifier.IsNull || specifier.NumChildren <= 0)
            return false;

        int count = specifier.NumChildren;
        for (int i = 0; i < count; i++)
        {
            CXCursor child = specifier.GetChild((uint)i);
            if (child.kind is CXCursorKind.CXCursor_AnnotateAttr
                && child.Spelling.ToString() is "qt_signal")
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 查找成员所属的访问说明符节点（最近的前一个 <c>CXXAccessSpecifier</c>）。
    /// </summary>
    private static CXCursor FindAccessSpecifier(CXCursor member)
    {
        CXCursor parent = member.SemanticParent;
        if (parent.IsNull)
            return default;

        CXCursor current = default;
        int count = parent.NumChildren;
        for (int i = 0; i < count; i++)
        {
            CXCursor child = parent.GetChild((uint)i);
            if (child.kind is CXCursorKind.CXCursor_CXXAccessSpecifier)
            {
                current = child;
                continue;
            }

            if (child.CanonicalCursor.Hash == member.CanonicalCursor.Hash)
                return current;
        }

        return default;
    }

    /// <summary>
    /// 成员是否位于 <c>signals:</c> 或 <c>Q_SIGNALS:</c> 访问区。
    /// </summary>
    /// <remarks>
    /// clang 把 <c>signals:</c> 当作 protected 处理，无法从 AST 区分，只能读源码行：
    /// 从成员所在行向上扫描，先遇到信号说明符即为信号，先遇到普通访问说明符或类定义结尾则不是。
    /// </remarks>
    private bool IsInsideSignalsBlock(CXCursor member)
    {
        member.Location.GetExpansionLocation(out CXFile file, out uint line, out _, out _);
        string path = file.Name.ToString();
        if (path.Length is 0 || line is 0)
            return false;

        if (!_sourceLines.TryGetValue(path, out string[]? lines))
        {
            if (!File.Exists(path))
                return false;

            lines = File.ReadAllLines(path);
            _sourceLines[path] = lines;
        }

        if (line > lines.Length)
            return false;

        for (int i = (int)line - 2; i >= 0; i--)
        {
            if (SignalsSpecifierRegex.IsMatch(lines[i]))
                return true;

            if (AccessSpecifierRegex.IsMatch(lines[i]))
                return false;

            // 越过当前类的外层定义就不再继续：更上层的 signals: 属于别的类型
            if (ClassOpeningRegex.IsMatch(lines[i]))
                return false;
        }

        return false;
    }

    /// <summary>
    /// 普通访问说明符（<c>public:</c> / <c>protected:</c> / <c>private:</c>）。
    /// </summary>
    [GeneratedRegex(@"^\s*(public|protected|private)\s*:", RegexOptions.Compiled | RegexOptions.CultureInvariant)]
    private static partial Regex AccessSpecifierRegex { get; }

    /// <summary>
    /// 类定义起始行（<c>class Xxx</c> / <c>struct Xxx</c>），用于限制向上扫描的范围。
    /// </summary>
    [GeneratedRegex(@"^\s*(class|struct)\s(?!=)[^;]*$", RegexOptions.Compiled | RegexOptions.CultureInvariant)]
    private static partial Regex ClassOpeningRegex { get; }

    /// <summary>
    /// 同名方法是否构成重载，决定信号成员指针是否需要 <c>static_cast</c> 消歧。
    /// </summary>
    private static bool IsOverloaded(CXCursor signal, string name)
    {
        CXCursor parent = signal.SemanticParent;
        if (parent.IsNull)
            return false;

        int count = parent.NumChildren;
        int matches = 0;
        for (int i = 0; i < count; i++)
        {
            CXCursor child = parent.GetChild((uint)i);
            if (child.kind is CXCursorKind.CXCursor_CXXMethod && child.Spelling.ToString() == name)
                matches++;
        }

        return matches > 1;
    }
}
