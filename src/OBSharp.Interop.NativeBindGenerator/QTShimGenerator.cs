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
    /// 成员函数地址能否从 shim 里取用（<c>&amp;Class::member</c>）。
    /// </summary>
    /// <remarks>
    /// <c>signals:</c> 在 clang 里算 protected 区，但 Qt 的公开信号本来就需要能被连接，
    /// 因此这里只排除**明确声明为 private/protected 的成员**；
    /// Qt 的 protected slot（如 <c>resetInternalData</c>）会因此被正确跳过。
    /// </remarks>
    private static bool IsMemberAddressable(CXCursor member)
        => member.CXXAccessSpecifier is CX_CXXAccessSpecifier.CX_CXXInvalidAccessSpecifier
            or CX_CXXAccessSpecifier.CX_CXXPublic;

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
    /// <remarks>
    /// 只对**按值返回**的 <c>QString</c> 生效。若是 <c>QString*</c> / <c>QString&amp;</c> 返回，
    /// 走基类路径（按句柄/引用语义处理），否则会生成 <c>return QString*</c> 到 <c>const char*</c> 的
    /// 不可编译转换（C2440）。
    /// </remarks>
    protected override string? ReturnCType(CXType resultType, string? handleFallback)
    {
        if (IsQStringValueReturn(resultType))
            return "const char*";

        // QString* 返回位：指针不是字符串内容，不能映射成 char*（QString::data() 会报 C2440）
        // 基类会按记录指针给出句柄或降级指针，这里显式确保不走 const char*
        if (IsQStringPointerReturn(resultType))
            return "void*";

        return base.ReturnCType(resultType, handleFallback);
    }

    /// <summary>
    /// 返回值是否为 <c>QString*</c>（指针形态的字符串，不能按内容封送）。
    /// </summary>
    private static bool IsQStringPointerReturn(CXType resultType)
    {
        CXType shape = Canonical(resultType);
        return shape.kind is CXTypeKind.CXType_Pointer
            && IsQString(Canonical(shape.PointeeType));
    }

    /// <summary>
    /// 返回值是否为**按值**的 <c>QString</c>（剥掉 typedef/elaborated 后仍是记录类型本身）。
    /// </summary>
    private static bool IsQStringValueReturn(CXType resultType)
    {
        CXType shape = Canonical(resultType);
        if (shape.kind is not CXTypeKind.CXType_Record)
            return false;

        return IsQString(shape);
    }

    /// <summary>
    /// <c>QString</c> 参数的重载后缀用 <c>s</c>。
    /// </summary>
    protected override string ParameterSuffix(CXType canonical)
        => IsQString(canonical) ? "s" : base.ParameterSuffix(canonical);

    /// <summary>
    /// <c>QString</c> 按 UTF-8 字符串传递：值本身是 <c>char</c>，指针是 <c>char*</c>。
    /// </summary>
    /// <remarks>
    /// 只有**按值**的 <c>QString</c> 才做内容封送。`QString*` 是**出参**（库会把结果写进去），
    /// 若也当 <c>char*</c> 入参封送就会出现「出参当入参」的方向错误（QLockFile::getLockInfo，C2664），
    /// 因此 <c>QString*</c> 一律降级为裸指针。
    /// </remarks>
    protected override string? ParameterCType(CXType canonical, bool indirect)
    {
        CXType shape = Canonical(canonical);
        if (IsQString(shape))
            return "char";

        // 指针/引用形态的 QString：出参语义，降级为裸指针，不做内容封送
        if (shape.kind is CXTypeKind.CXType_Pointer && IsQString(Canonical(shape.PointeeType)))
            return indirect ? "void**" : "void*";

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

        // QString* 是出参：不做内容封送，只把裸指针还原成 QString*（库会往里写）
        if (shape.kind is CXTypeKind.CXType_Pointer && IsQString(Canonical(shape.PointeeType)))
            return $"reinterpret_cast<QString*>({name})";

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

            // 信号连接会生成 `&Class::member`：被删除的成员（如 QDateEdit 的拷贝赋值）取地址会引用已删除函数（C2280）
            if (method.IsDeleted)
                continue;

            // 连接导出会生成 `&Class::member` 取成员函数地址：非 public 的成员在此处不可访问
            // （Qt 的 resetInternalData 是 protected slot，取地址会报 C2248）
            if (IsMemberAddressable(method) is false)
                continue;

            AppendSignalConnect(type, method);
        }
    }

    private void AppendSignalConnect(ShimTypeInfo type, CXCursor signal)
    {
        string signalName = signal.Spelling.ToString();

        // 标识符必须合法：`operator=` 里含 `=`，Underscore() 不会剔除它，
        // 直接拼进符号名会生成 `cpp_shim_X_connect_operator=(...)` 这种非法 C 标识符
        string snake = ToIdentifier(SignalIdentifier(signalName));

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

            bool byValue = IsCopyableSignalParameter(argument);
            parameters.Add(parameter);
            cppTypes.Add(CppTypeOf(Canonical(argument.Type)));

            // 按值传参的 lambda 形参会触发拷贝构造 + 析构；若该类型的析构不可访问
            // （QSessionManager 的析构是 private），连接表达式会引用私有析构（C2248）。
            // 这类参数改用 const 引用传递，不构造也不析构
            lambdaParameters.Add(byValue
                ? SignalLambdaParameter(argument, cppTypes[i]) + " p" + i.ToString(CultureInfo.InvariantCulture)
                : "const " + cppTypes[i] + "& p" + i.ToString(CultureInfo.InvariantCulture));
            // 回调实参必须与 C 形参**同源**：形参是 void* / 句柄指针时，实参也要是同一层级的指针，
            // 否则按值对象接不进 void*（C2664）；已是指针的实参不能再取一次地址
            lambdaArguments.Add(SignalArgument(argument, parameter, !byValue, cppTypes[i], i));
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
    /// 按值接参的 lambda 形参类型文本：指针目标的 <c>const</c> 必须与信号声明一致。
    /// </summary>
    /// <remarks>
    /// <see cref="CppShimGenerator.CppTypeOf"/> 拼指针时用剥壳后的目标类型，而展开
    /// <c>CXType_Elaborated</c> 取的是 <c>NamedType</c>（<c>clang_Type_getNamedType</c>
    /// 返回的记录类型不带限定符），<c>const QPointerEvent*</c> 因此退化成 <c>QPointerEvent*</c>。
    /// 其余场合丢 const 只是少写限定符（实参到只读形参的隐式转换仍然成立），但 lambda 形参
    /// 必须与信号声明**完全**一致：<c>QObject::connect</c> 的 ComputeFunctorArgumentCount
    /// 逐前缀试调用，<c>const T*</c> 不能绑定到 <c>T*</c>，参数个数会被判成 0，
    /// 进而报 qobject.h C2039/C2065 一族错误（QPointingDevice::grabChanged 实测）。
    /// </remarks>
    private static string SignalLambdaParameter(CXCursor argument, string cppType)
    {
        if (HasConstPointee(argument.Type) is false)
            return cppType;

        return cppType.StartsWith("const ", StringComparison.Ordinal) ? cppType : "const " + cppType;
    }

    /// <summary>
    /// 指向的目标是否带 <c>const</c> 限定。
    /// </summary>
    private static bool HasConstPointee(CXType declared)
    {
        CXType canonical = Canonical(declared);
        if (canonical.kind is not CXTypeKind.CXType_Pointer)
            return false;

        return declared.PointeeType.IsConstQualified || canonical.PointeeType.IsConstQualified;
    }

    /// <summary>
    /// 信号形参能否按值传递：记录类型一律按 <c>const</c> 引用传递。
    /// </summary>
    /// <remarks>
    /// 按值接参会先在 lambda 表达式里构造、再在退出时析构该对象；析构不可访问
    /// （<c>QSessionManager</c> 的析构是 private）或类型不可拷贝时，connect 表达式都无法编译。
    /// 只判记录会漏掉引用形参：<c>QSessionManager&amp;</c> 的 canonical kind 是引用而不是记录，
    /// 于是引用参数被按值接参（C2248 实测）。
    /// 引用既不构造也不析构，对所有记录形态都安全，因此剥掉引用后按记录判定。
    /// </remarks>
    private static bool IsCopyableSignalParameter(CXCursor argument)
    {
        CXType shape = PeelReference(Canonical(argument.Type));
        return shape.kind is not CXTypeKind.CXType_Record;
    }

    /// <summary>
    /// 信号名到符号名用的基础形式：C++ 运算符名先做可读化替换，避免运算符字符进入标识符。
    /// </summary>
    /// <remarks>
    /// <c>operator=</c>、<c>operator==</c>、<c>operator!=</c>、<c>operator&lt;&lt;</c> 等经
    /// <c>Underscore()</c> 后仍会保留 <c>=</c>、<c>&lt;</c>、<c>!</c> 等字符，因此先映射成
    /// 语义等价的可读后缀，再交给 <see cref="CppShimGenerator.ToIdentifier"/> 做最终净化。
    /// </remarks>
    private static string SignalIdentifier(string name)
    {
        if (name.StartsWith("operator", StringComparison.Ordinal) is false)
            return name;

        string operation = name["operator".Length..].Trim();
        string suffix = operation switch
        {
            "=" => "assign",
            "==" => "eq",
            "!=" => "ne",
            "<" => "lt",
            "<=" => "le",
            ">" => "gt",
            ">=" => "ge",
            "<<" => "ls",   // left shift
            ">>" => "rs",   // right shift
            "+" => "add",
            "-" => "sub",
            "*" => "mul",
            "/" => "div",
            "%" => "mod",
            "[]" => "index",
            "()" => "call",
            "+=" => "addassign",
            "-=" => "subassign",
            "" => "operator",
            _ => "op",
        };

        return "operator_" + suffix;
    }

    /// <summary>
    /// 回调实参是否需要取地址：C 侧形参是指针（句柄或 T*）而 C++ 实参是按值对象时。
    /// </summary>
    /// <remarks>
    /// 回调形参 <c>parameter.CType</c> 由 <see cref="CppShimGenerator.BuildParameter"/> 推导，
    /// 实参表达式必须**与它的类型恰好一致**。关键点：<c>cpp_shim_X</c> 是不透明句柄 struct
    /// （<c>typedef struct cpp_shim_X cpp_shim_X;</c>），与 C++ 的 <c>X</c> 是**两个不同的 struct**，
    /// 因此无论实参是值还是指针，都必须显式 <c>reinterpret_cast</c> 到形参类型：
    /// ① 形参是 <c>void*</c>（记录按值被降级）：值实参取址后再转 <c>void*</c>；
    /// ② 形参是句柄指针：值实参 <c>reinterpret_cast&lt;T*&gt;(&amp;p)</c>，指针实参 <c>reinterpret_cast&lt;T*&gt;(p)</c>；
    /// ③ 形参是标量：原样传递。
    /// </remarks>
    private static string SignalArgument(
        CXCursor argument,
        ShimParameterInfo parameter,
        bool byReference,
        string cppType,
        int index)
    {
        string name = "p" + index.ToString(CultureInfo.InvariantCulture);
        string cType = parameter.CType ?? "";
        if (cType.EndsWith('*') is false)
        {
            // 形参是标量：枚举必须显式转换，C++ 不会把 enum class 隐式转成 int32_t
            // （QRangeModel::AutoConnectPolicy 作为信号实参时就漏了这一步，C2664）
            return Canonical(argument.Type).kind is CXTypeKind.CXType_Enum
                ? "static_cast<" + cType + ">(" + name + ")"
                : name;
        }

        // 实参已是指针：直接强转为形参类型（不取址，否则指针套指针）。
        // 目标带 const 时（lambda 形参必须与信号声明完全一致，不能丢 const）不能直接
        // 转成 void*/句柄指针：reinterpret_cast 会丢弃限定（C2440），先经 const_cast 去掉
        if (Canonical(argument.Type).kind is CXTypeKind.CXType_Pointer)
        {
            string value = HasConstPointee(argument.Type)
                ? "const_cast<void*>(static_cast<const void*>(" + name + "))"
                : name;
            return cType is "void*" ? value : "reinterpret_cast<" + cType + ">(" + value + ")";
        }

        // 实参是值：先取址，再强转为形参类型。
        // 取址得到的是 C++ 类型指针（如 QPrivateSignal*），而形参是不透明句柄指针
        // （cpp_shim_*_QPrivateSignal*）——两者是不同的 struct，缺这一步转换就会报 C2664。
        // 引用接参时形参是 const 引用，取址得到的是 const T*，必须先去掉 const 才能转成
        // 非 const 的句柄指针（reinterpret_cast 不能隐式丢弃 const）
        string address = byReference
            ? "const_cast<" + cppType + "*>(&" + name + ")"
            : "&" + name;
        return cType is "void*" ? address : "reinterpret_cast<" + cType + ">(" + address + ")";
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
        string? memberPointerType = overloaded ? MemberPointerType(type, signal) : null;
        string signalPointer = memberPointerType is null
            ? "&" + type.CppName + "::" + signalName
            : "static_cast<" + memberPointerType + ">(&" + type.CppName + "::" + signalName + ")";

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
    /// 重载信号的成员指针类型文本，供 <c>static_cast</c> 消歧。
    /// </summary>
    /// <remarks>
    /// 形参类型必须取自**声明本身**（<c>CXType.Spelling</c> 保留 const 与引用），
    /// 不能用 lambda 形参反推：lambda 形参为规避析构会把记录类型改成 const 引用，
    /// 与信号声明的签名不一定一致，<c>static_cast</c> 会因签名不匹配而失败。
    /// Qt 的信号一律返回 void，非 void 声明不参与消歧。
    /// </remarks>
    private static string? MemberPointerType(ShimTypeInfo type, CXCursor signal)
    {
        if (Canonical(signal.ResultType).kind is not CXTypeKind.CXType_Void)
            return null;

        List<string> parts = [];
        int count = signal.NumArguments;
        for (int i = 0; i < count; i++)
            parts.Add(signal.GetArgument((uint)i).Type.Spelling.ToString());

        return "void (" + type.CppName + "::*)(" + string.Join(", ", parts) + ")";
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

        // 2) 必须插在基类的句柄前置声明块之后：回调 typedef 的形参里会用到
        //    cpp_shim_*_QPrivateSignal、cpp_shim_QSocketDescriptor 等句柄，
        //    若早于前置声明，在 extern "C" 下会被编译器当作未知类型（MSVC C2061）。
        string anchor = "\r\nCPP_SHIM_EXPORT ";
        int anchorIndex = header.IndexOf(anchor, StringComparison.Ordinal);
        if (anchorIndex < 0)
        {
            // 没有任何导出函数时也要能插入，退回到链接块收尾位置（插在右花括号之前）
            anchor = "\r\n#ifdef __cplusplus\r\n}";
            anchorIndex = header.IndexOf(anchor, StringComparison.Ordinal);
            if (anchorIndex >= 0)
                anchorIndex += "\r\n#ifdef __cplusplus".Length;
        }

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

        header = header.Insert(anchorIndex, block.ToString());
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
    /// <remarks>
    /// 必须走类定义的 <c>NumMethods</c>/<c>GetMethod</c>：<c>SemanticParent</c> 拿到的是
    /// 「被引用的类游标」，它的 <c>NumChildren</c> 在跨翻译单元引用时返回 -1
    /// （QSignalMapper 实测），按子节点遍历永远数不到重载，<c>&amp;Class::map</c> 就保留了
    /// overloaded-function 形态（C2665）。
    /// </remarks>
    private static bool IsOverloaded(CXCursor signal, string name)
    {
        CXCursor scope = signal.SemanticParent;
        if (scope.IsNull)
            return false;

        CXCursor definition = scope.Definition;
        if (definition.IsNull is false)
            scope = definition;

        int count = scope.NumMethods;
        int matches = 0;
        for (int i = 0; i < count; i++)
        {
            if (scope.GetMethod((uint)i).Spelling.ToString() == name)
                matches++;
        }

        return matches > 1;
    }
}


