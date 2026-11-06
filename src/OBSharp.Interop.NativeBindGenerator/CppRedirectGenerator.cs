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

    private sealed record OpaquePointerInfo(
        string CName,
        string CppName,
        string ManagedType,
        string DeclarationKey,
        string Declaration);

    private sealed record Param(string H, string C, string Name);

    private sealed record ParamPlan(List<Param> Params, string Argument);

    private sealed record ReturnPlan(
        string H,
        string C,
        Func<string, string> Wrap,
        bool NeedsCount = false,
        string? CountParameterName = null);

    private const string SelfName = "self";
    private const string CountName = "cppm_count";
    private const string QtCountName = CExportGenerator.QtCountName;
    private const string QtArraySuffix = CExportGenerator.QtArraySuffix;
    private const string QtArrayCountSuffix = "__cppm_qt_count";

    private string FreeMemoryFunctionName
        => NativeIdentifier(options.NativeName) + "_cppm_free_memory";

    private string ConnectionTypeName
        => NativeIdentifier(options.NativeName) + "_cppm_qt_connection";

    private string ConnectionDestroyFunctionName
        => NativeIdentifier(options.NativeName) + "_cppm_qt_connection_destroy";

    private static readonly HashSet<string> WellKnownTypes =
    [
        "size_t", "ptrdiff_t", "ssize_t", "intptr_t", "uintptr_t", "intmax_t", "uintmax_t",
        "int8_t", "int16_t", "int32_t", "int64_t", "uint8_t", "uint16_t", "uint32_t", "uint64_t",
    ];

    /// <summary>
    /// 公共声明（桥接类型、枚举、不透明指针、free_memory、Qt 连接销毁）所属的分组键；
    /// 其余分组键为类的 C 名，每个键对应一对拆分的 <c>.h</c>/<c>.cpp</c>。
    /// </summary>
    private const string CommonOwner = "";

    /// <summary>
    /// 按所属类归集的导出声明与定义，避免把全部导出堆进一个巨大的翻译单元。
    /// </summary>
    private sealed class ExportGroup
    {
        public List<string> HeaderTypedefs { get; } = [];

        public List<string> SourceTypedefs { get; } = [];

        public List<string> HeaderDeclarations { get; } = [];

        public List<string> SourceDefinitions { get; } = [];

        public int Count => SourceDefinitions.Count;
    }

    private readonly Dictionary<string, ClassInfo> _classes = [];
    private readonly Dictionary<string, EnumInfo> _enums = [];
    private readonly Dictionary<string, OpaquePointerInfo> _opaquePointers = [];
    private readonly HashSet<string> _opaqueManagedNames = [];
    private readonly Dictionary<string, string> _opaqueTemplateNames = [];
    private readonly HashSet<string> _usedClasses = [];
    private readonly HashSet<string> _cNames = [];
    private readonly Dictionary<string, ExportGroup> _groups = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _originalSignatures = new(StringComparer.Ordinal);
    private HashSet<string> _headers = [];
    private string _currentOwner = CommonOwner;
    private int _sourceDefinitionCount;

    /// <summary>
    /// 生成的原生文件：相对 native 产物目录的路径与内容。
    /// </summary>
    private sealed record NativeFile(string RelativePath, string Content);

    private readonly List<NativeFile> _nativeFiles = [];

    /// <summary>
    /// 交给 <see cref="CExportGenerator"/> 重新解析的全部生成头文件（绝对路径）。
    /// </summary>
    private readonly List<string> _generatedHeaders = [];

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
                        int before = _sourceDefinitionCount;
                        BindClass(info);
                        if (_sourceDefinitionCount > before)
                            _usedClasses.Add(Usr(info.Cursor));
                    }

                    if (_connectionUsed)
                        BindConnectionDestroy();

                    // 头文件文本依赖仍存活的游标，必须在翻译单元释放之前生成
                    BuildNativeFiles(headers);
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

        if (_sourceDefinitionCount is 0)
            throw new InvalidOperationException("指定头文件中没有找到可转发的 C++ 类");

        string nativeDirectory = Path.GetFullPath(options.NativeOutput);
        Directory.CreateDirectory(nativeDirectory);
        RemoveStaleNativeFiles(nativeDirectory);
        // 一律 UTF-8 无 BOM；用 MSVC 编译时请加 /utf-8
        UTF8Encoding utf8 = new(false);
        foreach (NativeFile file in _nativeFiles)
        {
            string path = Path.Combine(nativeDirectory, file.RelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, file.Content, utf8);
        }

        WriteManifest(nativeDirectory);

        Console.WriteLine(
            $"生成了 {_sourceDefinitionCount} 个 C 式导出、{_nativeFiles.Count} 个 native 文件（{nativeDirectory}）。");

        // 对生成的 C 头文件再走一遍 C 导出流程；其中是 C++ 翻译单元，clang 参数仍由用户提供
        options.Header = [.. _generatedHeaders];
        new CExportGenerator(options)
        {
            TuFileName = "exports.cpp",
            UseQtArrays = options.Qt,
            OriginalSignatures = _originalSignatures,
            FreeMemoryFunctionName = FreeMemoryFunctionName,
            OpaquePointerTypes = _opaquePointers.Values.ToDictionary(
                pointer => pointer.CName,
                pointer => (pointer.ManagedType, pointer.CppName, pointer.DeclarationKey, pointer.Declaration),
                StringComparer.Ordinal),
        }.Generate();
    }

    /// <summary>
    /// 记录本次生成的文件清单的文件名（放在 native 产物目录内）。
    /// </summary>
    private const string ManifestFileName = "native_manifest.txt";

    /// <summary>
    /// 写入生成清单，供下次运行精确清理旧产物；不使用通配删除，避免误删目录中用户自己的文件。
    /// </summary>
    private void WriteManifest(string nativeDirectory)
    {
        string[] relativePaths = [.. _nativeFiles
            .Select(file => file.RelativePath)
            .Append(ManifestFileName)
            .OrderBy(path => path, StringComparer.Ordinal)];
        File.WriteAllLines(
            Path.Combine(nativeDirectory, ManifestFileName),
            relativePaths,
            new UTF8Encoding(false));
    }

    /// <summary>
    /// 按上次的清单删除本次不再产生的生成文件，避免类改名或删除后残留旧文件继续参与编译。
    /// 只删除清单里列出的、位于输出目录内的文件。
    /// </summary>
    private static void RemoveStaleNativeFiles(string nativeDirectory)
    {
        string manifestPath = Path.Combine(nativeDirectory, ManifestFileName);
        if (!File.Exists(manifestPath))
            return;

        string root = Path.GetFullPath(nativeDirectory);
        foreach (string line in File.ReadAllLines(manifestPath))
        {
            string relative = line.Trim();
            if (relative.Length is 0)
                continue;

            string candidate = Path.GetFullPath(Path.Combine(root, relative));
            // 防御清单被篡改：只处理输出目录内的文件
            if (!candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                continue;

            if (File.Exists(candidate))
                File.Delete(candidate);
        }
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
    {
        CX_CXXAccessSpecifier access = clang.getCXXAccessSpecifier(cursor);
        if (access is CX_CXXAccessSpecifier.CX_CXXPublic)
            return true;
        if (access is not CX_CXXAccessSpecifier.CX_CXXInvalidAccessSpecifier)
            return false;

        CXCursor parent = clang.getCursorSemanticParent(cursor);
        return clang.getCursorKind(parent) is CXCursorKind.CXCursor_StructDecl
            or CXCursorKind.CXCursor_UnionDecl;
    }

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
                    if (IsWellKnownValueType(cursor)
                        || clang.isCursorDefinition(cursor) is 0
                        || !IsInHeaders(cursor)
                        || HasTemplateArguments(cursor)
                        || !IsVisibleNestedType(cursor)
                        || !IsQtClassBindable(cursor))
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
                        && IsVisibleNestedType(cursor)
                        && QualifiedName(cursor) is var (enumC, enumCpp)
                        && enumC is not null)
                    {
                        _enums.TryAdd(Usr(cursor), new(cursor, enumC, enumCpp!));
                    }

                    break;
            }
        }
    }

    /// <summary>
    /// Windows 的 <c>_GUID</c> 与 Qt 的 <c>qfloat16</c> 分别有标准托管对应物 <c>Guid</c>、<c>Half</c>，
    /// 以位模式桥接类型按值传递，而不是生成不透明的 SafeHandle 类。
    /// </summary>
    private static bool IsWellKnownValueType(CXCursor cursor)
        => Spelling(cursor) is "_GUID" or "qfloat16";

    private static bool IsVisibleNestedType(CXCursor cursor)
    {
        CXCursor parent = clang.getCursorSemanticParent(cursor);
        CXCursorKind parentKind = clang.getCursorKind(parent);
        return parentKind is not (CXCursorKind.CXCursor_ClassDecl or CXCursorKind.CXCursor_StructDecl)
            || IsPublic(cursor);
    }

    private bool IsQtClassBindable(CXCursor cursor)
    {
        if (!options.Qt)
            return true;

        List<CXCursor> members = CExportGenerator.ChildrenOf(cursor);
        if (HasDllImportOrExport(cursor)
            || members.Any(HasDllImportOrExport))
        {
            return true;
        }

        return members
            .Where(member => clang.getCursorKind(member) is CXCursorKind.CXCursor_CXXMethod
                or CXCursorKind.CXCursor_Constructor
                or CXCursorKind.CXCursor_Destructor)
            .All(member => clang.Cursor_isFunctionInlined(member) is not 0);
    }

    private bool IsQtMemberBindable(CXCursor member)
    {
        if (!options.Qt)
            return true;

        CXCursor owner = clang.getCursorSemanticParent(member);
        bool isImported = HasDllImportOrExport(owner)
            || HasDllImportOrExport(member);
        if (isImported
            && clang.getCursorKind(member) is CXCursorKind.CXCursor_Constructor
            && clang.Cursor_isFunctionInlined(member) is not 0)
        {
            return false;
        }

        return isImported
            || clang.Cursor_isFunctionInlined(member) is not 0;
    }

    private static bool HasDllImportOrExport(CXCursor cursor)
        => CExportGenerator.ChildrenOf(cursor).Any(child =>
            clang.getCursorKind(child) is CXCursorKind.CXCursor_DLLImport
                or CXCursorKind.CXCursor_DLLExport);

    private static bool HasTemplateArguments(CXCursor cursor)
    {
        CXType type = clang.getCanonicalType(clang.getCursorType(cursor));
        return clang.Type_getNumTemplateArguments(type) > 0;
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
                case CXCursorKind.CXCursor_Namespace when name.StartsWith("__", StringComparison.Ordinal):
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

    private static string CallbackCppType(CXType type)
    {
        CXType canonical = clang.getCanonicalType(type);
        if (canonical.kind is CXTypeKind.CXType_Enum)
            return QualifiedName(clang.getTypeDeclaration(canonical)).Cpp
                ?? clang.getTypeSpelling(type).ToString();

        return clang.getTypeSpelling(type).ToString();
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

    private static bool IsQtList(CXType canonical)
    {
        string spelling = clang.getTypeSpelling(canonical).ToString();
        while (spelling.StartsWith("const ", StringComparison.Ordinal)
            || spelling.StartsWith("volatile ", StringComparison.Ordinal))
        {
            int qualifierLength = spelling.StartsWith("const ", StringComparison.Ordinal) ? 6 : 9;
            spelling = spelling[qualifierLength..].TrimStart();
        }

        if (spelling.StartsWith("class ", StringComparison.Ordinal)
            || spelling.StartsWith("struct ", StringComparison.Ordinal))
        {
            spelling = spelling[(spelling.IndexOf(' ') + 1)..];
        }

        return (spelling.StartsWith("QList<", StringComparison.Ordinal)
                || spelling.StartsWith("::QList<", StringComparison.Ordinal))
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

    private OpaquePointerInfo OpaquePointerOf(CXType canonical)
    {
        CXCursor declaration = clang.getTypeDeclaration(canonical);
        string usr = Usr(declaration);
        if (_opaquePointers.TryGetValue(usr, out OpaquePointerInfo? existing))
            return existing;

        string cppName = clang.getTypeSpelling(canonical).ToString().Trim();
        while (cppName.StartsWith("const ", StringComparison.Ordinal)
            || cppName.StartsWith("volatile ", StringComparison.Ordinal))
        {
            int qualifierLength = cppName.StartsWith("const ", StringComparison.Ordinal) ? 6 : 9;
            cppName = cppName[qualifierLength..].TrimStart();
        }
        int templateStart = cppName.IndexOf('<');
        if (templateStart is not -1)
        {
            string cppTemplateName = cppName[..templateStart].Trim();
            string managedTemplateName = OpaqueTemplateName(cppTemplateName);
            int argumentCount = clang.Type_getNumTemplateArguments(canonical);
            List<string> managedArguments = [];
            if (argumentCount > 0)
            {
                for (int i = 0; i < argumentCount; i++)
                    managedArguments.Add(OpaqueTemplateArgument(clang.Type_getTemplateArgumentAsType(canonical, (uint)i)));
            }
            else
            {
                managedArguments.AddRange(TemplateArguments(cppName).Select(OpaqueTemplateArgument));
            }

            if (managedArguments.Count is 0)
                managedArguments.Add("nint");

            int managedArgumentCount = managedArguments.Count;
            string managedType = $"{managedTemplateName}<{string.Join(", ", managedArguments)}>";
            string typeParameters = managedArgumentCount is 1
                ? "T"
                : string.Join(", ", Enumerable.Range(1, managedArgumentCount).Select(index => $"T{index}"));
            string declarationKey = $"{managedTemplateName}<{typeParameters}>";
            string constraints = string.Join(" ", Enumerable.Range(1, managedArgumentCount)
                .Select(index => $"where {(managedArgumentCount is 1 ? "T" : $"T{index}")} : unmanaged"));
            string declarationSource = $"public partial struct {managedTemplateName}<{typeParameters}> {constraints} {{ }}";
            string templateIdentifier = NativeIdentifier(cppTemplateName);
            OpaquePointerInfo genericPointer = new(
                UniqueName("nbg_opaque_" + templateIdentifier),
                cppName,
                managedType,
                declarationKey,
                declarationSource);
            _opaquePointers.Add(usr, genericPointer);
            return genericPointer;
        }

        string shortName = Spelling(declaration);
        if (shortName.Length is 0)
            shortName = cppName;

        string identifier = NativeIdentifier(shortName);
        OpaquePointerInfo pointer = new(
            UniqueName("nbg_opaque_" + identifier),
            cppName,
            "nint",
            $"opaque:{usr}",
            "");
        _opaquePointers.Add(usr, pointer);
        return pointer;
    }

    private static List<string> TemplateArguments(string cppType)
    {
        int start = cppType.IndexOf('<');
        int end = cppType.LastIndexOf('>');
        if (start is -1 || end <= start)
            return [];

        List<string> arguments = [];
        int depth = 0;
        int argumentStart = start + 1;
        for (int i = start + 1; i < end; i++)
        {
            switch (cppType[i])
            {
                case '<':
                    depth++;
                    break;
                case '>':
                    depth--;
                    break;
                case ',' when depth is 0:
                    arguments.Add(cppType[argumentStart..i].Trim());
                    argumentStart = i + 1;
                    break;
            }
        }

        arguments.Add(cppType[argumentStart..end].Trim());
        return arguments;
    }

    private string OpaqueTemplateName(string cppName)
    {
        if (_opaqueTemplateNames.TryGetValue(cppName, out string? existing))
            return existing;

        string managedName = ManagedIdentifier(cppName);
        while (_classes.Values.Any(type => type.CName == managedName)
            || _enums.Values.Any(type => type.CName == managedName)
            || _opaqueManagedNames.Contains(managedName))
        {
            managedName += "Opaque";
        }

        _opaqueManagedNames.Add(managedName);
        _opaqueTemplateNames.Add(cppName, managedName);
        return managedName;
    }

    private string OpaqueTemplateArgument(CXType argument)
    {
        CXType canonical = clang.getCanonicalType(argument);
        if (canonical.kind is CXTypeKind.CXType_Pointer
            or CXTypeKind.CXType_FunctionProto
            or CXTypeKind.CXType_FunctionNoProto)
        {
            return "nint";
        }

        if (ClassOf(canonical) is not null)
            return "nint";

        if (EnumOf(canonical) is { } enumInfo)
            return enumInfo.CName;

        if (canonical.kind is CXTypeKind.CXType_Record)
            return "nint";

        if (ArithName(canonical) is { } arith)
        {
            return arith switch
            {
                "bool" => "bool",
                "char" or "unsigned char" => "byte",
                "signed char" => "sbyte",
                "size_t" or "uintptr_t" => "nuint",
                "ptrdiff_t" or "ssize_t" or "intptr_t" => "nint",
                "int8_t" => "sbyte",
                "uint8_t" => "byte",
                "int16_t" => "short",
                "uint16_t" => "ushort",
                "int32_t" => "int",
                "uint32_t" => "uint",
                "int64_t" => "long",
                "uint64_t" => "ulong",
                "short" => "short",
                "unsigned short" => "ushort",
                "int" => "int",
                "unsigned int" => "uint",
                "long" => "CLong",
                "unsigned long" => "CULong",
                "long long" => "long",
                "unsigned long long" => "ulong",
                "float" => "float",
                "double" => "double",
                _ => "nint",
            };
        }

        return "nint";
    }

    private static string OpaqueTemplateArgument(string argument)
    {
        string normalized = argument
            .Replace("const ", "", StringComparison.Ordinal)
            .Replace("volatile ", "", StringComparison.Ordinal)
            .Trim();
        if (normalized.Contains('*')
            || normalized.Contains('&')
            || normalized.Contains('('))
        {
            return "nint";
        }

        return normalized switch
        {
            "bool" => "bool",
            "char" or "unsigned char" => "byte",
            "signed char" => "sbyte",
            "size_t" or "uintptr_t" => "nuint",
            "ptrdiff_t" or "ssize_t" or "intptr_t" => "nint",
            "int8_t" => "sbyte",
            "uint8_t" => "byte",
            "int16_t" => "short",
            "uint16_t" => "ushort",
            "int32_t" => "int",
            "uint32_t" => "uint",
            "int64_t" => "long",
            "uint64_t" => "ulong",
            "short" => "short",
            "unsigned short" => "ushort",
            "int" => "int",
            "unsigned int" => "uint",
            "long" => "CLong",
            "unsigned long" => "CULong",
            "long long" => "long",
            "unsigned long long" => "ulong",
            "float" => "float",
            "double" => "double",
            _ => "nint",
        };
    }

    private static string ManagedIdentifier(string qualifiedName)
    {
        string identifier = new(qualifiedName
            .TrimStart(':')
            .Select(character => char.IsLetterOrDigit(character) || character is '_' ? character : '_')
            .ToArray());
        return identifier.Length is 0 || char.IsDigit(identifier[0])
            ? "_" + identifier
            : identifier;
    }

    private static string NativeIdentifier(string name)
        => new(name.Select(character =>
            char.IsLetterOrDigit(character) || character is '_' ? character : '_').ToArray());

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
                {
                    if (options.Qt && IsQtList(pointeeCanonical))
                        return ConvertValue(pointeeCanonical, name);

                    if (IsHalf(pointeeCanonical) || IsGuid(pointeeCanonical))
                        return ConvertValue(pointeeCanonical, name);

                    if (pointeeCanonical.kind is CXTypeKind.CXType_Record
                        && ClassOf(pointeeCanonical) is null)
                    {
                        OpaquePointerInfo opaque = OpaquePointerOf(pointeeCanonical);
                        return new(
                            [new($"const {opaque.CName}*", "const void*", name)],
                            $"*static_cast<const {opaque.CppName}*>({name})");
                    }

                    return ConvertValue(pointeeCanonical, name);
                }

                if (ClassOf(pointeeCanonical) is { } info)
                {
                    _usedClasses.Add(Usr(info.Cursor));
                    return new([new(info.CName + "*", "void*", name)],
                        $"*static_cast<{info.CppName}*>({name})");
                }

                if (ArithName(pointee) is { } arith)
                    return new([new(arith + "*", arith + "*", name)], "*" + name);

                if (IsHalf(pointeeCanonical))
                {
                    _needsHalf = true;
                    return new(
                        [new(CExportGenerator.HalfBridgeType + "*", "uint16_t*", name)],
                        $"*reinterpret_cast<{NormalizedSpelling(pointeeCanonical)}*>({name})");
                }

                if (IsGuid(pointeeCanonical))
                {
                    _needsGuid = true;
                    return new(
                        [new(CExportGenerator.GuidBridgeType + "*", "cppm_guid*", name)],
                        $"*reinterpret_cast<{NormalizedSpelling(pointeeCanonical)}*>({name})");
                }

                if (pointeeCanonical.kind is CXTypeKind.CXType_Record)
                {
                    OpaquePointerInfo opaque = OpaquePointerOf(pointeeCanonical);
                    return new(
                        [new(opaque.CName + "*", "void*", name)],
                        $"*static_cast<{opaque.CppName}*>({name})");
                }

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

                if (IsHalf(pointeeCanonical))
                {
                    _needsHalf = true;
                    return new(
                        [new($"{qualifier}{CExportGenerator.HalfBridgeType}*", $"{qualifier}uint16_t*", name)],
                        $"reinterpret_cast<{qualifier}{NormalizedSpelling(pointeeCanonical)}*>({name})");
                }

                if (IsGuid(pointeeCanonical))
                {
                    _needsGuid = true;
                    return new(
                        [new($"{qualifier}{CExportGenerator.GuidBridgeType}*", $"{qualifier}cppm_guid*", name)],
                        $"reinterpret_cast<{qualifier}{NormalizedSpelling(pointeeCanonical)}*>({name})");
                }

                if (ClassOf(pointeeCanonical) is { } info)
                {
                    _usedClasses.Add(Usr(info.Cursor));
                    return new([new($"{qualifier}{info.CName}*", $"{qualifier}void*", name)],
                        $"static_cast<{qualifier}{info.CppName}*>({name})");
                }

                if (pointeeCanonical.kind is CXTypeKind.CXType_Record)
                {
                    OpaquePointerInfo opaque = OpaquePointerOf(pointeeCanonical);
                    return new(
                        [new($"{qualifier}{opaque.CName}*", $"{qualifier}void*", name)],
                        $"static_cast<{qualifier}{opaque.CppName}*>({name})");
                }

                string pointerType = clang.getTypeSpelling(canonical).ToString();
                return new(
                    [new("void*", "void*", name)],
                    $"reinterpret_cast<{pointerType}>({name})");
            }

            default:
                return ConvertValue(canonical, name);
        }
    }

    private ParamPlan ConvertValue(CXType canonical, string name)
    {
        if (IsHalf(canonical))
        {
            _needsHalf = true;
            return new(
                [new(CExportGenerator.HalfBridgeType, "uint16_t", name)],
                $"cppm::from_half_bits<{NormalizedSpelling(canonical)}>({name})");
        }

        if (IsGuid(canonical))
        {
            _needsGuid = true;
            return new(
                [new(CExportGenerator.GuidBridgeType, "cppm_guid", name)],
                $"cppm::from_guid_bits<{NormalizedSpelling(canonical)}>({name})");
        }

        if (options.Qt
            && IsQString(canonical))
            return new([new("const char*", "const char*", name)], $"QString::fromUtf8({name})");

        if (options.Qt
            && IsQtList(canonical))
        {
            CXType elementType = clang.Type_getTemplateArgumentAsType(canonical, 0);
            string element;
            string bridgeElement;
            string arrayExpression;
            if (ArithName(elementType) is { } arith)
            {
                element = arith;
                bridgeElement = arith;
                arrayExpression = name + QtArraySuffix;
            }
            else if (clang.getCanonicalType(elementType).kind is CXTypeKind.CXType_Pointer)
            {
                element = clang.getTypeSpelling(elementType).ToString();
                bridgeElement = "intptr_t";
                arrayExpression = $"reinterpret_cast<{element} const*>({name}{QtArraySuffix})";
            }
            else
            {
                throw new NotSupportedException("QList 元素类型暂不支持 Span 转换");
            }

            string arrayName = name + QtArraySuffix;
            string countName = name + QtArrayCountSuffix;
            return new(
                [new($"const {bridgeElement}*", $"const {bridgeElement}*", arrayName), new("size_t", "size_t", countName)],
                $"cppm::to_list<QList<{element}>>({arrayExpression}, {countName})");
        }

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

                if (pointeeCanonical.kind is CXTypeKind.CXType_Record)
                {
                    OpaquePointerInfo opaque = OpaquePointerOf(pointeeCanonical);
                    return new(opaque.CName + "*", "void*", x => $"cppm::ref_to_void({x})");
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

                if (pointeeCanonical.kind is CXTypeKind.CXType_Record)
                {
                    _ = OpaquePointerOf(pointeeCanonical);
                    return new(
                        _opaquePointers[Usr(clang.getTypeDeclaration(pointeeCanonical))].CName + "*",
                        "void*",
                        x => $"cppm::ptr_to_void({x})");
                }

                return new(
                    "void*",
                    "void*",
                    pointeeCanonical.kind is CXTypeKind.CXType_FunctionProto or CXTypeKind.CXType_FunctionNoProto
                        ? x => $"reinterpret_cast<void*>({x})"
                        : x => $"cppm::ptr_to_void({x})");
            }
        }

        if (IsHalf(canonical))
        {
            _needsHalf = true;
            return new(CExportGenerator.HalfBridgeType, "uint16_t", x => $"cppm::to_half_bits({x})");
        }

        if (IsGuid(canonical))
        {
            _needsGuid = true;
            return new(CExportGenerator.GuidBridgeType, "cppm_guid", x => $"cppm::to_guid_bits({x})");
        }

        if (options.Qt
            && IsQString(canonical))
            return new("const char*", "char*", x => $"cppm::dup_string(({x}).toUtf8().constData())");

        if (IsStdString(canonical)
            || IsStdStringView(canonical))
        {
            return new("const char*", "char*", x => $"cppm::dup_string({x})");
        }

        if (options.Qt
            && IsQtList(canonical))
        {
            CXType elementType = clang.Type_getTemplateArgumentAsType(canonical, 0);
            if (ArithName(elementType) is { } arith)
            {
                return new(
                    $"{arith}*",
                    $"{arith}*",
                    x => $"cppm::to_c_array({x}, {QtCountName})",
                    NeedsCount: true,
                    CountParameterName: QtCountName);
            }

            if (clang.getCanonicalType(elementType).kind is CXTypeKind.CXType_Pointer)
            {
                return new(
                    "intptr_t*",
                    "intptr_t*",
                    x => $"reinterpret_cast<intptr_t*>(cppm::to_c_array({x}, {QtCountName}))",
                    NeedsCount: true,
                    CountParameterName: QtCountName);
            }

            throw new NotSupportedException("QList 元素类型暂不支持 Span 转换");
        }

        if (IsStdVector(canonical))
        {
            string element = ArithName(clang.Type_getTemplateArgumentAsType(canonical, 0)) is { } arith and not "bool"
                ? arith
                : throw new NotSupportedException("仅支持元素为 C 基本类型的 std::vector");
            return new(
                $"{element}*",
                $"{element}*",
                x => $"cppm::to_c_array({x}, {CountName})",
                NeedsCount: true,
                CountParameterName: CountName);
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

        while (name is SelfName or CountName or QtCountName
            || !used.Add(name))
        {
            name += "_";
        }

        return name;
    }

    private void BindClass(ClassInfo info)
    {
        // 该类的全部导出写入以类名命名的 .h/.cpp 对
        _currentOwner = info.CName;
        try
        {
            BindClassCore(info);
        }
        finally
        {
            _currentOwner = CommonOwner;
        }
    }

    private void BindClassCore(ClassInfo info)
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
                        && IsQtMemberBindable(child)
                        && clang.CXXMethod_isDeleted(child) is 0)
                    {
                        TryBind(info, "create", () => BindConstructor(info, child));
                    }

                    break;

                case CXCursorKind.CXCursor_Destructor:
                    destructorExported = IsPublic(child)
                        && IsQtMemberBindable(child)
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
                        && IsQtMemberBindable(child)
                        && clang.CXXMethod_isDeleted(child) is 0
                        && !Spelling(child).StartsWith("operator", StringComparison.Ordinal)
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
    private bool _needsHalf;
    private bool _needsGuid;

    private static bool IsQString(CXType canonical)
        => clang.getTypeSpelling(canonical).ToString().Replace("const ", "").Trim() is "QString";

    /// <summary>
    /// 去掉 const/volatile、class/struct 和全局限定符后的类型名。
    /// </summary>
    private static string NormalizedSpelling(CXType type)
    {
        string spelling = clang.getTypeSpelling(type).ToString().Trim();
        bool changed = true;
        while (changed)
        {
            changed = false;
            foreach (string prefix in (string[])["const ", "volatile ", "class ", "struct "])
            {
                if (spelling.StartsWith(prefix, StringComparison.Ordinal))
                {
                    spelling = spelling[prefix.Length..].TrimStart();
                    changed = true;
                }
            }
        }

        return spelling.TrimStart(':');
    }

    private static bool IsHalf(CXType canonical)
        => NormalizedSpelling(canonical) is "qfloat16";

    private static bool IsGuid(CXType canonical)
        => NormalizedSpelling(canonical) is "_GUID" or "GUID";

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
            CXType pointeeCanonical = clang.getCanonicalType(pointee);
            if (clang.isConstQualifiedType(pointee) is 0
                && ClassOf(pointeeCanonical) is null
                && pointeeCanonical.kind is not CXTypeKind.CXType_Record)
            {
                throw new NotSupportedException($"不支持的非 const 引用信号参数 {clang.getTypeSpelling(type)}");
            }

            canonical = pointeeCanonical;
            if (ClassOf(canonical) is { } referenced)
            {
                _usedClasses.Add(Usr(referenced.Cursor));
                return (referenced.CName + "*", "void*", $"cppm::ref_to_void(const_cast<{referenced.CppName}&>({name}))");
            }

            if (canonical.kind is CXTypeKind.CXType_Record)
            {
                OpaquePointerInfo opaque = OpaquePointerOf(canonical);
                return (opaque.CName + "*", "void*", $"cppm::ref_to_void(const_cast<{opaque.CppName}&>({name}))");
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

            if (pointeeCanonical.kind is CXTypeKind.CXType_Record)
            {
                OpaquePointerInfo opaque = OpaquePointerOf(pointeeCanonical);
                return (opaque.CName + "*", "void*", $"cppm::ptr_to_void(const_cast<{opaque.CppName}*>({name}))");
            }

            return (
                qualifier + "void*",
                qualifier + "void*",
                $"reinterpret_cast<{qualifier}void*>({name})");
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
            string cppType = CallbackCppType(type);
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

        ExportGroup group = GroupOf(_currentOwner);
        group.HeaderTypedefs.Add($"typedef void (CPPM_CALL *{callbackName})({string.Join(", ", hTypes)});");
        group.SourceTypedefs.Add($"typedef void (CPPM_CALL *{callbackName})({string.Join(", ", cTypes)});");
        _connectionUsed = true;

        string sender = $"static_cast<{info.CppName}*>({SelfName})";
        Add(
            UniqueName($"{info.CName}_connect_{snake}"),
            new(ConnectionTypeName + "*", "void*", x => x),
            [
                new(info.CName + "*", "void*", SelfName),
                new(callbackName, callbackName, "callback"),
                new("void*", "void*", "user"),
            ],
            $"new QMetaObject::Connection(QObject::connect({sender}, {pointer}, {sender}, [callback, user]({string.Join(", ", lambdaParameters)}) {{ callback({string.Join(", ", callArguments)}); }}))");
    }

    private void BindConnectionDestroy()
        => Add(
            UniqueName(ConnectionDestroyFunctionName),
            new("void", "void", x => x),
            [new(ConnectionTypeName + "*", "void*", SelfName)],
            $"[&] {{ auto* connection = static_cast<QMetaObject::Connection*>({SelfName}); QObject::disconnect(*connection); delete connection; }}()",
            isStatement: true);

    private static void TryBind(ClassInfo info, string description, Action bind)
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
            string name = ParameterName(argument, i, used);
            ParamPlan plan = ConvertParameter(clang.getCursorType(argument), name);
            foreach (Param parameter in plan.Params.Where(parameter => parameter.Name != name))
            {
                if (!used.Add(parameter.Name))
                    throw new NotSupportedException($"参数名 {parameter.Name} 与 QList 数组转换参数冲突");
            }

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
        CXCursor declaring = clang.getCursorSemanticParent(method);
        string declaringName = QualifiedName(declaring).Cpp ?? info.CppName;

        List<Param> parameters = [];
        List<string> arguments = [];
        string qualifier = isConst ? "const " : "";
        if (!isStatic)
            parameters.Add(new($"{qualifier}{info.CName}*", $"{qualifier}void*", SelfName));

        ConvertArguments(method, parameters, arguments);
        string target = isStatic
            ? $"{declaringName}::{name}"
            : $"static_cast<{qualifier}{declaringName}*>({SelfName})->{name}";
        Add(
            UniqueName($"{info.CName}_{name.Underscore()}"),
            returnPlan,
            parameters,
            $"{target}({string.Join(", ", arguments)})",
            originalSignature: OriginalMethodSignature(info, method, name));
    }

    private static string OriginalMethodSignature(ClassInfo info, CXCursor method, string name)
    {
        string returnType = clang.getTypeSpelling(clang.getCursorResultType(method)).ToString();
        int count = clang.Cursor_getNumArguments(method);
        string parameters = count is 0
            ? ""
            : string.Join(", ", Enumerable.Range(0, count)
                .Select(index => clang.getTypeSpelling(clang.getCursorType(
                    clang.Cursor_getArgument(method, (uint)index))).ToString()));
        string isConst = clang.CXXMethod_isConst(method) is not 0 ? " const" : "";
        string declaringName = QualifiedName(clang.getCursorSemanticParent(method)).Cpp ?? info.CppName;
        return $"{returnType} {declaringName}::{name}({parameters}){isConst}";
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

    private void Add(
        string cName,
        ReturnPlan returnPlan,
        List<Param> parameters,
        string expression,
        bool isStatement = false,
        string? originalSignature = null)
    {
        if (originalSignature is not null)
            _originalSignatures.Add(cName, originalSignature);

        List<Param> all = [.. parameters];
        if (returnPlan.NeedsCount)
            all.Add(new("size_t*", "size_t*", returnPlan.CountParameterName!));

        string hParameters = all.Count is 0
            ? "void"
            : string.Join(", ", all.Select(p => $"{p.H} {p.Name}"));
        string cParameters = all.Count is 0
            ? ""
            : string.Join(", ", all.Select(p => $"{p.C} {p.Name}"));

        string body = (returnPlan.C is "void" || isStatement)
            ? $"{expression};"
            : $"return {returnPlan.Wrap(expression)};";

        ExportGroup group = GroupOf(_currentOwner);
        group.HeaderDeclarations.Add($"{returnPlan.H} CPPM_CALL {cName}({hParameters});");
        group.SourceDefinitions.Add(Lf($$"""
            CPPM_EXPORT {{returnPlan.C}} CPPM_CALL {{cName}}({{cParameters}})
            {
                {{body}}
            }
            """));
        _sourceDefinitionCount++;
    }

    private ExportGroup GroupOf(string owner)
    {
        if (!_groups.TryGetValue(owner, out ExportGroup? group))
            _groups[owner] = group = new();

        return group;
    }

    // 源文件可能是 CRLF，原始字符串中的换行随之变化，统一规整为 LF
    private static string Lf(string text)
        => text.ReplaceLineEndings("\n");

    // ---- 输出 ----

    /// <summary>
    /// 生成按类拆分的 native 产物：
    /// <list type="bullet">
    /// <item><c>&lt;native-name&gt;.h</c> / <c>&lt;native-name&gt;.cpp</c>：公共声明（桥接类型、枚举、不透明指针、free_memory）与类句柄前置声明；</item>
    /// <item><c>&lt;类名&gt;.h</c> / <c>&lt;类名&gt;.cpp</c>：该类的导出声明与实现，各为一个翻译单元。</item>
    /// </list>
    /// 分开的翻译单元让编译器与 Roslyn 都能并行处理，避免单个巨大文件拖慢甚至引发编译失败。
    /// </summary>
    private void BuildNativeFiles(string[] headers)
    {
        string includes = string.Join("\n", headers.Select(header =>
            $"#include \"{header.Replace('\\', '/')}\""));

        // 公共头：所有拆分文件都包含它，因此这里集中放置共享声明
        string commonHeader = BuildCommonHeader();
        _nativeFiles.Add(new(options.NativeName + ".h", commonHeader));
        _generatedHeaders.Add(Path.Combine(
            Path.GetFullPath(options.NativeOutput), options.NativeName + ".h"));

        // 公共实现：free_memory 与 Qt 连接销毁
        _nativeFiles.Add(new(
            options.NativeName + ".cpp",
            BuildCommonSource(includes)));

        // 同一个头文件里可能出现清理后同名的类（如 a::b 与 a_b），文件名需去重
        HashSet<string> usedBaseNames = [with(StringComparer.OrdinalIgnoreCase)];
        usedBaseNames.Add(options.NativeName);

        foreach ((string owner, ExportGroup group) in _groups
            .Where(entry => entry.Key != CommonOwner && entry.Value.Count > 0)
            .OrderBy(entry => entry.Key, StringComparer.Ordinal))
        {
            string baseName = FileBaseName(owner);
            string unique = baseName;
            for (int suffix = 2; !usedBaseNames.Add(unique); suffix++)
                unique = $"{baseName}_{suffix}";

            string headerName = unique + ".h";
            _nativeFiles.Add(new(headerName, BuildGroupHeader(owner, group)));
            _nativeFiles.Add(new(unique + ".cpp", BuildGroupSource(owner, group, includes)));
            _generatedHeaders.Add(Path.Combine(Path.GetFullPath(options.NativeOutput), headerName));
        }

        _nativeFiles.Add(new(CppMarshaller.FileName, CppMarshaller.Source.Replace("\r\n", "\n") + "\n"));
    }

    /// <summary>
    /// 文件名主体：只保留字母、数字与下划线，避免命名空间分隔符等字符进入路径。
    /// </summary>
    private static string FileBaseName(string owner)
    {
        string name = NativeIdentifier(owner);
        return name.Length is 0 ? "exports" : name;
    }

    /// <summary>
    /// 公共头：桥接类型、枚举、不透明指针的前置声明、共享宏与 free_memory 声明。
    /// </summary>
    private string BuildCommonHeader()
    {
        string classes = string.Join("\n", _classes.Values
            .Where(c => _usedClasses.Contains(Usr(c.Cursor)))
            .OrderBy(c => c.CName, StringComparer.Ordinal)
            .Select(c => $"typedef struct {c.CName} {c.CName};"));
        if (_connectionUsed)
            classes += $"\ntypedef struct {ConnectionTypeName} {ConnectionTypeName};";

        string enums = string.Join("\n\n", _enums.Values
            .OrderBy(e => e.CName, StringComparer.Ordinal)
            .Select(EnumDeclaration));
        string opaquePointers = string.Join("\n", _opaquePointers.Values
            .OrderBy(pointer => pointer.CName, StringComparer.Ordinal)
            .Select(pointer => $"typedef struct {pointer.CName} {{ unsigned char _opaque; }} {pointer.CName};"));
        string bridgeTypes = BuildBridgeTypeDeclarations();

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

            // DLL 导出标记；实现文件通过 cpp_marshaller.hpp 也会定义一次，保持相容
            #ifndef CPPM_EXPORT
            #if defined(_WIN32)
            #define CPPM_EXPORT extern "C" __declspec(dllexport)
            #else
            #define CPPM_EXPORT extern "C" __attribute__((visibility("default")))
            #endif
            #endif

            #ifdef __cplusplus
            extern "C" {
            #endif

            {{bridgeTypes}}

            {{classes}}

            {{enums}}

            {{opaquePointers}}

            void CPPM_CALL {{FreeMemoryFunctionName}}(void* pointer);

            #ifdef __cplusplus
            }
            #endif

            """);
    }

    /// <summary>
    /// 公共实现：free_memory 与 Qt 连接销毁。
    /// 与拆分类文件保持一致，不包含公共头（头文件里的强类型句柄与实现的 <c>void*</c> 签名不同）。
    /// </summary>
    private string BuildCommonSource(string includes)
    {
        string typedefs = string.Join("\n", GroupOf(CommonOwner).SourceTypedefs);
        string definitions = string.Join("\n\n",
            GroupOf(CommonOwner).SourceDefinitions);

        return Lf($$"""
            // 由 OBSharp.Interop.NativeBindGenerator --cpp-redirect 生成，请勿手工修改。
            #include "{{CppMarshaller.FileName}}"
            {{(_connectionUsed ? includes : "")}}

            {{typedefs}}

            CPPM_EXPORT void CPPM_CALL {{FreeMemoryFunctionName}}(void* pointer)
            {
                cppm::release(pointer);
            }

            {{definitions}}

            """);
    }

    /// <summary>
    /// 单个类的头文件：包含公共头，再声明该类的全部 C 式导出。
    /// </summary>
    private string BuildGroupHeader(string owner, ExportGroup group)
    {
        string typedefs = string.Join("\n", group.HeaderTypedefs);
        string declarations = string.Join("\n", group.HeaderDeclarations);

        return Lf($$"""
            // 由 OBSharp.Interop.NativeBindGenerator --cpp-redirect 生成，请勿手工修改。
            // 类：{{owner}}
            #pragma once

            #include "{{options.NativeName}}.h"

            #ifdef __cplusplus
            extern "C" {
            #endif

            {{typedefs}}

            {{declarations}}

            #ifdef __cplusplus
            }
            #endif

            """);
    }

    /// <summary>
    /// 单个类的实现文件：独立翻译单元。
    /// 刻意<b>不</b>包含该类头文件：头文件里的参数是 <c>demo_Widget*</c> 这类强类型句柄，
    /// 而实现统一用 <c>void*</c> 承接，同名 extern "C" 函数签名不同会直接报错。
    /// 头文件只供使用方（以及 C# 绑定生成）读取。
    /// </summary>
    private static string BuildGroupSource(string owner, ExportGroup group, string includes)
    {
        string typedefs = string.Join("\n", group.SourceTypedefs);
        string definitions = string.Join("\n\n", group.SourceDefinitions);

        return Lf($$"""
            // 由 OBSharp.Interop.NativeBindGenerator --cpp-redirect 生成，请勿手工修改。
            // 类：{{owner}}
            #include "{{CppMarshaller.FileName}}"
            {{includes}}

            {{typedefs}}

            {{definitions}}

            """);
    }

    // 位模式桥接类型的 C 声明：C# 侧分别映射为 Half、Guid。
    private string BuildBridgeTypeDeclarations()
    {
        List<string> declarations = [];
        if (_needsHalf)
            declarations.Add($"typedef uint16_t {CExportGenerator.HalfBridgeType};");

        if (_needsGuid)
        {
            declarations.Add($$"""
                typedef struct {{CExportGenerator.GuidBridgeType}}
                {
                    uint32_t data1;
                    uint16_t data2;
                    uint16_t data3;
                    uint8_t data4[8];
                } {{CExportGenerator.GuidBridgeType}};
                """);
        }

        return string.Join("\n", declarations);
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
}
