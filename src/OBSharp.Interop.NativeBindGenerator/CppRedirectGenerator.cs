using System.Text;
using System.Text.RegularExpressions;

using ClangSharp.Interop;

using Humanizer;

namespace OBSharp.Interop.NativeBindGenerator;

/// <summary>
/// 自动发现 --header 中的 C++ 类与枚举，生成 C 式导出的 native 转发代码，
/// 再交给 <see cref="CExportGenerator"/> 生成 C# 绑定。
/// </summary>
internal sealed unsafe class CppRedirectGenerator(RootCommand options)
{
    private sealed record ClassInfo(CXCursor Cursor, string CName, string CppName);

    private sealed record EnumInfo(CXCursor Cursor, string CName, string CppName);

    private sealed record Param(string H, string C, string Name);

    private sealed record ParamPlan(List<Param> Params, string Argument);

    private sealed record ReturnPlan(string H, string C, Func<string, string> Wrap, bool NeedsCount = false);

    private const string SelfName = "self";
    private const string CountName = "cppm_count";

    private static readonly HashSet<string> WellKnownTypes =
    [
        "size_t", "ptrdiff_t", "ssize_t", "intptr_t", "uintptr_t", "intmax_t", "uintmax_t",
        "int8_t", "int16_t", "int32_t", "int64_t", "uint8_t", "uint16_t", "uint32_t", "uint64_t",
    ];

    private readonly Dictionary<string, ClassInfo> _classes = [];
    private readonly Dictionary<string, EnumInfo> _enums = [];
    private readonly HashSet<string> _usedClasses = [];
    private readonly HashSet<string> _cNames = [];
    private readonly List<string> _headerDeclarations = [];
    private readonly List<string> _sourceDefinitions = [];
    private readonly List<string> _sourceTypedefs = [];
    private HashSet<string> _headers = [];
    private string _headerText = "";

    public void Generate()
    {
        LibClangLoader.Ensure();
        string[] headers = [.. options.Header.Select(Path.GetFullPath)];
        _headers = [.. headers.Select(NormalizePath)];

        string staging = Path.Combine(Path.GetTempPath(), "nbg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            string tuPath = Path.Combine(staging, "redirect.cpp");
            File.WriteAllLines(tuPath, headers.Select(header =>
                $"#include \"{header.Replace('\\', '/').Replace("\"", "\\\"")}\""));

