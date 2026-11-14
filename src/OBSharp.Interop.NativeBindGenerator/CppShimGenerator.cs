using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

using ClangSharp.Interop;

namespace OBSharp.Interop.NativeBindGenerator;

/// <summary>
/// 把 C++ 类型转成 extern "C" 导出层，使 <see cref="CBindingGenerator"/> 能按纯 C 声明生成绑定。
/// </summary>
/// <remarks>
/// 完整流程为 ClangCollector → 本生成器 → ClangCollector（重新解析 shim 头文件）
/// → <see cref="CBindingGenerator"/> → <see cref="CSharpWrapGenerator"/>。
/// C++ 类和结构体在 C# 侧一律以 <c>struct</c> 呈现：构造映射为 <c>type_new</c>，
/// 析构映射为 <c>type_finalize</c>，成员函数映射为 <c>type_method</c>；重载按参数类型追加后缀区分。
/// 派生类（如 <c>QTShimGenerator</c>）通过覆盖 <see cref="SkipMember"/>、<see cref="ScalarCType"/>、
/// <see cref="BuildToCppExpression"/> 等钩子实现框架特化，而不必复制整套发现与输出逻辑。
/// </remarks>
public partial class CppShimGenerator(CppShimGeneratorOptions options) : ClangCollector
{
    protected CppShimGeneratorOptions ShimOptions { get; } = options;
    protected List<ShimTypeInfo> Types { get; } = [];
    protected List<ShimFunctionInfo> Functions { get; } = [];
    protected Dictionary<uint, ShimTypeInfo> TypeByKey { get; } = [];
    protected HashSet<string> UsedSymbols { get; } = [with(StringComparer.Ordinal)];

    /// <summary>
    /// 源码文件行缓存：libclang 不暴露内联标志等语法信息，需要回读声明所在源码行判断。
    /// </summary>
    private readonly Dictionary<string, string[]> _sourceLines = [];

    /// <summary>
    /// 头文件里实际引用到的全部句柄类型名：已登记类型与临时换算类型都在此登记，按名字去重。
    /// </summary>
    protected HashSet<string> HandleDeclarations { get; } = [with(StringComparer.Ordinal)];
    protected List<GeneratedFile> Files { get; } = [];

    /// <summary>
    /// 扫描头文件并写出 shim 头文件、实现、PCH 与 CMake 清单。
    /// </summary>
    public void Generate()
    {
        LibClangLoader.Ensure();

        string staging = CreateStagingDirectory("cpp-shim-scan-");
        try
        {
            string tuPath = Path.Combine(staging, "scan.cpp");
            using (StringWriter writer = CreateWriter())
            {
                foreach (string header in ShimOptions.Headers)
                {
                    // 临时翻译单元位于系统临时目录，因此头文件必须转成绝对路径
                    writer.Write("#include \"");
                    writer.Write(Path.GetFullPath(header).Replace('\\', '/'));
                    writer.WriteLine("\"");
                }

                WriteTextFile(tuPath, writer.ToString());
            }

            CXIndex index = CXIndex.Create(false, false);
            try
            {
                CXTranslationUnit unit = ParseUnit(index, tuPath);
                if (unit.Handle == IntPtr.Zero)
                    throw new InvalidOperationException("libclang 无法创建翻译单元，请检查 -- 之后的 clang 参数");

                try
                {
                    if (ReportErrors(unit))
                        throw new InvalidOperationException("clang 解析头文件时出现错误，已中止生成");

                    // cursor 的生命周期绑定在翻译单元上，发现必须在其释放前完成
                    List<CXCursor> declarations = CollectTopLevel(unit);
                    DiscoverTypes(declarations);
                    DiscoverMembers(declarations);
                }
                finally
                {
                    unit.Dispose();
                }
            }
            finally
            {
                index.Dispose();
            }
        }
        finally
        {
            DeleteStagingDirectory(staging);
        }

        WriteOutputs();
    }

    /// <summary>
    /// 解析 shim 头文件，返回可供 <see cref="CBindingGenerator"/> 使用的翻译单元。
    /// </summary>
    public CXTranslationUnit ParseShim()
    {
        LibClangLoader.Ensure();

        // 只按 shim 导出前缀识别函数，避免转换工具头文件里的内部 extern "C" 函数被误收集
        ExportNamePrefix = ShimOptions.ExportPrefix;

        // shim 头文件 include 了用户头文件、标准库与转换头；
        // 常量只应从这两类头文件收集，否则会混入标准库的上千个常量
        SourceFiles =
        [
            .. ShimOptions.Headers.Select(Path.GetFullPath),
            Path.GetFullPath(Path.Combine(ShimOptions.Output.FullName, ShimOptions.BaseName + ".h")),
        ];

        string staging = CreateStagingDirectory("cpp-shim-parse-");
        try
        {
            string headerName = ShimOptions.BaseName + ".h";
            // shim 头文件里含 extern "C" 与 C++ 类型注释，必须以 C++ 方式解析
            string tuPath = Path.Combine(staging, "shim.cpp");
            WriteTextFile(
                tuPath,
                "#include \"" + Path.Combine(ShimOptions.Output.FullName, headerName).Replace('\\', '/') + "\"\r\n");

            CXIndex index = CXIndex.Create(false, false);
            try
            {
                CXTranslationUnit unit = ParseUnit(index, tuPath);
                if (unit.Handle == IntPtr.Zero)
                    throw new InvalidOperationException("libclang 无法解析 shim 头文件，请检查 -- 之后的 clang 参数");

                return unit;
            }
            finally
            {
                index.Dispose();
            }
        }
        finally
        {
            DeleteStagingDirectory(staging);
        }
    }

    // ---- 解析 ----

    private CXTranslationUnit ParseUnit(CXIndex index, string path)
        => CXTranslationUnit.Parse(
            index,
            path,
            ClangArguments(),
            default,
            CXTranslationUnit_Flags.CXTranslationUnit_SkipFunctionBodies);

    private ReadOnlySpan<string> ClangArguments()
    {
        List<string> arguments = [.. ShimOptions.ClangArguments];
        return arguments.ToArray();
    }

    /// <summary>
    /// 收集顶层声明；<c>extern "C"</c> 块内的声明按层级展开，与普通顶层声明同等看待。
    /// </summary>
    /// <remarks>
    /// 通过 <c>VisitChildren</c> 遍历：<see cref="CXCursor.NumChildren"/> 在跳过函数体解析的翻译单元里返回 -1，
    /// 不能用下标访问子节点。
    /// </remarks>
    private static List<CXCursor> CollectTopLevel(CXTranslationUnit unit)
    {
        DeclarationCollector collector = new();
        GCHandle handle = GCHandle.Alloc(collector);
        try
        {
            unsafe
            {
                unit.Cursor.VisitChildren(&TopLevelVisitor, new CXClientData((nint)handle));
            }
        }
        finally
        {
            handle.Free();
        }

        return collector.Results;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static unsafe CXChildVisitResult TopLevelVisitor(CXCursor cursor, CXCursor parent, void* clientData)
    {
        if (GCHandle.FromIntPtr((nint)clientData).Target is not DeclarationCollector collector)
            return CXChildVisitResult.CXChildVisit_Break;

        // namespace 与 extern "C" 块都要下钻：真正的类型声明通常在命名空间里
        if (cursor.kind is CXCursorKind.CXCursor_LinkageSpec
            or CXCursorKind.CXCursor_Namespace
            or CXCursorKind.CXCursor_NamespaceAlias)
        {
            if (collector.Depth >= 8)
                return CXChildVisitResult.CXChildVisit_Continue;

            collector.Depth++;
            return CXChildVisitResult.CXChildVisit_Recurse;
        }

        collector.Results.Add(cursor);
        return CXChildVisitResult.CXChildVisit_Continue;
    }

    private static bool ReportErrors(CXTranslationUnit unit)
    {
        bool hasError = false;
        uint count = unit.NumDiagnostics;
        for (uint i = 0; i < count; i++)
        {
            using CXDiagnostic diagnostic = unit.GetDiagnostic(i);
            if (diagnostic.Severity is CXDiagnosticSeverity.CXDiagnostic_Error or CXDiagnosticSeverity.CXDiagnostic_Fatal)
            {
                hasError = true;
                Console.Error.WriteLine(diagnostic.Format(CXDiagnosticDisplayOptions.CXDiagnostic_DisplaySourceLocation));
            }
        }

        return hasError;
    }

    // ---- 类型发现 ----

    /// <summary>
    /// 收集可导出的记录类型：具名、完整定义、且不处于 private/protected 区。
    /// </summary>
    protected virtual void DiscoverTypes(IEnumerable<CXCursor> declarations)
    {
        foreach (CXCursor declaration in declarations)
        {
            if (IsRecordDeclaration(declaration) is false)
                continue;

            AddType(declaration);
        }

        // 字段中出现的类型可能没有出现在顶层声明里（例如内联定义的嵌套结构）
        List<ShimTypeInfo> pending = [.. Types];
        foreach (ShimTypeInfo type in pending)
            EnsureFieldDependencies(type);

        Types.Sort((left, right) => string.CompareOrdinal(left.CppName, right.CppName));
    }

    private void AddType(CXCursor declaration)
    {
        if (declaration.IsAnonymous || declaration.Spelling.ToString().Length is 0)
            return;

        if (IsAccessible(declaration) is false)
            return;

        // 未实例化的类模板本身不是类型：既不能 `new`，也不能在 C++ 侧以裸名出现（C2955）。
        // 在登记阶段就剔除，避免它的 _new/_finalize/成员导出进入产物
        if (IsUninstantiatedTemplate(declaration))
            return;

        uint key = KeyOf(declaration);
        if (TypeByKey.ContainsKey(key))
            return;

        ShimTypeInfo type = new(
            key,
            declaration,
            RecordCppName(declaration.Type),
            ShimOptions.ExportPrefix + "_" + ToIdentifier(QualifiedName(declaration).Replace("::", "_", StringComparison.Ordinal)),
            CandidateHandleName(declaration),
            RecordKeyword(declaration.kind))
        {
            // 翻译单元存活期间判定并缓存，供输出阶段的自检使用
            IsUninstantiatedTemplate = IsUninstantiatedTemplate(declaration),
        };

        TypeByKey.Add(key, type);
        Types.Add(type);
        HandleDeclarations.Add(type.HandleName);
    }

    protected static string RecordKeyword(CXCursorKind kind)
        => kind switch
        {
            CXCursorKind.CXCursor_ClassDecl => "class",
            CXCursorKind.CXCursor_UnionDecl => "union",
            _ => "struct",
        };

    /// <summary>
    /// 句柄类型名在发现阶段就确定，保证同一类型在头文件与实现中一致。
    /// </summary>
    private string CandidateHandleName(CXCursor declaration)
        => ShimOptions.ExportPrefix + "_" + ToIdentifier(QualifiedName(declaration).Replace("::", "_", StringComparison.Ordinal));

    /// <summary>
    /// 声明是否为「未实例化的类模板」：这种声明不是类型，既不能 <c>new</c>，也不能以裸名出现在 C++ 侧。
    /// </summary>
    /// <remarks>
    /// 不能只靠 cursor kind：模板经常经 typedef/别名（如 <c>typedef QBasicAtomicInteger&lt;int&gt; QBasicAtomicInt;</c>）
    /// 或作为参数/字段类型被间接引用，到达这里的 kind 可能是 <c>StructDecl</c>。
    /// 因此组合多个判据，**任一命中即视为未实例化模板**；所有判据都不命中时也按模板处理
    /// （宁可少导出，也不要产出编译不过的 C++ 代码）。
    /// </remarks>
    private static bool IsUninstantiatedTemplate(CXCursor declaration)
    {
        if (declaration.kind is CXCursorKind.CXCursor_ClassTemplate
            or CXCursorKind.CXCursor_ClassTemplatePartialSpecialization
            or CXCursorKind.CXCursor_TypeAliasTemplateDecl)
        {
            return true;
        }

        // 最直接的 AST 证据：模板声明会带 TemplateTypeParameter 子声明。
        // 这比 kind 与拼写都可靠——Qt 的主模板在部分平移单元里会以 StructDecl 到达、拼写也无 `<`
        if (HasTemplateTypeParameter(declaration))
            return true;

        // 模板实例必须能给出带参数列表的拼写；只有裸模板名说明没有实例化信息
        CXType type = declaration.Type;
        string spelling = type.Spelling.ToString();
        if (spelling.Contains('<', StringComparison.Ordinal))
            return false;

        // libclang 只有在真正实例化后才报告模板参数个数
        if (type.NumTemplateArguments > 0)
            return false;

        // 语义父级是模板声明，说明它是模板里的嵌套声明
        CXCursor parent = declaration.SemanticParent;
        while (!parent.IsNull && parent.kind is not CXCursorKind.CXCursor_TranslationUnit)
        {
            if (parent.kind is CXCursorKind.CXCursor_ClassTemplate
                or CXCursorKind.CXCursor_ClassTemplatePartialSpecialization)
            {
                return true;
            }

            parent = parent.SemanticParent;
        }

        // 到这里仍无法证明它是模板：普通类型（非模板、非实例化）必须放行，
        // 否则会把所有正常类都判成模板、导致导出全灭。只有能确认「与模板关联且无参数列表」时才拒绝
        CXCursor specialized = declaration.SpecializedCursorTemplate;
        if (specialized.IsNull)
            return false;

        // SpecializedCursorTemplate 非空 = 与某个模板有关联（要么是模板本身，要么是特化/实例化）
        return specialized.kind is CXCursorKind.CXCursor_ClassTemplate
            or CXCursorKind.CXCursor_ClassTemplatePartialSpecialization;
    }

    /// <summary>
    /// 声明第一层子节点里是否存在模板类型参数（<c>template &lt;typename T&gt;</c> 的标志）。
    /// </summary>
    private static bool HasTemplateTypeParameter(CXCursor declaration)
    {
        int count = declaration.NumChildren;
        for (int i = 0; i < count; i++)
        {
            CXCursor child = declaration.GetChild((uint)i);
            if (child.kind is CXCursorKind.CXCursor_TemplateTypeParameter
                or CXCursorKind.CXCursor_TemplateTemplateParameter
                or CXCursorKind.CXCursor_NonTypeTemplateParameter)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 已登记类型的句柄名；未登记时退化为按限定名即时换算（用于参数/返回值里的临时类型，例如
    /// Qt 私有嵌套类型与模板实例）。这种类型没有自己的成员导出，但同样要在头文件里补前置声明，
    /// 否则在 <c>extern "C"</c> 下会被编译器当成未知类型（MSVC 按 int 处理，Qt Core 产物因此编译失败）。
    /// </summary>
    /// <remarks>
    /// 未实例化的类模板（如 <c>QBasicAtomicInteger</c>、<c>QAtomicOpsSupport</c>）没有合法 C 侧类型名，
    /// 返回 <c>null</c> 让调用方降级为 <c>void*</c>，绝不产出裸模板名。
    /// </remarks>
    private string? ResolveHandleName(CXCursor declaration)
    {
        ShimTypeInfo? known = TypeByKey.GetValueOrDefault(KeyOf(declaration));
        if (known is not null)
            return known.HandleName;

        if (IsUninstantiatedTemplate(declaration))
            return null;

        string name = CandidateHandleName(declaration);
        HandleDeclarations.Add(name);
        return name;
    }

    private void EnsureFieldDependencies(ShimTypeInfo type)
    {
        CXCursor definition = type.Declaration.Definition;
        if (definition.IsNull)
            return;

        int fieldCount = definition.NumFields;
        for (int i = 0; i < fieldCount; i++)
            EnsureDependency(definition.GetField((uint)i).Type, depth: 0);
    }

    private void EnsureDependency(CXType type, int depth)
    {
        if (depth > 4)
            return;

        CXType canonical = Canonical(type);
        switch (canonical.kind)
        {
            case CXTypeKind.CXType_Record:
                AddType(canonical.Declaration);
                return;

            case CXTypeKind.CXType_ConstantArray:
            case CXTypeKind.CXType_IncompleteArray:
                EnsureDependency(canonical.ArrayElementType, depth + 1);
                return;

            case CXTypeKind.CXType_Pointer:
            case CXTypeKind.CXType_LValueReference:
            case CXTypeKind.CXType_RValueReference:
                EnsureDependency(canonical.PointeeType, depth + 1);
                return;
        }
    }

    protected virtual bool IsRecordDeclaration(CXCursor cursor)
        => cursor.kind is CXCursorKind.CXCursor_ClassDecl
            or CXCursorKind.CXCursor_StructDecl
            or CXCursorKind.CXCursor_UnionDecl;

    /// <summary>
    /// 是否在可访问区：命名空间作用域（无访问修饰符）或 public 区。
    /// </summary>
    protected virtual bool IsAccessible(CXCursor cursor)
        => cursor.CXXAccessSpecifier is CX_CXXAccessSpecifier.CX_CXXInvalidAccessSpecifier
            or CX_CXXAccessSpecifier.CX_CXXPublic;

    protected static uint KeyOf(CXCursor cursor) => cursor.CanonicalCursor.Hash;

    /// <summary>
    /// 剥去 typedef、elaborated 等语法糖，得到用于判定的规范类型。
    /// </summary>
    protected static CXType Canonical(CXType type)
    {
        CXType current = type;
        for (int guard = 0; guard < 64; guard++)
        {
            switch (current.kind)
            {
                case CXTypeKind.CXType_Elaborated:
                    current = current.NamedType;
                    break;
                case CXTypeKind.CXType_Attributed:
                    current = current.ModifiedType;
                    break;
                case CXTypeKind.CXType_Typedef:
                    {
                        CXCursor declaration = current.Declaration;
                        if (declaration.IsNull)
                            return current.CanonicalType;

                        current = declaration.TypedefDeclUnderlyingType;
                        break;
                    }
                case CXTypeKind.CXType_Unexposed:
                case (CXTypeKind)177: // CXType_Paren
                    {
                        CXType inner = current.CanonicalType;
                        if (inner == current)
                            return current;

                        current = inner;
                        break;
                    }
                default:
                    return current;
            }
        }

        return current;
    }

    // ---- 成员发现 ----

    protected virtual void DiscoverMembers(IEnumerable<CXCursor> declarations)
    {
        foreach (ShimTypeInfo type in Types)
        {
            // 构造函数 -> type_new，析构函数 -> type_finalize，其余成员函数 -> type_method
            AppendConstructors(type);
            AppendMethods(type);
            AppendFinalizer(type);
        }

        foreach (CXCursor declaration in declarations)
        {
            if (declaration.kind is not CXCursorKind.CXCursor_FunctionDecl)
                continue;

            if (IsAccessible(declaration) is false)
                continue;

            AppendFreeFunction(declaration);
        }
    }

    protected virtual void AppendConstructors(ShimTypeInfo type)
    {
        CXCursor definition = type.Declaration.Definition;
        if (definition.IsNull)
            return;

        // 类模板本身不是类型，`new QTypeInfo()` 这种写法无法编译（C2955/C2641）；
        // 它的实例化由参数/返回值位置按需以降级类型出现，不需要构造导出
        if (definition.kind is CXCursorKind.CXCursor_ClassTemplate
            or CXCursorKind.CXCursor_ClassTemplatePartialSpecialization)
        {
            return;
        }

        // 迭代器/哨兵等依附型类型没有独立的构造语义，其生命周期由所属容器掌握（C2512/C2514 残余来源）
        if (IsIteratorLikeType(definition))
            return;

        // 引用/代理类型（QBitRef、QArrayDataPointerRef 等）只是容器提供的临时视图，
        // 没有可用的默认构造，也不能由调用方 `new` 出来
        if (IsProxyType(definition))
            return;

        // 抽象类无法实例化（C2259），不能为它生成 new 导出
        if (definition.CXXRecord_IsAbstract)
            return;

        int count = definition.NumCtors;
        bool hasExplicit = false;
        for (int i = 0; i < count; i++)
        {
            CXCursor ctor = definition.GetCtor((uint)i);
            if (SkipMember(ctor))
                continue;

            if (IsSupportedReturn(ctor.ResultType) is false && ctor.ResultType.kind is not CXTypeKind.CXType_Void)
                continue;

            AddFunction(type, ctor, ShimRole.New, type.SymbolPrefix + "_new");
            hasExplicit = true;
        }

        if (hasExplicit)
            return;

        // 没有用户声明的构造函数时才补无参 new，但要求类型确实可默认构造：
        // 例如聚合类型里有成员不可默认构造、或默认构造被隐式删除时，`new X()` 会报 C2512/C2514
        if (!CanConstructDefault(definition))
            return;

        AddSynthetic(type, ShimRole.New, type.SymbolPrefix + "_new");
    }

    /// <summary>
    /// 类型能否用 <c>new X()</c> 构造出实例。
    /// </summary>
    /// <remarks>
    /// 比 <see cref="HasUsableDefaultConstructor"/> 更严格：除了字段可默认构造，
    /// 还要求存在可调用的默认构造。<c>QAdoptSharedDataTag</c> 只声明了带参构造、
    /// <c>QAbstractConcatenable</c> 只有删除的拷贝构造，二者都不能 <c>new X()</c>（C2514/C2641）。
    /// </remarks>
    private static bool CanConstructDefault(CXCursor definition)
    {
        // libclang 对「声明了任何构造函数」的类型不一定给出 NumCtors>0（模板里/被删除的情况尤其如此），
        // 因此统一按「有可调用的无参构造 + 字段可默认构造」判定；
        // 完全没有任何构造的类型由 HasDefaultConstructorOverload 的 count==0 分支放行（隐式默认构造）
        return HasDefaultConstructorOverload(definition)
            && HasUsableDefaultConstructor(definition);
    }

    /// <summary>
    /// 类型能否用 <c>new X()</c> 默认构造。
    /// </summary>
    /// <remarks>
    /// 只看“没有用户声明的构造函数”还不够：统一初始化语法、引用成员、常量成员等情况会让
    /// 隐式默认构造函数被删除。逐个字段检查可默认构造性，任一字段不满足即视为不可默认构造，
    /// 宁可少一个构造导出，也不要生成编译不过的 shim。
    /// </remarks>
    private static bool HasUsableDefaultConstructor(CXCursor definition)
    {
        int fieldCount = definition.NumFields;
        for (int i = 0; i < fieldCount; i++)
        {
            CXCursor field = definition.GetField((uint)i);
            if (!IsDefaultConstructible(field.Type))
                return false;
        }

        return true;
    }

    /// <summary>
    /// 字段类型是否可默认构造：标量/枚举/指针天然可以，记录类型递归判断，数组按元素判断。
    /// </summary>
    private static bool IsDefaultConstructible(CXType type, int depth = 0)
    {
        if (depth > 4)
            return false;

        CXType canonical = Canonical(type);
        switch (canonical.kind)
        {
            case CXTypeKind.CXType_Record:
                {
                    CXCursor declaration = canonical.Declaration;
                    CXCursor definition = declaration.Definition;
                    if (definition.IsNull)
                        return false;

                    if (definition.kind is CXCursorKind.CXCursor_ClassTemplate
                        or CXCursorKind.CXCursor_ClassTemplatePartialSpecialization)
                    {
                        return false;
                    }

                    if (definition.CXXRecord_IsAbstract)
                        return false;

                    // 有用户声明的构造函数时，只有存在无参重载才算可默认构造
                    return HasDefaultConstructorOverload(definition)
                        && HasUsableDefaultConstructor(definition);
                }

            case CXTypeKind.CXType_ConstantArray:
                return IsDefaultConstructible(canonical.ArrayElementType, depth + 1);

            case CXTypeKind.CXType_Void:
                return false;

            default:
                return true;
        }
    }

    /// <summary>
    /// 记录类型是否有可调用的无参构造函数；无用户声明构造函数时视为有隐式默认构造。
    /// </summary>
    /// <remarks>
    /// 只有「被删除的构造」或「全带参构造」时返回 false —— 这两种类型都无法 <c>new X()</c>。
    /// </remarks>
    private static bool HasDefaultConstructorOverload(CXCursor definition)
    {
        int count = definition.NumCtors;
        if (count is 0)
            return true;

        // 没有任何可用的无参构造就算不可默认构造；带参构造同理
        for (int i = 0; i < count; i++)
        {
            CXCursor ctor = definition.GetCtor((uint)i);
            if (ctor.IsDeleted)
                continue;

            if (ctor.NumArguments is 0)
                return true;
        }

        return false;
    }

    protected virtual void AppendFinalizer(ShimTypeInfo type)
    {
        CXCursor definition = type.Declaration.Definition;
        if (definition.IsNull)
            return;

        // 不可构造的类型根本无法从 C 侧得到实例，析构导出只会是死代码（且容易随模板名一起编译失败）
        if (IsUninstantiatedTemplate(definition) || !CanConstructDefault(definition))
            return;

        CXCursor destructor = definition.Destructor;
        if (destructor.IsNull)
            AddSynthetic(type, ShimRole.Finalize, type.SymbolPrefix + "_finalize");
        else if (SkipMember(destructor) is false)
            AddFunction(type, destructor, ShimRole.Finalize, type.SymbolPrefix + "_finalize");
    }

    /// <summary>
    /// 为没有对应原生声明的能力补充导出：默认构造与默认析构。
    /// </summary>
    protected virtual void AddSynthetic(ShimTypeInfo type, ShimRole role, string baseName)
    {
        string returnCType = role is ShimRole.New ? type.HandleName + "*" : "void";
        ShimFunctionInfo function = new(type, role, [], UniqueSymbol(baseName), returnCType, null, role is ShimRole.Finalize, null);
        Functions.Add(function);
        type.Functions.Add(function);
    }

    /// <summary>
    /// 登记一个派生类自定义的导出：参数表、声明与实现体都由调用方给出完整文本。
    /// </summary>
    /// <remarks>
    /// 供框架特化（如 Qt 信号连接）使用：基类只负责按导出上限分配到库与源文件，
    /// 以及写出导出宏、调用约定等统一形态。
    /// </remarks>
    /// <param name="owner">所属类型；为空表示全局函数。</param>
    /// <param name="baseName">符号基名，重名时自动追加序号。</param>
    /// <param name="returnCType">C 侧返回类型文本。</param>
    /// <param name="declarationParameters">形参表中每个形参的「类型 名字」文本，可为空表示 void。</param>
    /// <param name="body">函数体语句块原文。</param>
    protected ShimFunctionInfo AddCustomExport(
        ShimTypeInfo? owner,
        string baseName,
        string returnCType,
        IReadOnlyList<string> declarationParameters,
        string body)
    {
        ShimFunctionInfo function = new(
            owner,
            ShimRole.Extra,
            [],
            UniqueSymbol(baseName),
            returnCType,
            null,
            returnCType is "void",
            null)
        {
            ExtraDeclaration = string.Join(", ", declarationParameters),
            ExtraBody = body,
        };

        Functions.Add(function);
        owner?.Functions.Add(function);
        return function;
    }

    protected virtual void AppendMethods(ShimTypeInfo type)
    {
        CXCursor definition = type.Declaration.Definition;
        if (definition.IsNull)
            return;

        int count = definition.NumMethods;
        for (int i = 0; i < count; i++)
        {
            CXCursor method = definition.GetMethod((uint)i);

            // 构造/析构/转换函数由各自的角色分支处理，不能作为普通方法重复导出
            if (method.kind is CXCursorKind.CXCursor_Constructor
                or CXCursorKind.CXCursor_Destructor
                or CXCursorKind.CXCursor_ConversionFunction)
            {
                continue;
            }

            if (SkipMember(method))
                continue;

            if (method.CXXMethod_IsStatic)
                continue;

            if (method.CXXMethod_IsVirtual)
                continue;

            if (IsSupportedReturn(method.ResultType) is false)
                continue;

            AddFunction(type, method, ShimRole.Method, type.SymbolPrefix + "_" + ToIdentifier(method.Spelling.ToString()));
        }
    }

    /// <summary>
    /// 跳过无法正确桥接的成员：隐式、已删除、非 public。
    /// </summary>
    protected virtual bool SkipMember(CXCursor member)
        => member.IsImplicit || member.IsDeleted || IsAccessible(member) is false;

    private void AppendFreeFunction(CXCursor declaration)
    {
        string name = declaration.Spelling.ToString();
        if (name.Length is 0)
            return;

        // 头文件里还混着 Qt 的宏/内联工具函数与 CRT 的静态链接函数（qt_noop、qBadAlloc、
        // qYieldCpu、qSNaN、qQNaN、__security_init_cookie、_invalid_parameter_noinfo 等）。
        // 它们不是库的公开导出，生成 C 导出会与头顶已声明的同名实体冲突（C2375/C2732/C2475），
        // 且包装体调用同名函数会自我递归。
        if (IsNonBindableFreeFunction(declaration, name))
            return;

        // C++ 运算符作为自由函数同样无法在 C 侧表达（Qt 的 QArrayData 命名空间下有大量 operator+/-），
        // 且部分重载已被删除（C2280）
        if (name.StartsWith("operator", StringComparison.Ordinal))
            return;

        if (IsSupportedReturn(declaration.ResultType) is false)
            return;

        AddFunction(null, declaration, ShimRole.Free, ToIdentifier(name));
    }

    /// <summary>
    /// 无宿主前缀的自由函数是否不应成为 C 导出：内联函数、Qt 的 <c>q</c>/<c>qt_</c> 工具函数、CRT 保留名。
    /// </summary>
    /// <remarks>
    /// 只针对**没有** <c>--export-prefix</c> 前缀的名字：带前缀的自由函数是调用方用该前缀
    /// 明确约定引入的导出，即使名字看起来像 Qt 工具函数也照常生成。
    /// 判定按前缀族而不是逐个枚举名字：Qt 的 <c>q</c>/<c>qt_</c> 工具函数与 CRT 的 <c>__</c> 静态函数
    /// 会随版本不断新增，逐个列举必然漏网（alpha.9 就以 qErrnoWarning、qSNaN、qYieldCpu 等漏出 24 条错误）。
    /// </remarks>
    private bool IsNonBindableFreeFunction(CXCursor declaration, string name)
    {
        // 内联函数没有稳定的导出地址，链接器不会为它产生符号
        if (IsInlineFunction(declaration))
            return true;

        if (name.StartsWith(ShimOptions.ExportPrefix, StringComparison.Ordinal))
            return false;

        // Qt 工具函数族：即使被编译成非内联的库导出，也不是面向 C 的公开接口
        if (IsQtUtilityName(name))
            return true;

        // CRT / 编译器运行时：(__ 开头的所有保留名，例如 __report_gsfailure、__security_*、__CxxThrowException
        return name.StartsWith("__", StringComparison.Ordinal)
            || (name.StartsWith('_') && LooksLikeCrtReservedName(name));
    }

    /// <summary>
    /// Qt 工具函数名：<c>q</c> 前缀的宏/内联函数族与 <c>qt_</c> 内部命名空间族。
    /// </summary>
    /// <remarks>
    /// <c>q</c> 族按「首字符 <c>q</c>、第二字符是大写字母」判定（<c>qBadAlloc</c>、<c>qSNaN</c>、<c>qInf</c>、<c>qIsInf</c>、<c>qYieldCpu</c>），
    /// 这样不会误伤 <c>query</c>、<c>quit</c>、<c>qt_noop</c> 之外的普通业务函数名。
    /// </remarks>
    private static bool IsQtUtilityName(string name)
        => name.StartsWith("qt_", StringComparison.Ordinal)
            || (name.Length >= 2 && name[0] is 'q' && char.IsUpper(name[1]));

    /// <summary>
    /// 声明是否为内联函数。
    /// </summary>
    /// <remarks>
    /// libclang 不直接暴露该标志，按声明所在源码行的关键字判断；
    /// 同时覆盖 MSVC 的 <c>__inline</c> / <c>__forceinline</c> 与 GNU 的 <c>inline</c>。
    /// </remarks>
    private bool IsInlineFunction(CXCursor declaration)
    {
        declaration.Location.GetExpansionLocation(out CXFile file, out uint line, out _, out _);
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

        int offset = (int)line - 1;
        if (offset >= lines.Length)
            return false;

        // 声明可能跨行书写，把该行与下一行拼起来看关键字
        string text = offset + 1 < lines.Length ? lines[offset] + " " + lines[offset + 1] : lines[offset];
        return InlineKeywordRegex.IsMatch(text);
    }

    /// <summary>
    /// 源码行里出现 <c>inline</c> / <c>__inline</c> / <c>__forceinline</c> 关键字（按标识符边界匹配，避免误伤子串）。
    /// </summary>
    [GeneratedRegex(@"(?<![A-Za-z0-9_])(inline|__inline|__forceinline|__inline__)(?![A-Za-z0-9_])", RegexOptions.Compiled | RegexOptions.CultureInvariant)]
    private static partial Regex InlineKeywordRegex { get; }

    /// <summary>
    /// 形如 <c>_*</c> 且带工具集气息的保留名；用于兜住 CRT 未来的同类静态函数。
    /// </summary>
    /// <remarks>
    /// <c>__</c> 前缀已在 <see cref="IsNonBindableFreeFunction"/> 里整族排除，这里只处理单下划线族。
    /// </remarks>
    private static bool LooksLikeCrtReservedName(string name)
        => name.Contains("parameter", StringComparison.OrdinalIgnoreCase)
            || name.Contains("security", StringComparison.OrdinalIgnoreCase)
            || name.Contains("initterm", StringComparison.OrdinalIgnoreCase)
            || name.Contains("exe_entry", StringComparison.OrdinalIgnoreCase)
            || name.Contains("call_reportfault", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 记录一个导出项，并按参数类型生成唯一符号名。
    /// </summary>
    /// <remarks>
    /// 重载后缀规则：参数个数与参数类别组合，例如
    /// <c>foo</c>（无参）、<c>foo_i</c>（一个 int）、<c>foo_i_d</c>（int 与 double）。
    /// 类别字母：<c>p</c> 指针、<c>r</c> 引用、<c>b</c> bool、<c>c</c> 字符、<c>h</c> 短整型、
    /// <c>i</c> 整型、<c>l</c> 长整型、<c>ll</c> 64 位整型、<c>f</c> float、<c>d</c> double、<c>o</c> 记录、<c>e</c> 枚举。
    /// </remarks>
    protected virtual void AddFunction(ShimTypeInfo? owner, CXCursor declaration, ShimRole role, string baseName)
    {
        List<ShimParameterInfo> parameters = [];
        int argumentCount = declaration.NumArguments;
        for (int i = 0; i < argumentCount; i++)
            parameters.Add(BuildParameter(declaration.GetArgument((uint)i), i));

        if (parameters.Any(parameter => parameter.CType is null))
            return;

        string symbol = UniqueSymbol(BuildSymbol(baseName, parameters));

        // 按值返回的不可绑定记录类型不能映射成 cpp_shim_X*：C++ 侧 return 的是值本身，
        // 塞进指针返回位会报 C2440（Qt Core 实测 100 条，例如 QPropertyBindingError、QBindable<...>）
        if (role is not (ShimRole.New or ShimRole.Finalize) && IsUnbindableValueReturn(declaration.ResultType))
            return;

        // 构造函数对外始终返回句柄指针；析构返回 void；其余按原生返回值映射
        string returnCType = role switch
        {
            ShimRole.New => owner!.HandleName + "*",
            ShimRole.Finalize => "void",
            _ => ReturnCType(declaration.ResultType, handleFallback: null) ?? "void",
        };
        bool returnsVoid = role is ShimRole.Finalize
            || Canonical(declaration.ResultType).kind is CXTypeKind.CXType_Void;
        bool returnsEnum = role is not (ShimRole.New or ShimRole.Finalize)
            && Canonical(declaration.ResultType).kind is CXTypeKind.CXType_Enum;

        // 返回位一致性校验：C 侧返回类型必须能承接 C++ 的实际返回值。
        // 不满足就不生成该导出——宁可少一个绑定，也不要产出编译不过的产物（C2440）
        if (role is not (ShimRole.New or ShimRole.Finalize)
            && IsInconsistentReturn(declaration.ResultType, returnCType))
        {
            return;
        }

        ShimFunctionInfo function = role switch
        {
            ShimRole.Method => new(
                owner,
                role,
                parameters,
                symbol,
                returnCType,
                declaration.Spelling.ToString(),
                returnsVoid,
                null)
            {
                ReturnsEnum = returnsEnum,
            },
            ShimRole.Free => new(
                owner,
                role,
                parameters,
                symbol,
                returnCType,
                null,
                returnsVoid,
                QualifiedName(declaration))
            {
                ReturnsEnum = returnsEnum,
            },
            _ => new(owner, role, parameters, symbol, returnCType, null, returnsVoid, null),
        };

        Functions.Add(function);
        owner?.Functions.Add(function);
    }

    protected virtual ShimParameterInfo BuildParameter(CXCursor argument, int index)
    {
        CXType canonical = Canonical(argument.Type);
        string name = argument.Spelling.ToString() is { Length: > 0 } spelling
            ? ToIdentifier(spelling)
            : "arg" + index.ToString(CultureInfo.InvariantCulture);

        string cType = ParameterCType(canonical, indirect: false)!;
        string cppType = CppTypeOf(canonical);
        return new(name, canonical, ParameterSuffix(canonical), cType, cppType, BuildToCppExpression(name, canonical, cType, cppType));
    }

    /// <summary>
    /// 把 C 形参还原成 C++ 调用实参的默认表达式；派生类可覆盖以插入框架特有的转换。
    /// </summary>
    protected virtual string BuildToCppExpression(string name, CXType canonical, string cType, string cppType)
        => canonical.kind switch
        {
            CXTypeKind.CXType_LValueReference or CXTypeKind.CXType_RValueReference
                => "*reinterpret_cast<" + cppType + "*>(" + name + ")",
            CXTypeKind.CXType_Record
                => "*reinterpret_cast<" + cppType + "*>(" + name + ")",
            CXTypeKind.CXType_Enum
                => "static_cast<" + cppType + ">(" + name + ")",
            // 指针形参的 cppType 已包含完整指针层级，这里原样强转，不能再补 *
            CXTypeKind.CXType_Pointer when cType is "void*"
                => "static_cast<" + cppType + ">(" + name + ")",
            CXTypeKind.CXType_Pointer
                => "reinterpret_cast<" + cppType + ">(" + name + ")",
            _ => name,
        };

    /// <summary>
    /// C++ 侧的类型文本，用于在 shim 实现里把 C 值强转回真实类型。
    /// </summary>
    /// <remarks>
    /// 指针会保留目标类型的 <c>const</c> 与 <c>volatile</c> 限定，
    /// 否则还原出来的指针无法传给只接受只读指针的 C++ 成员函数。
    /// </remarks>
    protected virtual string CppTypeOf(CXType canonical)
        => canonical.kind switch
        {
            CXTypeKind.CXType_Bool => "bool",
            // C 侧 char 映射为 char，C++ 还原时同样用 char，避免与只接受 char* 的接口不兼容
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
            CXTypeKind.CXType_Record => RecordCppName(canonical),
            CXTypeKind.CXType_Enum => QualifiedName(canonical.Declaration),
            CXTypeKind.CXType_Pointer => CppPointeeOf(canonical) + "*",
            CXTypeKind.CXType_LValueReference or CXTypeKind.CXType_RValueReference
                => CppPointeeOf(canonical),
            _ => "int",
        };

    /// <summary>
    /// 指针或引用的目标类型文本。
    /// </summary>
    protected virtual string CppPointeeOf(CXType canonical)
    {
        CXType pointee = Canonical(canonical.PointeeType);
        string qualifier = QualifierPrefix(pointee);
        if (pointee.kind is CXTypeKind.CXType_Void)
            return qualifier + "void";

        if (pointee.kind is CXTypeKind.CXType_Record)
            return qualifier + RecordCppName(pointee);

        if (pointee.kind is CXTypeKind.CXType_Enum)
            return qualifier + QualifiedName(pointee.Declaration);

        return qualifier + CppTypeOf(pointee);
    }

    /// <summary>
    /// 记录类型的 C++ 完整拼写：模板实例必须带参数列表。
    /// </summary>
    /// <remarks>
    /// <c>QualifiedName</c> 只沿语义父级拼名字，模板实例会退化成裸模板名
    /// （<c>QFlags</c>、<c>std::chrono::duration</c>），在 C++ 侧直接报 C2955。
    /// 因此优先采用 libclang 给的完整拼写；只有在拼写缺失时才回退到限定名。
    /// </remarks>
    private static string RecordCppName(CXType type)
    {
        string spelling = type.Spelling.ToString();
        if (spelling.Length is not 0)
            return spelling;

        CXCursor declaration = type.Declaration;
        return declaration.IsNull ? "void" : QualifiedName(declaration);
    }

    /// <summary>
    /// 目标类型的 <c>const</c> 限定前缀。
    /// </summary>
    private static string QualifierPrefix(CXType type)
        => type.IsConstQualified ? "const " : "";

    /// <summary>
    /// 重载后缀：由参数类别字母拼接。
    /// </summary>
    protected static string BuildSymbol(string baseName, List<ShimParameterInfo> parameters)
    {
        if (parameters.Count is 0)
            return baseName;

        string suffix = string.Concat(parameters.Select(parameter => parameter.Suffix));
        return baseName + "_" + suffix;
    }

    protected virtual string ParameterSuffix(CXType canonical)
        => canonical.kind switch
        {
            CXTypeKind.CXType_Pointer => "p",
            CXTypeKind.CXType_LValueReference or CXTypeKind.CXType_RValueReference => "r",
            CXTypeKind.CXType_Bool => "b",
            CXTypeKind.CXType_Char_S or CXTypeKind.CXType_Char_U or CXTypeKind.CXType_SChar
                or CXTypeKind.CXType_UChar => "c",
            CXTypeKind.CXType_Short or CXTypeKind.CXType_UShort => "h",
            CXTypeKind.CXType_Int or CXTypeKind.CXType_UInt => "i",
            CXTypeKind.CXType_Long or CXTypeKind.CXType_ULong => "l",
            CXTypeKind.CXType_LongLong or CXTypeKind.CXType_ULongLong => "ll",
            CXTypeKind.CXType_Float => "f",
            CXTypeKind.CXType_Double => "d",
            CXTypeKind.CXType_Record => "o",
            CXTypeKind.CXType_Enum => "e",
            _ => "x",
        };

    /// <summary>
    /// 记录类型的 C 指针文本；未实例化模板等无合法 C 侧名称的情形降级为 <c>void*</c>。
    /// </summary>
    private string? HandlePointerOrNull(CXCursor declaration, string suffix)
    {
        string? handle = ResolveHandleName(declaration);
        if (handle is null)
            return "void" + suffix;

        return handle + suffix;
    }

    /// <summary>
    /// 参数对应的 C 类型文本；返回 null 表示该类型无法桥接到 C。
    /// </summary>
    protected virtual string? ParameterCType(CXType canonical, bool indirect)
    {
        switch (canonical.kind)
        {
            case CXTypeKind.CXType_Pointer:
                {
                    CXType pointee = Canonical(canonical.PointeeType);
                    if (pointee.kind is CXTypeKind.CXType_Void)
                        return "void*";

                    if (pointee.kind is CXTypeKind.CXType_Record)
                        return indirect ? HandlePointerOrNull(pointee.Declaration, "**") : "void*";

                    string? scalar = ScalarCType(pointee);
                    return scalar is null ? null : scalar + "*";
                }

            case CXTypeKind.CXType_LValueReference:
            case CXTypeKind.CXType_RValueReference:
                {
                    // 引用在 C 侧一律降级为指针，实现里再解引用
                    CXType pointee = Canonical(canonical.PointeeType);
                    if (pointee.kind is CXTypeKind.CXType_Record)
                        return indirect ? HandlePointerOrNull(pointee.Declaration, "**") : "void*";

                    string? scalar = ScalarCType(pointee);
                    return scalar is null ? null : scalar + "*";
                }

            case CXTypeKind.CXType_Record:
                return indirect
                    ? HandlePointerOrNull(canonical.Declaration, "**")
                    : HandlePointerOrNull(canonical.Declaration, "*");

            case CXTypeKind.CXType_Enum:
                return "int32_t";

            default:
                return ScalarCType(canonical);
        }
    }

    /// <summary>
    /// 标量类型对应的 C 文本；用户自定义类型返回 <c>null</c>（拼不出合法 C 指针）。
    /// </summary>
    /// <remarks>
    /// 与 <see cref="ScalarCType"/> 的区别：记录/枚举/函数原型等非标量一律返回 <c>null</c>，
    /// 用于判断「能不能作为 T 出现在 C 侧指针里」。
    /// </remarks>
    private string? ScalarCTypeOrNull(CXType type)
        => Canonical(type).kind switch
        {
            CXTypeKind.CXType_Char_S or CXTypeKind.CXType_Char_U
                or CXTypeKind.CXType_SChar or CXTypeKind.CXType_UChar
                or CXTypeKind.CXType_Short or CXTypeKind.CXType_UShort
                or CXTypeKind.CXType_Int or CXTypeKind.CXType_UInt
                or CXTypeKind.CXType_Long or CXTypeKind.CXType_ULong
                or CXTypeKind.CXType_LongLong or CXTypeKind.CXType_ULongLong
                or CXTypeKind.CXType_Float or CXTypeKind.CXType_Double
                or CXTypeKind.CXType_Bool
                => ScalarCType(Canonical(type)),
            _ => null,
        };

    protected virtual string? ScalarCType(CXType canonical)
        => canonical.kind switch
        {
            CXTypeKind.CXType_Void => "void",
            CXTypeKind.CXType_Bool => "bool",
            // char 在 C 侧用 char，便于与 UTF-8 字符串 marshaller 对应
            CXTypeKind.CXType_Char_S or CXTypeKind.CXType_Char_U => "char",
            // Qt 的 QChar::unicode() 返回 char16_t，必须映射成定宽整型，否则返回位会退化成 void（C2562）
            CXTypeKind.CXType_Char16 => "uint16_t",
            CXTypeKind.CXType_Char32 => "uint32_t",
            CXTypeKind.CXType_SChar => "int8_t",
            CXTypeKind.CXType_UChar => "uint8_t",
            CXTypeKind.CXType_UShort => "uint16_t",
            CXTypeKind.CXType_Short => "int16_t",
            CXTypeKind.CXType_Int => "int32_t",
            CXTypeKind.CXType_UInt => "uint32_t",
            CXTypeKind.CXType_Long or CXTypeKind.CXType_LongLong => "int64_t",
            CXTypeKind.CXType_ULong or CXTypeKind.CXType_ULongLong => "uint64_t",
            CXTypeKind.CXType_Float => "float",
            CXTypeKind.CXType_Double => "double",
            _ => null,
        };

    protected virtual bool IsSupportedReturn(CXType type)
        => Canonical(type).kind is CXTypeKind.CXType_Void
            or CXTypeKind.CXType_Pointer
            or CXTypeKind.CXType_LValueReference
            or CXTypeKind.CXType_RValueReference
            or CXTypeKind.CXType_Record
            or CXTypeKind.CXType_Enum
            or CXTypeKind.CXType_Bool
            or CXTypeKind.CXType_Char_S or CXTypeKind.CXType_Char_U or CXTypeKind.CXType_SChar
            or CXTypeKind.CXType_UChar or CXTypeKind.CXType_Short or CXTypeKind.CXType_UShort
            or CXTypeKind.CXType_Int or CXTypeKind.CXType_UInt or CXTypeKind.CXType_Long
            or CXTypeKind.CXType_ULong or CXTypeKind.CXType_LongLong or CXTypeKind.CXType_ULongLong
            or CXTypeKind.CXType_Float or CXTypeKind.CXType_Double;

    /// <summary>
    /// 返回值的 C 类型文本；记录类型返回对应句柄指针，无法表达时返回 null。
    /// </summary>
    protected virtual string? ReturnCType(CXType resultType, string? handleFallback)
    {
        CXType result = Canonical(resultType);
        if (result.kind is CXTypeKind.CXType_Void)
            return "void";

        if (result.kind is CXTypeKind.CXType_Record)
        {
            // 按值返回的 record 一律不生成导出（C++ 侧返回的是对象本身），这里只服务剩余的指针语义场景；
            // 未实例化模板同样降级为 void*
            return HandlePointerOrNull(result.Declaration, "*");
        }

        if (result.kind is CXTypeKind.CXType_Enum)
            return "int32_t";

        // 标量必须在指针分支之前返回：否则形如 char16_t/ushort 的值返回会多拼一层 `*`
        // （QChar::unicode 曾因此生成 uint16_t*，C2440）
        if (ScalarCType(result) is { } scalarType)
            return scalarType;

        if (result.kind is CXTypeKind.CXType_Pointer
            or CXTypeKind.CXType_LValueReference
            or CXTypeKind.CXType_RValueReference)
        {
            return ParameterCType(result, indirect: false);
        }

        return handleFallback;
    }

    /// <summary>
    /// 返回值是否属于「无法安全桥接到 C」的记录类型（按值或以引用返回）。
    /// </summary>
    /// <remarks>
    /// shim 对记录类型一律用不透明句柄指针表达，而 C++ 侧 <c>return</c> 的是对象本身或引用，
    /// 塞进 <c>cpp_shim_X*</c> / <c>void*</c> 返回位都会报 C2440。
    /// C 侧既没有这些类型的完整定义，也无法安全地把它当成句柄长期持有，因此一律不生成导出。
    /// 必须**剥开引用**再看内层类型：Qt 常常 <c>QByteArray&amp;</c>、<c>QAnyStringView&amp;</c> 这样按引用返回，
    /// canonical kind 会是 <c>LValueReference</c> 而不是 <c>Record</c>，只判 Record 会全部漏网。
    /// 指针返回不受影响（它本来就是句柄语义）。
    /// </remarks>
    private static bool IsUnbindableValueReturn(CXType resultType)
    {
        CXType shape = PeelReference(Canonical(resultType));
        return shape.kind is CXTypeKind.CXType_Record;
    }

    /// <summary>
    /// 剥掉引用限定（引用指向的目标类型）。
    /// </summary>
    private static CXType PeelReference(CXType type)
    {
        CXType current = type;
        for (int guard = 0; guard < 8; guard++)
        {
            if (current.kind is not (CXTypeKind.CXType_LValueReference or CXTypeKind.CXType_RValueReference))
                return current;

            current = Canonical(current.PointeeType);
        }

        return current;
    }

    /// <summary>
    /// 名称表明是迭代器/哨兵/私有信号之类的「依附型」类型。
    /// </summary>
    private static bool IsIteratorLikeType(CXCursor declaration)
    {
        string name = declaration.Spelling.ToString();
        return name.Contains("iterator", StringComparison.OrdinalIgnoreCase)
            || name.Contains("const_iterator", StringComparison.OrdinalIgnoreCase)
            || name is "It" or "Sentinel" or "_Sentinel"
            || name.EndsWith("Iterator", StringComparison.Ordinal)
            || name.EndsWith("ConstIterator", StringComparison.Ordinal);
    }

    /// <summary>
    /// 引用/代理类型：容器或算法提供的临时视图，不能由调用方构造。
    /// </summary>
    /// <remarks>
    /// 判定组合「名字以 <c>Ref</c> / <c>PointerRef</c> 之类视图名结尾 + 没有用户声明的公开构造函数」。
    /// 代理类型通常不声明构造函数（隐含的默认构造在这种「只由容器产生」的视图上没有意义），
    /// 因此这里判断的是「是否有用户声明的构造」，而不是「能否默认构造」。
    /// </remarks>
    private static bool IsProxyType(CXCursor declaration)
    {
        string name = declaration.Spelling.ToString();
        bool nameSuggestsProxy = name.EndsWith("Ref", StringComparison.Ordinal)
            || name.EndsWith("Refs", StringComparison.Ordinal)
            || name.EndsWith("PointerRef", StringComparison.Ordinal);

        if (nameSuggestsProxy is false)
            return false;

        return HasUserDeclaredConstructor(declaration) is false;
    }

    /// <summary>
    /// 记录类型是否声明了任何（非隐式、非删除的）构造函数。
    /// </summary>
    /// <summary>
    /// 记录类型是否在源码中声明了任何构造函数。
    /// </summary>
    /// <remarks>
    /// 不能依赖 <c>CXCursor.IsImplicit</c>：只有声明、没有定义的构造函数也会被 libclang 标记为 implicit
    /// （实测 <c>QRef(int);</c> → <c>implicit=True</c>）。改为按「有参数」或「有源码位置」判断用户声明：
    /// 隐式生成的默认构造既没有参数、也指向类声明所在行。
    /// </remarks>
    private static bool HasUserDeclaredConstructor(CXCursor definition)
    {
        int count = definition.NumCtors;
        for (int i = 0; i < count; i++)
        {
            CXCursor ctor = definition.GetCtor((uint)i);
            if (ctor.IsDeleted)
                continue;

            // 有参数的重载必然是用户显式声明的
            if (ctor.NumArguments is not 0)
                return true;

            // 无参情形：隐式默认构造的位置与类声明重合，用户声明的则另有独立的源码位置
            if (HasOwnSourceLocation(definition, ctor))
                return true;
        }

        return false;
    }

    /// <summary>
    /// 构造函数的源码位置是否与类声明不同（即确实是源码里写出来的）。
    /// </summary>
    private static bool HasOwnSourceLocation(CXCursor definition, CXCursor ctor)
    {
        definition.Location.GetExpansionLocation(out CXFile definitionFile, out uint definitionLine, out _, out _);
        ctor.Location.GetExpansionLocation(out CXFile ctorFile, out uint ctorLine, out _, out _);
        if (ctorLine is 0 || definitionLine is 0)
            return false;

        return ctorFile.Name.ToString() != definitionFile.Name.ToString() || ctorLine != definitionLine;
    }

    protected string UniqueSymbol(string name)
    {        string candidate = name;
        int suffix = 1;
        while (!UsedSymbols.Add(candidate))
        {
            suffix++;
            candidate = name + "_" + suffix.ToString(CultureInfo.InvariantCulture);
        }

        return candidate;
    }

    // ---- 名称 ----

    /// <summary>
    /// C++ 限定名（命名空间与嵌套类型用 <c>::</c> 连接）。
    /// </summary>
    protected static string QualifiedName(CXCursor declaration)
    {
        List<string> parts = [];
        CXCursor current = declaration;
        for (int guard = 0; guard < 16; guard++)
        {
            string spelling = current.Spelling.ToString();
            if (spelling.Length is not 0)
                parts.Add(spelling);

            CXCursor parent = current.SemanticParent;
            if (parent.IsNull || parent.kind is CXCursorKind.CXCursor_TranslationUnit)
                break;

            current = parent;
        }

        return string.Join("::", Enumerable.Reverse(parts));
    }

    /// <summary>
    /// 把任意名称转成合法的 C 标识符。
    /// </summary>
    protected static string ToIdentifier(string name)
    {
        using StringWriter writer = CreateWriter();
        if (name.Length is 0)
            return "_";

        if (char.IsDigit(name[0]))
            writer.Write('_');

        foreach (char character in name)
            writer.Write(char.IsLetterOrDigit(character) || character is '_' ? character : '_');

        return writer.ToString();
    }

    // ---- 输出 ----

    /// <summary>
    /// 按导出上限把函数划分到若干共享库与源文件。
    /// </summary>
    /// <remarks>
    /// 第一个库的名称即 <see cref="CppShimGeneratorOptions.TargetName"/>，后续库追加序号。
    /// </remarks>
    protected virtual List<ShimLibrary> PlanLibraries()
    {
        List<ShimLibrary> libraries = [];
        int libraryIndex = 0;
        int sourceIndex = 0;

        ShimLibrary? library = null;
        ShimSource? source = null;

        foreach (ShimFunctionInfo function in Functions)
        {
            if (library is null || library.Functions.Count >= ShimOptions.MaxExportsPerLibrary)
            {
                libraryIndex++;
                string name = libraryIndex is 1
                    ? ShimOptions.TargetName
                    : ShimOptions.TargetName + "." + libraryIndex.ToString(CultureInfo.InvariantCulture);

                library = new ShimLibrary(name, libraryIndex);
                libraries.Add(library);
                sourceIndex = 0;
                source = null;
            }

            if (source is null || source.Functions.Count >= ShimOptions.MaxExportsPerSource)
            {
                sourceIndex++;
                source = new ShimSource(sourceIndex);
                library.Sources.Add(source);
            }

            source.Functions.Add(function);
            library.Functions.Add(function);
            function.Library = library.Name;
        }

        // 每个库至少要有一个翻译单元，否则 CMake 目标没有源文件
        if (libraries.Count is 0)
        {
            ShimLibrary primary = new(ShimOptions.TargetName, 1);
            primary.Sources.Add(new ShimSource(1));
            libraries.Add(primary);
        }

        return libraries;
    }

    /// <summary>
    /// 写出前的自检：直接扫描**待写出的文本**，不再依赖登记阶段的判定缓存。
    /// </summary>
    /// <remarks>
    /// 缓存判定曾经因为逻辑写反而整体失效（B5），因此这里的防线必须与判定无关：
    /// 只要实际写出的 `.cpp` 里出现 <c>new 裸模板名()</c> 或 <c>reinterpret_cast&lt;裸模板名*&gt;</c>，
    /// 就说明产物编译不过，直接让生成失败并列出类型名。
    /// 判据仍只依赖 AST/文本本身，不维护名字黑名单——逐个列举必然随版本漏网。
    /// </remarks>
    private static void AssertNoBareTemplateNames(IEnumerable<GeneratedFile> files, HashSet<string> templateNames)
    {
        if (templateNames.Count is 0)
            return;

        List<string> offenders = [];
        foreach (GeneratedFile file in files)
        {
            if (file.Name.EndsWith(".cpp", StringComparison.Ordinal) is false)
                continue;

            foreach (string templateName in templateNames)
            {
                // 只关心「裸名」用法：名字后紧跟 ( 、* 或 ::，且不是带参数列表的合法实例化
                if (ContainsBareNameUsage(file.Content, templateName)
                    && offenders.Contains(templateName, StringComparer.Ordinal) is false)
                {
                    offenders.Add(templateName);
                }
            }
        }

        if (offenders.Count is 0)
            return;

        offenders.Sort(StringComparer.Ordinal);
        throw new InvalidOperationException(
            "生成的 shim 中出现了未实例化的类模板名，C++ 侧无法编译：" + string.Join(", ", offenders));
    }

    /// <summary>
    /// 文本中是否存在对 <paramref name="name"/> 的裸用法（不带模板参数列表）。
    /// </summary>
    /// <remarks>
    /// 逐处定位名字出现位置，检查其后是否紧跟 <c>&lt;</c>：紧跟即合法实例化，跳过；
    /// 否则再检查紧跟的是 <c>(</c>、<c>*</c> 或 <c>::</c> 才算裸用法（避免把注释、字符串或更长标识符误判）。
    /// </remarks>
    private static bool ContainsBareNameUsage(string content, string name)
    {
        int index = content.IndexOf(name, StringComparison.Ordinal);
        while (index >= 0)
        {
            int after = index + name.Length;
            bool startsLongerIdentifier = after < content.Length
                && (char.IsLetterOrDigit(content[after]) || content[after] is '_');
            bool hasTemplateArguments = after < content.Length && content[after] is '<';

            if (startsLongerIdentifier is false && hasTemplateArguments is false && after < content.Length)
            {
                char next = content[after];
                if (next is '(' or '*' or ':')
                    return true;
            }

            index = content.IndexOf(name, index + 1, StringComparison.Ordinal);
        }

        return false;
    }

    /// <summary>
    /// 登记阶段识别出的未实例化模板名（C++ 侧拼写，不含模板参数）。
    /// </summary>
    /// <remarks>
    /// 在翻译单元存活期间收集，供写出前的文本自检使用；不维护手写黑名单。
    /// </remarks>
    private HashSet<string> UninstantiatedTemplateNames()
    {
        HashSet<string> names = [with(StringComparer.Ordinal)];
        foreach (ShimTypeInfo type in Types)
        {
            if (IsUninstantiatedTemplateByCache(type))
                names.Add(type.CppName);
        }

        return names;
    }

    /// <summary>
    /// 依据登记时缓存的判定结果（输出阶段不能访问 cursor）。
    /// </summary>
    private static bool IsUninstantiatedTemplateByCache(ShimTypeInfo type)
        => type.IsUninstantiatedTemplate;

    protected virtual void WriteOutputs()
    {
        Directory.CreateDirectory(ShimOptions.Output.FullName);

        string headerName = ShimOptions.BaseName + ".h";
        string pchName = ShimOptions.BaseName + ".pch.h";
        string cmakeName = ShimOptions.BaseName + ".cmake";

        List<ShimLibrary> libraries = PlanLibraries();

        Files.Clear();
        Files.Add(new(headerName, BuildHeader()));
        Files.Add(new(pchName, BuildPch()));
        Files.Add(new(cmakeName, BuildCMake(libraries)));

        foreach (ShimLibrary library in libraries)
        {
            foreach (ShimSource source in library.Sources)
                Files.Add(new(SourceFileName(library, source, libraries.Count), BuildSource(library, source)));
        }

        // 导出符号 → 库名清单：CBindingGenerator 据此生成每个函数的 LibraryImport 库名
        Files.Add(new(ShimOptions.LibraryManifestFileName, BuildLibraryManifest(libraries)));

        // 写出前断言：扫描实际输出文本中的已知模板名（名单来自 AST 判定，不维护手写黑名单）
        AssertNoBareTemplateNames(Files, UninstantiatedTemplateNames());

        // 写正文，再释放嵌入的转换头文件，最后清理本轮未产出的旧产物
        foreach (GeneratedFile file in Files)
            WriteTextFile(Path.Combine(ShimOptions.Output.FullName, file.Name), file.Content);

        if (ShimOptions.WriteMarshaller)
            CppShimMarshal.WriteTo(ShimOptions.Output.FullName);

        RemoveStaleOutputs([.. Files.Select(file => file.Name), CppShimMarshal.FileName]);

        if (ShimOptions.Quiet)
            return;

        Console.WriteLine(
            $"C++ shim 生成了 {Functions.Count} 个导出、{Types.Count} 个类型、{libraries.Count} 个库。");
    }

    /// <summary>
    /// 源文件名：单库时用 <c>基名.序号.cpp</c>，多库时用 <c>基名.库序号.源序号.cpp</c>。
    /// </summary>
    /// <remarks>
    /// 多库时两段式命名可避免 <c>基名.2.cpp</c> 与 <c>基名.2.1.cpp</c> 这类难以分辨的组合。
    /// </remarks>
    protected virtual string SourceFileName(ShimLibrary library, ShimSource source, int libraryCount)
        => libraryCount is 1
            ? ShimOptions.BaseName + "." + source.Index.ToString(CultureInfo.InvariantCulture) + ".cpp"
            : ShimOptions.BaseName + "." + library.Index.ToString(CultureInfo.InvariantCulture)
                + "." + source.Index.ToString(CultureInfo.InvariantCulture) + ".cpp";

    /// <summary>
    /// CMake 清单里显式库列表的变量名：目标名转大写，非字母数字字符统一替换为下划线。
    /// </summary>
    /// <remarks>
    /// CMake 目标名允许 <c>.</c>（如 <c>OBSharp.Interop.Qt.Native</c>），但带点号的变量名在
    /// <c>${}</c> / <c>LISTS</c> 中容易与组件语法混淆，因此列表变量一律规范化成字母数字与下划线。
    /// </remarks>
    private string LibraryListVariableName()
    {
        StringBuilder builder = new(ShimOptions.TargetName.Length);
        foreach (char character in ShimOptions.TargetName.ToUpperInvariant())
            builder.Append(char.IsLetterOrDigit(character) ? character : '_');

        return builder + "_LIBRARIES";
    }

    /// <summary>
    /// 导出符号到库名的映射清单，供 <see cref="CBindingGenerator"/> 使用。
    /// </summary>
    protected virtual string BuildLibraryManifest(List<ShimLibrary> libraries)
    {
        using StringWriter writer = CreateWriter();
        writer.WriteLine("# 导出符号 → 所属共享库；供 CBindingGenerator 生成 LibraryImport 库名。");
        writer.WriteLine("# 每行：<符号><TAB><库名>");
        writer.WriteLine();
        foreach (ShimLibrary library in libraries)
        {
            foreach (ShimFunctionInfo function in library.Functions)
            {
                writer.Write(library.Name);
                writer.Write('\t');
                writer.WriteLine(function.Symbol);
            }
        }

        return writer.ToString();
    }

    /// <summary>
    /// 删除本轮并未产出的旧文件，避免残留头文件与最新实现不一致。
    /// </summary>
    /// <param name="produced">本轮实际写出的文件名集合。</param>
    private void RemoveStaleOutputs(HashSet<string> produced)
    {
        foreach (FileInfo file in ShimOptions.Output.EnumerateFiles("*", SearchOption.TopDirectoryOnly))
        {
            if (produced.Contains(file.Name))
                continue;

            // 只清理本生成器负责的扩展名，其它文件属于调用方
            if (file.Extension is ".h" or ".hpp" or ".cpp" or ".cmake")
                file.Delete();
        }
    }

    /// <summary>
    /// shim 头文件：句柄前置声明块 + extern "C" 导出声明。
    /// </summary>
    /// <remarks>
    /// 前置声明块把全部句柄类型统一声明一次，既避免「先引用后 typedef」在 <c>extern "C"</c> 下被当成未知类型，
    /// 也把同名句柄（模板实例、宏生成名）归一到一次输出。
    /// </remarks>
    protected virtual string BuildHeader()
    {
        using StringWriter writer = CreateWriter();
        writer.WriteLine("// 由 OBSharp.Interop.NativeBindGenerator 生成：C++ 类型的 C 导出层。");
        writer.WriteLine("// 供 CBindingGenerator 按纯 C 声明生成 C# 绑定，请勿手工修改。");
        writer.WriteLine("#pragma once");
        writer.WriteLine();
        writer.WriteLine("#include <stdint.h>");
        writer.WriteLine("#include \"" + CppShimMarshal.FileName + "\"");
        writer.WriteLine();
        writer.WriteLine("#if defined(__cplusplus)");
        writer.WriteLine("extern \"C\" {");
        writer.WriteLine("#endif");
        writer.WriteLine();

        WriteHandleDeclarations(writer);

        foreach (ShimFunctionInfo function in Functions)
            WriteFunctionDeclaration(writer, function);

        writer.WriteLine("#ifdef __cplusplus");
        writer.WriteLine("}");
        writer.WriteLine("#endif");
        return writer.ToString();
    }

    /// <summary>
    /// 统一的句柄前置声明块：每个句柄名只声明一次，已登记类型额外附上原生类型注释。
    /// </summary>
    private void WriteHandleDeclarations(StringWriter writer)
    {
        if (HandleDeclarations.Count is 0)
            return;

        writer.WriteLine("// 句柄前置声明：所有被引用的不透明类型，避免在 extern \"C\" 下出现未知类型。");

        // 已登记类型先按注释分组输出，保证「原生类型 → 句柄名」的对照信息仍在，且每个名字只声明一次
        HashSet<string> written = [with(StringComparer.Ordinal)];
        foreach (ShimTypeInfo type in Types)
        {
            if (!written.Add(type.HandleName))
                continue;

            writer.WriteLine("// " + type.Keyword + " " + type.CppName);
            writer.WriteLine("typedef struct " + type.HandleName + " " + type.HandleName + ";");
        }

        // 其余只在参数/返回值里出现过的临时类型（嵌套类、模板实例等）没有成员导出，只补声明
        foreach (string handle in HandleDeclarations.OrderBy(value => value, StringComparer.Ordinal))
        {
            if (written.Add(handle))
                writer.WriteLine("typedef struct " + handle + " " + handle + ";");
        }

        writer.WriteLine();
    }

    protected virtual void WriteFunctionDeclaration(StringWriter writer, ShimFunctionInfo function)
    {
        using StringWriter line = CreateWriter();

        // 必须带导出标记，否则 DLL 不产生导出表，C# 的 LibraryImport 运行时找不到入口
        line.Write("CPP_SHIM_EXPORT ");
        line.Write(function.ReturnCType);
        line.Write(" CPP_SHIM_CALL ");
        line.Write(function.Symbol);
        line.Write('(');

        // 自定义导出自带完整参数表；其余由函数签名推导
        line.Write(function.Role is ShimRole.Extra && function.ExtraDeclaration is not null
            ? function.ExtraDeclaration
            : DeclarationParameters(function));
        line.Write(");");
        writer.WriteLine(line.ToString());
    }

    /// <summary>
    /// 声明中的参数列表；实例成员在首位插入 self 句柄。
    /// </summary>
    protected virtual string DeclarationParameters(ShimFunctionInfo function)
    {
        List<string> parts = [];

        // 构造没有 self；析构与实例方法的首位是对应句柄
        if (function.Owner is not null && function.Role is not ShimRole.New)
            parts.Add(function.Owner.HandleName + "* self");

        foreach (ShimParameterInfo parameter in function.Parameters)
            parts.Add(parameter.CType!);

        return parts.Count is 0 ? "void" : string.Join(", ", parts);
    }
    /// <summary>
    /// 单个实现文件：先包含原始头文件让真实类型可见，再包含 shim 头文件取得导出声明。
    /// </summary>
    protected virtual string BuildSource(ShimLibrary library, ShimSource source)
    {
        using StringWriter writer = CreateWriter();
        writer.WriteLine("// 由 OBSharp.Interop.NativeBindGenerator 生成：C++ 类型的 C 导出层实现。");
        writer.WriteLine("// 所属库：" + library.Name + "，请勿手工修改。");
        writer.WriteLine("#include \"" + ShimOptions.BaseName + ".pch.h\"");
        writer.WriteLine("#include \"" + ShimOptions.BaseName + ".h\"");
        writer.WriteLine();

        foreach (ShimFunctionInfo function in source.Functions)
            WriteFunctionDefinition(writer, function);

        return writer.ToString();
    }

    protected virtual void WriteFunctionDefinition(StringWriter writer, ShimFunctionInfo function)
    {
        // 自定义导出的形参表来自派生类给出的原文
        string parameters = function.Role is ShimRole.Extra && function.ExtraDeclaration is not null
            ? function.ExtraDeclaration
            : DefinitionParameters(function);

        if (function.Role is ShimRole.Extra && function.ExtraDeclaration is null)
            parameters = "void";

        writer.WriteLine(function.ReturnCType + " CPP_SHIM_CALL " + function.Symbol
            + "(" + parameters + ")");
        writer.WriteLine("{");

        // 自定义导出：实现体由派生类给出，这里只负责包裹函数外壳
        if (function.Role is ShimRole.Extra && function.ExtraBody is not null)
        {
            foreach (string line in Normalize(function.ExtraBody).Split("\r\n"))
                writer.WriteLine(line.Length is 0 ? line : "    " + line);

            writer.WriteLine("}");
            writer.WriteLine();
            return;
        }

        switch (function.Role)
        {
            case ShimRole.New:
                writer.WriteLine("    return reinterpret_cast<" + function.Owner!.HandleName + "*>(new " + function.Owner.CppName + "(" + ArgumentList(function) + "));");
                break;

            case ShimRole.Finalize:
                writer.WriteLine("    delete reinterpret_cast<" + function.Owner!.CppName + "*>(self);");
                break;

            case ShimRole.Method:
                {
                    writer.WriteLine("    auto* target = reinterpret_cast<" + function.Owner!.CppName + "*>(self);");
                    string call = "target->" + function.MethodName + "(" + ArgumentList(function) + ")";
                    writer.WriteLine(function.ReturnsVoid ? "    " + call + ";" : "    return " + ReturnExpression(function, call) + ";");
                    break;
                }

            default:
                {
                    string call = function.FreeQualifiedName + "(" + ArgumentList(function) + ")";
                    writer.WriteLine("    return " + ReturnExpression(function, call) + ";");
                    break;
                }
        }

        writer.WriteLine("}");
        writer.WriteLine();
    }

    /// <summary>
    /// 返回位是否与 C++ 实际返回值类型不兼容：一旦不兼容就说明 shim 会产出编译不过的代码。
    /// </summary>
    /// <remarks>
    /// 覆盖三类实测问题：
    /// ① C++ 返回记录/类类型（含按**引用**返回），C 侧却被映射成 <c>void*</c> 或其它指针
    ///    （<c>QByteArray&amp; → void*</c>、<c>QAnyStringView&amp; → void*</c>、<c>const void* → void*</c>）；
    /// ② C++ 返回标量，C 侧被映射成指针（<c>char → char*</c>）；
    /// ③ C++ 返回值带 <c>const</c> 限定而 C 侧去掉了限定（<c>const char* → char*</c>）。
    /// 枚举按 <c>static_cast</c> 处理，属于已知可转换的组合，不在此列。
    /// </remarks>
    private bool IsInconsistentReturn(CXType resultType, string returnCType)
    {
        // 引用必须先剥开：Qt 大量按 QByteArray& / QAnyStringView& 返回，只判 canonical 会全部漏网
        CXType result = PeelReference(Canonical(resultType));
        if (result.kind is CXTypeKind.CXType_Enum or CXTypeKind.CXType_Void)
            return false;

        bool cIsPointer = returnCType.EndsWith('*');
        bool cIsVoidPointer = returnCType is "void*";

        switch (result.kind)
        {
            // 记录/类类型按值返回：C 侧任何指针都无法承载（值不是指针）
            case CXTypeKind.CXType_Record:
                return true;

            // 标量不可能"是指针"；映射出指针说明类型映射本身错了
            case CXTypeKind.CXType_Bool
                or CXTypeKind.CXType_Char_S
                or CXTypeKind.CXType_Char_U
                or CXTypeKind.CXType_SChar
                or CXTypeKind.CXType_UChar
                or CXTypeKind.CXType_Short
                or CXTypeKind.CXType_UShort
                or CXTypeKind.CXType_Int
                or CXTypeKind.CXType_UInt
                or CXTypeKind.CXType_Long
                or CXTypeKind.CXType_ULong
                or CXTypeKind.CXType_LongLong
                or CXTypeKind.CXType_ULongLong
                or CXTypeKind.CXType_Float
                or CXTypeKind.CXType_Double:
                return cIsPointer;

            case CXTypeKind.CXType_Pointer:
                {
                    // 指针值可以隐式转成另一个指针，但丢 const 限定或语义完全不同的 void* 承接会产生 C2440
                    if (cIsPointer is false)
                        return true;

                    CXType pointee = Canonical(result.PointeeType);
                    bool cppConst = pointee.IsConstQualified;

                    // const T* 不能喂给 T*：C 层必须保留 const 限定（Qt 的 constData()/const_iterator 大量踩中）
                    if (pointee.IsConstQualified && returnCType.StartsWith("const ", StringComparison.Ordinal) is false)
                        return true;

                    // T* 的 T 必须是能桥接成 C 标量的类型；用户自定义类型（迭代器、嵌套常量等）拼不出合法 C 指针
                    if (ScalarCTypeOrNull(pointee) is null && pointee.kind is not CXTypeKind.CXType_Void)
                        return true;

                    // const 目标类型喂给 void* 同样会丢掉限定
                    if (cppConst && cIsVoidPointer)
                        return true;

                    return false;
                }

            default:
                return false;
        }
    }

    /// <summary>
    /// 返回语句里的表达式：C 侧返回类型与 C++ 实际返回值不一致时补显式转换。
    /// </summary>
    /// <remarks>
    /// 强类型枚举（<c>enum class</c>）映射为 <c>int32_t</c>，C++ 不会隐式转换，必须 <c>static_cast</c>；
    /// 其余标量与指针本就按原类型返回，不做多余转换（尤其不能把 <c>const T*</c> 强制转成 <c>T*</c>）。
    /// </remarks>
    private static string ReturnExpression(ShimFunctionInfo function, string call)
    {
        if (function.ReturnsEnum && function.ReturnCType is "int32_t")
            return "static_cast<int32_t>(" + call + ")";

        return call;
    }

    /// <summary>
    /// 实现中的参数列表：类型必须与头文件声明一致。
    /// 构造（<c>new</c>）没有 self；析构与实例方法的首位是具体句柄类型的 self。
    /// </summary>
    protected virtual string DefinitionParameters(ShimFunctionInfo function)
    {
        List<string> parts = [];
        if (function.Owner is not null && function.Role is not ShimRole.New)
            parts.Add(function.Owner.HandleName + "* self");

        foreach (ShimParameterInfo parameter in function.Parameters)
            parts.Add(parameter.CType! + " " + parameter.Name);

        return parts.Count is 0 ? "void" : string.Join(", ", parts);
    }

    /// <summary>
    /// 调用实参：按参数类型把 C 值还原成 C++ 值。
    /// </summary>
    protected virtual string ArgumentList(ShimFunctionInfo function)
        => string.Join(", ", function.Parameters.Select(parameter => parameter.ToCppExpression));

    /// <summary>
    /// 预编译头候选文件：按序包含全部原始头文件，顺序与 <c>.cpp</c> 的 preamble 一致。
    /// </summary>
    /// <remarks>
    /// 这里保留调用方传入的路径形式（只把反斜杠换成正斜杠），以便 PCH 可以随项目一起移动；
    /// 编译时通过 include 目录解析即可。
    /// </remarks>
    private string BuildPch()
    {
        using StringWriter writer = CreateWriter();
        writer.WriteLine("// 由 OBSharp.Interop.NativeBindGenerator 生成的预编译头候选文件。");
        writer.WriteLine("#pragma once");
        writer.WriteLine();

        foreach (string header in ShimOptions.Headers)
            writer.WriteLine("#include \"" + IncludePath(header) + "\"");

        return writer.ToString();
    }

    /// <summary>
    /// PCH 与实现文件 preamble 里的头文件路径。
    /// </summary>
    /// <remarks>
    /// 默认写绝对路径，保证任何构建系统都能直接解析；开启 <see cref="CppShimGeneratorOptions.RelativeIncludes"/>
    /// 后改写为相对 native 产物目录的形式，便于 ccache 等编译缓存在不同目录、不同机器之间复用。
    /// </remarks>
    private string IncludePath(string header)
    {
        if (!ShimOptions.RelativeIncludes)
            return header.Replace('\\', '/');

        string absolute = Path.GetFullPath(header);
        string outputRoot = Path.GetFullPath(ShimOptions.Output.FullName);
        string relative = Path.GetRelativePath(outputRoot, absolute);
        return relative.Replace('\\', '/');
    }

    /// <summary>
    /// CMake 清单：每个库一个共享库目标，并声明头文件搜索路径。
    /// </summary>
    /// <remarks>
    /// 单个 PE 导出表上限是 65535 个符号，导出过多时按 <see cref="CppShimGeneratorOptions.MaxExportsPerLibrary"/>
    /// 拆成多个目标，避免链接或运行时失败。
    /// </remarks>
    protected virtual string BuildCMake(List<ShimLibrary> libraries)
    {
        using StringWriter writer = CreateWriter();
        writer.WriteLine("# 由 OBSharp.Interop.NativeBindGenerator 生成，请勿手工修改。");
        writer.WriteLine("# 每个库一个共享库目标；C# 侧 LibraryImport 使用的库名与下列目标输出名一致。");
        writer.WriteLine();

        // 显式库列表：下游无需靠 BUILDSYSTEM_TARGETS 反查就能知道清单建了哪些目标。
        // 变量名把非字母数字统一替换为下划线，避免 Qt 目标名里的点号难以在 CMake 中引用
        string libraryVariable = LibraryListVariableName();
        writer.WriteLine("set(" + libraryVariable + "");
        foreach (ShimLibrary library in libraries)
            writer.WriteLine("    \"" + library.Name + "\"");

        writer.WriteLine(")");
        writer.WriteLine();

        foreach (ShimLibrary library in libraries)
        {
            string variable = "CPP_SHIM_SOURCES_" + library.Index.ToString(CultureInfo.InvariantCulture);
            writer.WriteLine("set(" + variable + "");
            foreach (ShimSource source in library.Sources)
                writer.WriteLine("    \"${CMAKE_CURRENT_LIST_DIR}/" + SourceFileName(library, source, libraries.Count) + "\"");

            writer.WriteLine(")");
            writer.WriteLine();
            writer.WriteLine("add_library(" + library.Name + " SHARED ${" + variable + "})");
            writer.WriteLine("target_include_directories(" + library.Name + " PUBLIC \"${CMAKE_CURRENT_LIST_DIR}\")");
            writer.WriteLine("target_compile_features(" + library.Name + " PUBLIC cxx_std_17)");
            writer.WriteLine("set_target_properties(" + library.Name + " PROPERTIES OUTPUT_NAME \"" + library.Name + "\")");
            writer.WriteLine();
        }

        return writer.ToString();
    }

    // ---- 文件 ----

    private static string CreateStagingDirectory(string prefix)
    {
        string path = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteStagingDirectory(string path)
    {
        if (Directory.Exists(path))
            Directory.Delete(path, recursive: true);
    }

    private static void WriteTextFile(string path, string content)
        => File.WriteAllText(
            path,
            Normalize(content),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

    /// <summary>
    /// 创建换行符固定为 CRLF 的写入器。
    /// </summary>
    protected static StringWriter CreateWriter() => new() { NewLine = "\r\n" };

    protected static string Normalize(string value) => value.ReplaceLineEndings("\r\n");

    // ---- 数据模型 ----

    protected sealed record GeneratedFile(string Name, string Content);

    /// <summary>
    /// 一个共享库：承载不超过导出上限的符号集合。
    /// </summary>
    protected sealed class ShimLibrary(string name, int index)
    {
        public string Name { get; } = name;
        public int Index { get; } = index;
        public List<ShimSource> Sources { get; } = [];
        public List<ShimFunctionInfo> Functions { get; } = [];
    }

    /// <summary>
    /// 一个实现文件，承载该库里不超过单文件上限的符号。
    /// </summary>
    protected sealed class ShimSource(int index)
    {
        public int Index { get; } = index;
        public List<ShimFunctionInfo> Functions { get; } = [];
    }

    /// <summary>
    /// 遍历顶层声明时使用的可变收集器。
    /// </summary>
    private sealed class DeclarationCollector
    {
        public List<CXCursor> Results { get; } = [];

        public int Depth { get; set; }
    }

    /// <summary>
    /// 导出项的角色。
    /// </summary>
    protected internal enum ShimRole
    {
        New,
        Finalize,
        Method,
        Free,

        /// <summary>
        /// 框架特有的额外导出（例如 Qt 的信号连接），输出形态由派生类决定。
        /// </summary>
        Extra,
    }

    /// <summary>
    /// 一个要导出的记录类型；全部输出所需信息在翻译单元存活期间就已取出并缓存。
    /// </summary>
    protected internal sealed class ShimTypeInfo(uint key, CXCursor declaration, string cppName, string symbolPrefix, string handleName, string keyword)
    {
        public uint Key { get; } = key;
        public CXCursor Declaration { get; } = declaration;
        public string CppName { get; } = cppName;

        /// <summary>
        /// 导出符号前缀：由限定名换算得到的合法 C 标识符。
        /// </summary>
        public string SymbolPrefix { get; } = symbolPrefix;

        public string HandleName { get; } = handleName;
        public string Keyword { get; } = keyword;
        public List<ShimFunctionInfo> Functions { get; } = [];

        /// <summary>
        /// 派生类在发现阶段标记的框架特征（例如是否为 <c>QObject</c> 派生）。
        /// </summary>
        /// <remarks>
        /// 必须在翻译单元存活期间填好：输出阶段 <see cref="Declaration"/> 已失效，不能再访问 cursor。
        /// </remarks>
        public bool IsFrameworkType { get; set; }

        /// <summary>
        /// 登记时该声明是否为未实例化的类模板（输出阶段的自检依据）。
        /// </summary>
        /// <remarks>
        /// 必须在翻译单元存活期间计算并缓存；输出阶段访问 cursor 会导致崩溃。
        /// </remarks>
        public bool IsUninstantiatedTemplate { get; set; }
    }

    /// <summary>
    /// 一个导出函数；调用形态与返回类型文本在翻译单元存活期间就已取出。
    /// </summary>
    protected internal sealed class ShimFunctionInfo(
        ShimTypeInfo? owner,
        ShimRole role,
        List<ShimParameterInfo> parameters,
        string symbol,
        string returnCType,
        string? methodName,
        bool returnsVoid,
        string? freeQualifiedName)
    {
        public ShimTypeInfo? Owner { get; } = owner;
        public ShimRole Role { get; } = role;
        public List<ShimParameterInfo> Parameters { get; } = parameters;
        public string Symbol { get; } = symbol;
        public string ReturnCType { get; } = returnCType;

        /// <summary>
        /// 该导出所属的共享库名；由 <see cref="PlanLibraries"/> 在输出阶段指派。
        /// </summary>
        public string Library { get; set; } = "";

        /// <summary>
        /// 实例成员的原生方法名。
        /// </summary>
        public string? MethodName { get; } = methodName;

        /// <summary>
        /// 实例成员的返回类型是否为 <c>void</c>。
        /// </summary>
        public bool ReturnsVoid { get; } = returnsVoid;

        /// <summary>
        /// 自由函数的 C++ 限定名。
        /// </summary>
        public string? FreeQualifiedName { get; } = freeQualifiedName;

        /// <summary>
        /// <see cref="ShimRole.Extra"/> 角色的头文件声明原文（不含导出宏与调用约定）。
        /// </summary>
        public string? ExtraDeclaration { get; init; }

        /// <summary>
        /// <see cref="ShimRole.Extra"/> 角色的实现体语句块原文。
        /// </summary>
        public string? ExtraBody { get; init; }

        /// <summary>
        /// 该导出是否需要写入 shim 头文件的声明。
        /// </summary>
        public bool NeedsDeclaration { get; init; } = true;

        /// <summary>
        /// 原生返回值是否为枚举：C 侧统一映射为 <c>int32_t</c>，C++ 侧需要显式转换。
        /// </summary>
        /// <remarks>
        /// 必须在翻译单元存活期间计算并缓存；输出阶段 cursor 已失效。
        /// </remarks>
        public bool ReturnsEnum { get; init; }
    }

    /// <summary>
    /// 一个导出参数：C 形参名、C 类型文本，以及把 C 值还原为 C++ 实参的表达式。
    /// </summary>
    protected internal sealed class ShimParameterInfo(
        string name,
        CXType canonical,
        string suffix,
        string? cType,
        string cppType,
        string toCppExpression)
    {
        public string Name { get; } = name;
        public CXType Canonical { get; } = canonical;
        public string Suffix { get; } = suffix;
        public string? CType { get; } = cType;

        /// <summary>
        /// C++ 侧类型文本，用于还原强转。
        /// </summary>
        public string CppType { get; } = cppType;

        /// <summary>
        /// 把 C 形参还原成 C++ 调用实参的表达式；具体转换由生成器在构造时决定，
        /// 便于派生类（如 Qt）替换字符串、容器等特化转换。
        /// </summary>
        public string ToCppExpression { get; } = toCppExpression;
    }
}