            CXIndex index = CXIndex.Create(false, false);
            try
            {
                CXTranslationUnit tu = CXTranslationUnit.Parse(
                    index,
                    tuPath,
                    options.ClangArguments,
                    default,
                    CXTranslationUnit_Flags.CXTranslationUnit_SkipFunctionBodies);
                if (tu.Handle == IntPtr.Zero)
                    throw new InvalidOperationException("libclang 无法创建翻译单元，请检查 -- 之后的 clang 参数");

                try
                {
                    if (CExportGenerator.ReportErrors(tu))
                        throw new InvalidOperationException("clang 解析头文件时出现错误，已中止生成");

                    Discover(CExportGenerator.ChildrenOf(clang.getTranslationUnitCursor(tu)));
                    foreach (ClassInfo info in _classes.Values.OrderBy(c => c.CName, StringComparer.Ordinal))
                    {
                        int before = _sourceDefinitions.Count;
                        BindClass(info);
                        if (_sourceDefinitions.Count > before)
                            _usedClasses.Add(Usr(info.Cursor));
                    }

                    if (_connectionUsed)
                        BindConnectionDestroy();

                    // 头文件文本依赖仍存活的游标，必须在翻译单元释放之前生成
                    _headerText = BuildHeader();
                }
                finally
                {
                    tu.Dispose();
                }
            }
            finally
            {
                index.Dispose();
            }
        }
        finally
        {
            Directory.Delete(staging, recursive: true);
        }

        if (_sourceDefinitions.Count is 0)
            throw new InvalidOperationException("指定头文件中没有找到可转发的 C++ 类");

        string nativeDirectory = Path.GetFullPath(options.NativeOutput);
        Directory.CreateDirectory(nativeDirectory);
        string headerPath = Path.Combine(nativeDirectory, options.NativeName + ".h");
        // 一律 UTF-8 无 BOM；用 MSVC 编译时请加 /utf-8
        UTF8Encoding utf8 = new(false);
        File.WriteAllText(
            Path.Combine(nativeDirectory, CppMarshaller.FileName),
            CppMarshaller.Source.Replace("\r\n", "\n") + "\n",
            utf8);
        File.WriteAllText(headerPath, _headerText, utf8);
        File.WriteAllText(Path.Combine(nativeDirectory, options.NativeName + ".cpp"), BuildSource(headers), utf8);
        Console.WriteLine($"生成了 {_sourceDefinitions.Count} 个 C 式导出（{nativeDirectory}）。");

        // 对生成的 C 头文件再走一遍 C 导出流程；其中是 C++ 翻译单元，clang 参数仍由用户提供
        options.Header = [headerPath];
        new CExportGenerator(options)
        {
            TuFileName = "exports.cpp",
        }.Generate();
    }

    private static string NormalizePath(string path)
        => OperatingSystem.IsWindows()
            ? path.ToUpperInvariant()
            : path;

    private bool IsInHeaders(CXCursor cursor)
    {
        clang.getCursorLocation(cursor).GetExpansionLocation(out CXFile file, out _, out _, out _);
        string name = clang.getFileName(file).ToString();
        return name.Length is not 0
            && _headers.Contains(NormalizePath(Path.GetFullPath(name)));
    }

    private static bool IsPublic(CXCursor cursor)
        => clang.getCXXAccessSpecifier(cursor) is CX_CXXAccessSpecifier.CX_CXXPublic
            or CX_CXXAccessSpecifier.CX_CXXInvalidAccessSpecifier;

    private static string Spelling(CXCursor cursor)
        => clang.getCursorSpelling(cursor).ToString();

    private static string Usr(CXCursor cursor)
        => clang.getCursorUSR(cursor).ToString();

    private static bool IsAnonymous(CXCursor cursor)
    {
        string name = Spelling(cursor);
        return name.Length is 0
            || name.Contains("(anonymous")
            || name.Contains("(unnamed")
            || clang.Cursor_isAnonymous(cursor) is not 0;
    }

    // ---- 发现 ----

    private void Discover(List<CXCursor> cursors)
    {
        foreach (CXCursor cursor in cursors)
        {
            CXCursorKind kind = clang.getCursorKind(cursor);
            switch (kind)
            {
                case CXCursorKind.CXCursor_Namespace:
                case CXCursorKind.CXCursor_LinkageSpec:
                    Discover(CExportGenerator.ChildrenOf(cursor));
                    break;

                case CXCursorKind.CXCursor_ClassDecl:
                case CXCursorKind.CXCursor_StructDecl:
                    if (clang.isCursorDefinition(cursor) is 0
                        || !IsInHeaders(cursor))
                    {
                        break;
                    }

                    if (!IsAnonymous(cursor)
                        && QualifiedName(cursor) is var (cName, cppName)
                        && cName is not null)
                    {
                        _classes.TryAdd(Usr(cursor), new(cursor, cName, cppName!));
                    }

                    Discover([.. CExportGenerator.ChildrenOf(cursor).Where(IsPublic)]);
                    break;

                case CXCursorKind.CXCursor_EnumDecl:
                    if (clang.isCursorDefinition(cursor) is not 0
                        && IsInHeaders(cursor)
                        && !IsAnonymous(cursor)
                        && QualifiedName(cursor) is var (enumC, enumCpp)
                        && enumC is not null)
                    {
                        _enums.TryAdd(Usr(cursor), new(cursor, enumC, enumCpp!));
                    }

                    break;
            }
        }
    }

    // 模板与匿名命名空间中的类型无法稳定导出，返回 (null, null)
    private static (string? C, string? Cpp) QualifiedName(CXCursor cursor)
    {
        List<string> parts = [];
        CXCursor current = cursor;
        while (clang.Cursor_isNull(current) is 0
            && clang.getCursorKind(current) is not CXCursorKind.CXCursor_TranslationUnit)
        {
            CXCursorKind kind = clang.getCursorKind(current);
            string name = Spelling(current);
            switch (kind)
            {
                case CXCursorKind.CXCursor_Namespace when name.StartsWith("__"):
                    break;
                case CXCursorKind.CXCursor_Namespace when name.Length is 0:
                    return (null, null);
                case CXCursorKind.CXCursor_Namespace:
                case CXCursorKind.CXCursor_ClassDecl:
                case CXCursorKind.CXCursor_StructDecl:
                case CXCursorKind.CXCursor_EnumDecl:
                    parts.Insert(0, name);
                    break;
                case CXCursorKind.CXCursor_LinkageSpec:
                    break;
                default:
                    return (null, null);
            }

            current = clang.getCursorSemanticParent(current);
        }

        return (string.Join('_', parts), "::" + string.Join("::", parts));
    }

    // ---- 类型转换 ----

    private static string? ArithName(CXType type)
    {
        string spelled = clang.getTypeSpelling(type).ToString()
            .Replace("const ", "")
            .Replace("volatile ", "")
            .Replace("std::", "")
            .TrimStart(':')
            .Trim();
        if (WellKnownTypes.Contains(spelled))
            return spelled;

        return clang.getCanonicalType(type).kind switch
        {
            CXTypeKind.CXType_Bool => "bool",
            CXTypeKind.CXType_Char_S or CXTypeKind.CXType_Char_U => "char",
            CXTypeKind.CXType_SChar => "signed char",
            CXTypeKind.CXType_UChar => "unsigned char",
            CXTypeKind.CXType_Short => "short",
            CXTypeKind.CXType_UShort => "unsigned short",
            CXTypeKind.CXType_Int => "int",
            CXTypeKind.CXType_UInt => "unsigned int",
            CXTypeKind.CXType_Long => "long",
            CXTypeKind.CXType_ULong => "unsigned long",
            CXTypeKind.CXType_LongLong => "long long",
            CXTypeKind.CXType_ULongLong => "unsigned long long",
            CXTypeKind.CXType_Float => "float",
            CXTypeKind.CXType_Double => "double",
            _ => null,
        };
    }

    private static bool IsStdString(CXType canonical)
    {
        string text = clang.getTypeSpelling(canonical).ToString();
        return text.Contains("basic_string<char,") || text.Contains("basic_string<char>");
    }

    private static bool IsStdStringView(CXType canonical)
    {
        string text = clang.getTypeSpelling(canonical).ToString();
        return text.Contains("basic_string_view<char,") || text.Contains("basic_string_view<char>");
    }

    private static bool IsStdVector(CXType canonical)
    {
        string text = clang.getTypeSpelling(canonical).ToString();
        return (text.Contains("std::vector<") || text.Contains("std::__1::vector<"))
            && clang.Type_getNumTemplateArguments(canonical) > 0;
    }

    private ClassInfo? ClassOf(CXType canonical)
        => canonical.kind is CXTypeKind.CXType_Record
            && _classes.TryGetValue(Usr(clang.getTypeDeclaration(canonical)), out ClassInfo? info)
                ? info
                : null;

    private EnumInfo? EnumOf(CXType canonical)
        => canonical.kind is CXTypeKind.CXType_Enum
            && _enums.TryGetValue(Usr(clang.getTypeDeclaration(canonical)), out EnumInfo? info)
                ? info
                : null;

    private ParamPlan ConvertParameter(CXType type, string name)
    {
        CXType canonical = clang.getCanonicalType(type);
        switch (canonical.kind)
        {
            case CXTypeKind.CXType_RValueReference:
                throw new NotSupportedException("不支持右值引用");

            case CXTypeKind.CXType_LValueReference:
            {
                CXType pointee = clang.getPointeeType(canonical);
                CXType pointeeCanonical = clang.getCanonicalType(pointee);
                if (clang.isConstQualifiedType(pointee) is not 0)
                    return ConvertValue(pointee, name);

                if (ClassOf(pointeeCanonical) is { } info)
                {
                    _usedClasses.Add(Usr(info.Cursor));
                    return new([new(info.CName + "*", "void*", name)],
                        $"*static_cast<{info.CppName}*>({name})");
                }

                if (ArithName(pointee) is { } arith)
                    return new([new(arith + "*", arith + "*", name)], "*" + name);

                throw new NotSupportedException($"不支持的非 const 引用参数 {clang.getTypeSpelling(type)}");
            }

            case CXTypeKind.CXType_Pointer:
            {
                CXType pointee = clang.getPointeeType(canonical);
                CXType pointeeCanonical = clang.getCanonicalType(pointee);
                string qualifier = clang.isConstQualifiedType(pointee) is not 0 ? "const " : "";
                if (pointeeCanonical.kind is CXTypeKind.CXType_Void)
                    return new([new($"{qualifier}void*", $"{qualifier}void*", name)], name);

                if (ArithName(pointee) is { } arith)
                    return new([new($"{qualifier}{arith}*", $"{qualifier}{arith}*", name)], name);

                if (ClassOf(pointeeCanonical) is { } info)
                {
                    _usedClasses.Add(Usr(info.Cursor));
                    return new([new($"{qualifier}{info.CName}*", $"{qualifier}void*", name)],
                        $"static_cast<{qualifier}{info.CppName}*>({name})");
                }

                if (IsForeignQObject(pointeeCanonical))
                {
                    return new([new($"{qualifier}void*", $"{qualifier}void*", name)],
                        $"static_cast<{qualifier}{QObjectSpelling(pointeeCanonical)}*>({name})");
                }

                throw new NotSupportedException($"不支持的指针参数 {clang.getTypeSpelling(type)}");
            }

            default:
                return ConvertValue(canonical, name);
        }
    }

    private ParamPlan ConvertValue(CXType canonical, string name)
    {
        if (options.Qt && IsQString(canonical))
            return new([new("const char*", "const char*", name)], $"QString::fromUtf8({name})");

        if (IsStdStringView(canonical))
            return new([new("const char*", "const char*", name)], $"cppm::from_c_view({name})");

        if (IsStdString(canonical))
            return new([new("const char*", "const char*", name)], $"cppm::from_c_string({name})");

        if (IsStdVector(canonical))
        {
            string element = ArithName(clang.Type_getTemplateArgumentAsType(canonical, 0)) is { } arith and not "bool"
                ? arith
                : throw new NotSupportedException("仅支持元素为 C 基本类型的 std::vector");
            string countName = name + "_count";
            return new(
                [new($"const {element}*", $"const {element}*", name), new("size_t", "size_t", countName)],
                $"cppm::to_vector<{element}>({name}, {countName})");
        }

        if (ClassOf(canonical) is { } info)
        {
            _usedClasses.Add(Usr(info.Cursor));
            return new([new($"const {info.CName}*", "const void*", name)],
                $"*static_cast<const {info.CppName}*>({name})");
        }

        if (EnumOf(canonical) is { } enumInfo)
            return new([new(enumInfo.CName, "int", name)], $"static_cast<{enumInfo.CppName}>({name})");

        if (ArithName(canonical) is { } scalar)
            return new([new(scalar, scalar, name)], name);

        throw new NotSupportedException($"不支持的参数类型 {clang.getTypeSpelling(canonical)}");
    }

    private ReturnPlan ConvertReturn(CXType type)
    {
        CXType canonical = clang.getCanonicalType(type);
        switch (canonical.kind)
        {
            case CXTypeKind.CXType_Void:
                return new("void", "void", x => x);

            case CXTypeKind.CXType_RValueReference:
                throw new NotSupportedException("不支持右值引用返回值");

            case CXTypeKind.CXType_LValueReference:
            {
                CXType pointeeCanonical = clang.getCanonicalType(clang.getPointeeType(canonical));
                if (ClassOf(pointeeCanonical) is { } info)
                {
                    _usedClasses.Add(Usr(info.Cursor));
                    return new("void*", "void*", x => $"cppm::ref_to_void({x})");
                }

                return ConvertReturn(pointeeCanonical);
            }

            case CXTypeKind.CXType_Pointer:
            {
                CXType pointee = clang.getPointeeType(canonical);
                CXType pointeeCanonical = clang.getCanonicalType(pointee);
                string qualifier = clang.isConstQualifiedType(pointee) is not 0 ? "const " : "";
                if (pointeeCanonical.kind is CXTypeKind.CXType_Void)
                    return new($"{qualifier}void*", $"{qualifier}void*", x => x);

                if (ArithName(pointee) is { } arith)
                    return new($"{qualifier}{arith}*", $"{qualifier}{arith}*", x => x);

                if (ClassOf(pointeeCanonical) is { } info)
                {
                    _usedClasses.Add(Usr(info.Cursor));
                    return new("void*", "void*", x => $"cppm::ptr_to_void({x})");
                }

                if (IsForeignQObject(pointeeCanonical))
                    return new("void*", "void*", x => $"cppm::ptr_to_void({x})");

                throw new NotSupportedException($"不支持的指针返回值 {clang.getTypeSpelling(type)}");
            }
        }

        if (options.Qt && IsQString(canonical))
            return new("const char*", "char*", x => $"cppm::dup_string(({x}).toUtf8().constData())");

        if (IsStdString(canonical)
            || IsStdStringView(canonical))
        {
            return new("const char*", "char*", x => $"cppm::dup_string({x})");
        }

        if (IsStdVector(canonical))
        {
            string element = ArithName(clang.Type_getTemplateArgumentAsType(canonical, 0)) is { } arith and not "bool"
                ? arith
                : throw new NotSupportedException("仅支持元素为 C 基本类型的 std::vector");
            return new($"{element}*", $"{element}*", x => $"cppm::to_c_array({x}, {CountName})", NeedsCount: true);
        }

        if (ClassOf(canonical) is { } owned)
        {
            _usedClasses.Add(Usr(owned.Cursor));
            return new(owned.CName + "*", "void*", x => $"cppm::to_owned({x})");
        }

        if (EnumOf(canonical) is { } enumInfo)
            return new(enumInfo.CName, "int", x => $"static_cast<int>({x})");

        if (ArithName(canonical) is { } scalar)
            return new(scalar, scalar, x => x);

        throw new NotSupportedException($"不支持的返回类型 {clang.getTypeSpelling(canonical)}");
    }

    // ---- 绑定 ----

    private string UniqueName(string name)
    {
        string candidate = name;
        for (int i = 2; !_cNames.Add(candidate); i++)
            candidate = $"{name}_{i}";

        return candidate;
    }

    private static string ParameterName(CXCursor argument, int index, HashSet<string> used)
    {
        string name = Spelling(argument);
        if (name.Length is 0)
            name = $"arg{index}";

        while (name is SelfName or CountName
            || !used.Add(name))
        {
            name += "_";
        }

        return name;
    }

    private void BindClass(ClassInfo info)
    {
        CXCursor cursor = info.Cursor;
        List<CXCursor> children = CExportGenerator.ChildrenOf(cursor);
        string selfType = info.CppName;

        bool isAbstract = clang.CXXRecord_isAbstract(cursor) is not 0;
        bool hasUserConstructor = false;
        bool destructorExported = true;
        foreach (CXCursor child in children)
        {
            switch (clang.getCursorKind(child))
            {
                case CXCursorKind.CXCursor_Constructor:
                    hasUserConstructor = true;
                    if (!isAbstract
                        && IsPublic(child)
                        && clang.CXXMethod_isDeleted(child) is 0)
                    {
                        TryBind(info, "create", () => BindConstructor(info, child));
                    }

                    break;

                case CXCursorKind.CXCursor_Destructor:
                    destructorExported = IsPublic(child)
                        && clang.CXXMethod_isDeleted(child) is 0;
                    break;
            }
        }

        if (!hasUserConstructor
            && !isAbstract)
        {
            TryBind(info, "create", () => Add(
                UniqueName(info.CName + "_create"),
                new(info.CName + "*", "void*", x => x),
                [],
                $"cppm::make_default<{selfType}>()"));
        }

        foreach (CXCursor child in children.Where(c => clang.getCursorKind(c) is CXCursorKind.CXCursor_FieldDecl
            && IsPublic(c)
            && !(options.Qt && Spelling(c) is "staticMetaObject")))
        {
            string fieldName = Spelling(child);
            string snake = fieldName.Underscore();
            CXType fieldType = clang.getCursorType(child);
            TryBind(info, "get_" + snake, () => BindFieldGetter(info, fieldName, fieldType, snake));
            if (clang.isConstQualifiedType(fieldType) is 0)
                TryBind(info, "set_" + snake, () => BindFieldSetter(info, fieldName, fieldType, snake));
        }

        HashSet<string> seen = [];
        foreach ((CXCursor method, bool isSignal) in PublicMethods(cursor))
        {
            string name = Spelling(method);
            string key = name + "(" + string.Join(",", Enumerable.Range(0, clang.Cursor_getNumArguments(method))
                .Select(i => clang.getTypeSpelling(clang.getCanonicalType(
                    clang.getCursorType(clang.Cursor_getArgument(method, (uint)i)))).ToString())) + ")";
            if (!seen.Add(key))
                continue;

            if (isSignal)
                TryBind(info, "信号 " + name, () => BindSignal(info, method, name));
            else
                TryBind(info, name.Underscore(), () => BindMethod(info, method, name));
        }

        if (destructorExported)
        {
            // QObject 可能正在事件循环中使用，必须延迟销毁
            string release = options.Qt && IsQObject(cursor)
                ? $"static_cast<{selfType}*>({SelfName})->deleteLater()"
                : $"delete static_cast<{selfType}*>({SelfName})";
            TryBind(info, "destroy", () => Add(
                UniqueName(info.CName + "_destroy"),
                new("void", "void", x => x),
                [new(info.CName + "*", "void*", SelfName)],
                release,
                isStatement: true));
        }
    }

    private IEnumerable<(CXCursor Method, bool IsSignal)> PublicMethods(CXCursor classCursor)
    {
        bool inSignals = false;
        foreach (CXCursor child in CExportGenerator.ChildrenOf(classCursor))
        {
            switch (clang.getCursorKind(child))
            {
                case CXCursorKind.CXCursor_CXXAccessSpecifier when options.Qt:
                    inSignals = IsSignalsSpecifier(child);
                    break;

                case CXCursorKind.CXCursor_CXXMethod
                    when IsPublic(child)
                        && clang.CXXMethod_isDeleted(child) is 0
                        && !Spelling(child).StartsWith("operator")
                        && !(options.Qt && QtMetaMethods.Contains(Spelling(child))):
                    yield return (child, inSignals);
                    break;

                case CXCursorKind.CXCursor_CXXBaseSpecifier when IsPublic(child):
                    CXCursor baseDeclaration = clang.getTypeDeclaration(clang.getCursorType(child));
                    if (_classes.ContainsKey(Usr(baseDeclaration)))
                    {
                        foreach (var inherited in PublicMethods(baseDeclaration))
                            yield return inherited;
                    }

                    break;
            }
        }
    }

    // ---- Qt ----

    // Q_OBJECT 宏注入的元对象成员，不应导出
    private static readonly HashSet<string> QtMetaMethods =
    [
        "metaObject", "qt_metacast", "qt_metacall", "tr", "trUtf8", "qt_check_for_QGADGET_macro",
    ];

    private static readonly Regex SignalsBlockRegex =
        new(@"^\s*(Q_SIGNALS|signals)\s*:", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly Dictionary<string, string[]> _sourceLines = [];
    private bool _connectionUsed;

    private static bool IsQString(CXType canonical)
        => clang.getTypeSpelling(canonical).ToString().Replace("const ", "").Trim() is "QString";

    // 未被发现（不在 --header 中）的 QObject 派生类，如 QObject 本身，按不透明指针传递
    private bool IsForeignQObject(CXType canonical)
        => options.Qt
            && canonical.kind is CXTypeKind.CXType_Record
            && ClassOf(canonical) is null
            && IsQObject(clang.getTypeDeclaration(canonical));

    private static string QObjectSpelling(CXType canonical)
        => clang.getTypeSpelling(canonical).ToString().Replace("const ", "").Trim();

    private static bool IsQObject(CXCursor classCursor)
    {
        if (Spelling(classCursor) is "QObject")
            return true;

        foreach (CXCursor child in CExportGenerator.ChildrenOf(classCursor))
        {
            if (clang.getCursorKind(child) is CXCursorKind.CXCursor_CXXBaseSpecifier
                && IsQObject(clang.getTypeDeclaration(clang.getCursorType(child))))
            {
                return true;
            }
        }

        return false;
    }

    // 访问说明符带 qt_signal 注解（需要用户通过 -DQT_ANNOTATE_ACCESS_SPECIFIER 传入），
    // 或其源码行是 signals: / Q_SIGNALS:
    private bool IsSignalsSpecifier(CXCursor specifier)
    {
        if (CExportGenerator.ChildrenOf(specifier).Any(child =>
            clang.getCursorKind(child) is CXCursorKind.CXCursor_AnnotateAttr
            && Spelling(child) is "qt_signal"))
        {
            return true;
        }

        clang.getCursorLocation(specifier).GetExpansionLocation(out CXFile file, out uint line, out _, out _);
        string path = clang.getFileName(file).ToString();
        if (path.Length is 0)
            return false;

        if (!_sourceLines.TryGetValue(path, out string[]? lines))
        {
            lines = File.Exists(path) ? File.ReadAllLines(path) : [];
            _sourceLines[path] = lines;
        }

        return line > 0
            && line <= lines.Length
            && SignalsBlockRegex.IsMatch(lines[line - 1]);
    }

    // 回调参数只在调用期间有效，字符串以借用指针传出
    private (string H, string C, string Expression) ConvertCallbackArgument(CXType type, string name)
    {
        CXType canonical = clang.getCanonicalType(type);
        if (canonical.kind is CXTypeKind.CXType_RValueReference)
            throw new NotSupportedException("不支持右值引用信号参数");

        if (canonical.kind is CXTypeKind.CXType_LValueReference)
        {
            CXType pointee = clang.getPointeeType(canonical);
            if (clang.isConstQualifiedType(pointee) is 0
                && ClassOf(clang.getCanonicalType(pointee)) is null)
            {
                throw new NotSupportedException($"不支持的非 const 引用信号参数 {clang.getTypeSpelling(type)}");
            }

            canonical = clang.getCanonicalType(pointee);
            if (ClassOf(canonical) is { } referenced)
            {
                _usedClasses.Add(Usr(referenced.Cursor));
                return (referenced.CName + "*", "void*", $"cppm::ref_to_void(const_cast<{referenced.CppName}&>({name}))");
            }
        }

        if (IsQString(canonical))
            return ("const char*", "const char*", $"{name}.toUtf8().constData()");

        if (IsStdString(canonical)
            || IsStdStringView(canonical))
        {
            return ("const char*", "const char*", $"std::string({name}).c_str()");
        }

        if (canonical.kind is CXTypeKind.CXType_Pointer)
        {
            CXType pointee = clang.getPointeeType(canonical);
            CXType pointeeCanonical = clang.getCanonicalType(pointee);
            string qualifier = clang.isConstQualifiedType(pointee) is not 0 ? "const " : "";
            if (pointeeCanonical.kind is CXTypeKind.CXType_Void)
                return ($"{qualifier}void*", $"{qualifier}void*", name);

            if (ArithName(pointee) is { } arith)
                return ($"{qualifier}{arith}*", $"{qualifier}{arith}*", name);

            if (ClassOf(pointeeCanonical) is { } pointed)
            {
                _usedClasses.Add(Usr(pointed.Cursor));
                return (pointed.CName + "*", "void*", $"cppm::ptr_to_void(const_cast<{pointed.CppName}*>({name}))");
            }

            if (IsForeignQObject(pointeeCanonical))
                return ("void*", "void*", $"cppm::ptr_to_void(const_cast<{QObjectSpelling(pointeeCanonical)}*>({name}))");

            throw new NotSupportedException($"不支持的指针信号参数 {clang.getTypeSpelling(type)}");
        }

        if (EnumOf(canonical) is { } enumInfo)
            return (enumInfo.CName, "int", $"static_cast<int>({name})");

        if (ArithName(canonical) is { } scalar)
            return (scalar, scalar, name);

        throw new NotSupportedException($"不支持的信号参数类型 {clang.getTypeSpelling(type)}");
    }

    private void BindSignal(ClassInfo info, CXCursor signal, string name)
    {
        string snake = name.Underscore();
        string callbackName = UniqueName($"{info.CName}_{snake}_callback");
        List<string> hTypes = ["void*"];
        List<string> cTypes = ["void*"];
        List<string> lambdaParameters = [];
        List<string> callArguments = ["user"];
        List<string> cppTypes = [];

        int count = clang.Cursor_getNumArguments(signal);
        for (int i = 0; i < count; i++)
        {
            CXType type = clang.getCursorType(clang.Cursor_getArgument(signal, (uint)i));
            var (h, c, expression) = ConvertCallbackArgument(type, $"p{i}");
            string cppType = clang.getTypeSpelling(type).ToString();
            hTypes.Add(h);
            cTypes.Add(c);
            cppTypes.Add(cppType);
            lambdaParameters.Add($"{cppType} p{i}");
            callArguments.Add(expression);
        }

        CXCursor declaring = clang.getCursorSemanticParent(signal);
        string declaringName = QualifiedName(declaring).Cpp ?? info.CppName;
        bool overloaded = CExportGenerator.ChildrenOf(declaring).Count(c =>
            clang.getCursorKind(c) is CXCursorKind.CXCursor_CXXMethod
            && Spelling(c) == name) > 1;
        string pointer = overloaded
            ? $"static_cast<void ({declaringName}::*)({string.Join(", ", cppTypes)})>(&{declaringName}::{name})"
            : $"&{declaringName}::{name}";

        _headerDeclarations.Add($"typedef void (CPPM_CALL *{callbackName})({string.Join(", ", hTypes)});");
        _sourceTypedefs.Add($"typedef void (CPPM_CALL *{callbackName})({string.Join(", ", cTypes)});");
        _connectionUsed = true;

        string sender = $"static_cast<{info.CppName}*>({SelfName})";
        Add(
            UniqueName($"{info.CName}_connect_{snake}"),
            new("cppm_qt_connection*", "void*", x => x),
            [
                new(info.CName + "*", "void*", SelfName),
                new(callbackName, callbackName, "callback"),
                new("void*", "void*", "user"),
            ],
            $"new QMetaObject::Connection(QObject::connect({sender}, {pointer}, {sender}, [callback, user]({string.Join(", ", lambdaParameters)}) {{ callback({string.Join(", ", callArguments)}); }}))");
    }

    private void BindConnectionDestroy()
        => Add(
            UniqueName("cppm_qt_connection_destroy"),
            new("void", "void", x => x),
            [new("cppm_qt_connection*", "void*", SelfName)],
            $"[&] {{ auto* connection = static_cast<QMetaObject::Connection*>({SelfName}); QObject::disconnect(*connection); delete connection; }}()",
            isStatement: true);

    private void TryBind(ClassInfo info, string description, Action bind)
    {
        try
        {
            bind();
        }
        catch (NotSupportedException ex)
        {
            Console.Error.WriteLine($"跳过 {info.CppName} 的 {description}：{ex.Message}");
        }
    }

    private List<Param> ConvertArguments(CXCursor function, List<Param> parameters, List<string> arguments)
    {
        HashSet<string> used = [];
        int count = clang.Cursor_getNumArguments(function);
        for (int i = 0; i < count; i++)
        {
            CXCursor argument = clang.Cursor_getArgument(function, (uint)i);
            ParamPlan plan = ConvertParameter(clang.getCursorType(argument), ParameterName(argument, i, used));
            parameters.AddRange(plan.Params);
            arguments.Add(plan.Argument);
        }

        return parameters;
    }

    private void BindConstructor(ClassInfo info, CXCursor constructor)
    {
        List<Param> parameters = [];
        List<string> arguments = [];
        ConvertArguments(constructor, parameters, arguments);
        Add(
            UniqueName(info.CName + "_create"),
            new(info.CName + "*", "void*", x => x),
            parameters,
            $"new {info.CppName}({string.Join(", ", arguments)})");
    }

    private void BindMethod(ClassInfo info, CXCursor method, string name)
    {
        ReturnPlan returnPlan = ConvertReturn(clang.getCursorResultType(method));
        bool isStatic = clang.CXXMethod_isStatic(method) is not 0;
        bool isConst = clang.CXXMethod_isConst(method) is not 0;

        List<Param> parameters = [];
        List<string> arguments = [];
        string qualifier = isConst ? "const " : "";
        if (!isStatic)
            parameters.Add(new($"{qualifier}{info.CName}*", $"{qualifier}void*", SelfName));

        ConvertArguments(method, parameters, arguments);
        string target = isStatic
            ? $"{info.CppName}::{name}"
            : $"static_cast<{qualifier}{info.CppName}*>({SelfName})->{name}";
        Add(
            UniqueName($"{info.CName}_{name.Underscore()}"),
            returnPlan,
            parameters,
            $"{target}({string.Join(", ", arguments)})");
    }

    private void BindFieldGetter(ClassInfo info, string field, CXType type, string snake)
    {
        ReturnPlan returnPlan = ConvertReturn(type);
        Add(
            UniqueName($"{info.CName}_get_{snake}"),
            returnPlan,
            [new($"const {info.CName}*", "const void*", SelfName)],
            $"static_cast<const {info.CppName}*>({SelfName})->{field}");
    }

    private void BindFieldSetter(ClassInfo info, string field, CXType type, string snake)
    {
        ParamPlan plan = ConvertParameter(type, "value");
        List<Param> parameters = [new($"{info.CName}*", "void*", SelfName), .. plan.Params];
        Add(
            UniqueName($"{info.CName}_set_{snake}"),
            new("void", "void", x => x),
            parameters,
            $"static_cast<{info.CppName}*>({SelfName})->{field} = {plan.Argument}",
            isStatement: true);
    }

    private void Add(string cName, ReturnPlan returnPlan, List<Param> parameters, string expression, bool isStatement = false)
    {
        List<Param> all = [.. parameters];
        if (returnPlan.NeedsCount)
            all.Add(new("size_t*", "size_t*", CountName));

        string hParameters = all.Count is 0
            ? "void"
            : string.Join(", ", all.Select(p => $"{p.H} {p.Name}"));
        string cParameters = all.Count is 0
            ? ""
            : string.Join(", ", all.Select(p => $"{p.C} {p.Name}"));

        string body = (returnPlan.C is "void" || isStatement)
            ? $"{expression};"
            : $"return {returnPlan.Wrap(expression)};";

        _headerDeclarations.Add($"{returnPlan.H} CPPM_CALL {cName}({hParameters});");
        _sourceDefinitions.Add(Lf($$"""
            CPPM_EXPORT {{returnPlan.C}} CPPM_CALL {{cName}}({{cParameters}})
            {
                {{body}}
            }
            """));
    }

    // 源文件可能是 CRLF，原始字符串中的换行随之变化，统一规整为 LF
    private static string Lf(string text)
        => text.ReplaceLineEndings("\n");

    // ---- 输出 ----

    private string BuildHeader()
    {
        string classes = string.Join("\n", _classes.Values
            .Where(c => _usedClasses.Contains(Usr(c.Cursor)))
            .OrderBy(c => c.CName, StringComparer.Ordinal)
            .Select(c => $"typedef struct {c.CName} {c.CName};"));
        if (_connectionUsed)
            classes += "\ntypedef struct cppm_qt_connection cppm_qt_connection;";

        string enums = string.Join("\n\n", _enums.Values
            .OrderBy(e => e.CName, StringComparer.Ordinal)
            .Select(EnumDeclaration));

        string declarations = string.Join("\n", _headerDeclarations);

        return Lf($$"""
            // 由 OBSharp.Interop.NativeBindGenerator --cpp-redirect 生成，请勿手工修改。
            #pragma once

            #include <stddef.h>
            #include <stdint.h>
            #include <stdbool.h>

            #ifndef CPPM_CALL
            #if defined(_WIN32)
            #define CPPM_CALL __cdecl
            #else
            #define CPPM_CALL
            #endif
            #endif

            #ifdef __cplusplus
            extern "C" {
            #endif

            {{classes}}

            {{enums}}

            void CPPM_CALL cppm_free_memory(void* pointer);
            {{declarations}}

            #ifdef __cplusplus
            }
            #endif

            """);
    }

    private static string EnumDeclaration(EnumInfo info)
    {
        string constants = string.Join("\n", CExportGenerator.ChildrenOf(info.Cursor)
            .Where(c => clang.getCursorKind(c) is CXCursorKind.CXCursor_EnumConstantDecl)
            .Select(c => $"    {info.CName}_{Spelling(c)} = {clang.getEnumConstantDeclValue(c)},"));

        return $$"""
            typedef enum {{info.CName}}
            {
            {{constants}}
            } {{info.CName}};
            """;
    }

    private string BuildSource(string[] headers)
    {
        string includes = string.Join("\n", headers.Select(h => $"#include \"{h.Replace('\\', '/')}\""));
        string definitions = string.Join("\n\n", _sourceDefinitions);
        string typedefs = string.Join("\n", _sourceTypedefs);

        return Lf($$"""
            // 由 OBSharp.Interop.NativeBindGenerator --cpp-redirect 生成，请勿手工修改。
            #include "{{CppMarshaller.FileName}}"
            {{includes}}

            {{typedefs}}

            CPPM_EXPORT void CPPM_CALL cppm_free_memory(void* pointer)
            {
                cppm::release(pointer);
            }

            {{definitions}}

            """);
    }
}
