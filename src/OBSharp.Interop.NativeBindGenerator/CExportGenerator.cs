using System.Runtime.InteropServices;
using System.Text;

using ClangSharp.Interop;

namespace OBSharp.Interop.NativeBindGenerator;

/// <summary>
/// 解析 C 头文件中的导出函数与相关类型，并生成 C# LibraryImport 绑定。
/// </summary>
internal sealed unsafe partial class CExportGenerator(ICExportGeneratorOptions options)
{
    internal const string QtArraySuffix = "__cppm_qt_array";
    internal const string QtCountName = "__cppm_qt_count";

    /// <summary>
    /// <c>--cpp-redirect</c> 为 qfloat16 生成的 C 桥接类型名；C# 侧映射为 <see cref="Half"/>。
    /// </summary>
    internal const string HalfBridgeType = "nbg_half";

    /// <summary>
    /// <c>--cpp-redirect</c> 为 GUID 生成的 C 桥接类型名；C# 侧映射为 <see cref="Guid"/>。
    /// </summary>
    internal const string GuidBridgeType = "nbg_guid";

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate CXChildVisitResult ChildVisitor(CXCursor cursor, CXCursor parent, void* clientData);

    private static readonly ChildVisitor CollectDelegate = CollectChild;
    private static readonly delegate* unmanaged[Cdecl]<CXCursor, CXCursor, void*, CXChildVisitResult> CollectPtr =
        (delegate* unmanaged[Cdecl]<CXCursor, CXCursor, void*, CXChildVisitResult>)
            Marshal.GetFunctionPointerForDelegate(CollectDelegate);

    [ThreadStatic]
    private static List<CXCursor>? s_children;

    private static CXChildVisitResult CollectChild(CXCursor cursor, CXCursor parent, void* clientData)
    {
        s_children!.Add(cursor);
        return CXChildVisitResult.CXChildVisit_Continue;
    }

    internal static List<CXCursor> ChildrenOf(CXCursor cursor)
    {
        List<CXCursor> children = [];
        s_children = children;
        try
        {
            _ = clang.visitChildren(cursor, CollectPtr, null);
        }
        finally
        {
            s_children = null;
            GC.KeepAlive(CollectDelegate);
        }

        return children;
    }

    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    private readonly HashSet<string> _functionSources = [];
    private readonly List<(string Name, string Source)> _functions = [];
    private HashSet<string> _headers = [with(PathComparer)];

    /// <summary>
    /// 临时翻译单元文件名；扩展名决定 clang 按 C 还是 C++ 解析。
    /// </summary>
    internal string TuFileName { get; init; } = "exports.c";

    internal bool UseQtArrays { get; init; }

    internal IReadOnlyDictionary<string, string> OriginalSignatures { get; init; }
        = new Dictionary<string, string>(StringComparer.Ordinal);

    internal IReadOnlyDictionary<string, (string ManagedType, string NativeName, string DeclarationKey, string Declaration)> OpaquePointerTypes { get; init; }
        = new Dictionary<string, (string ManagedType, string NativeName, string DeclarationKey, string Declaration)>(StringComparer.Ordinal);

    internal string FreeMemoryFunctionName { get; init; } = "cppm_free_memory";

    public void Generate()
    {
        LibClangLoader.Ensure();
        string[] headers = [.. options.Header.Select(Path.GetFullPath)];
        _headers = [with(PathComparer), .. headers];

        // 多个头文件通过临时 TU 一次解析；clang 参数完全由用户提供，这里不追加任何参数
        string staging = Path.Combine(Path.GetTempPath(), "nbg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            string tuPath = Path.Combine(staging, TuFileName);
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
                    if (ReportErrors(tu))
                        throw new InvalidOperationException("clang 解析头文件时出现错误，已中止生成");

                    Collect(tu);
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

        if (_functions.Count is 0)
            throw new InvalidOperationException("指定头文件中没有找到可绑定的 C 函数导出声明");

        Emit();

        // 成员扩展片段与类型定义分开存放，统计类型数时按去掉 #members 后的类型名去重
        int typeCount = _typeSources.Keys
            .Select(key => key.EndsWith(MembersKeySuffix, StringComparison.Ordinal)
                ? key[..^MembersKeySuffix.Length]
                : key)
            .Distinct(StringComparer.Ordinal)
            .Count();
        Console.WriteLine($"生成了 {_functions.Count} 个函数、{typeCount} 个类型的 C# 绑定。");
    }

    internal static bool ReportErrors(CXTranslationUnit tu)
    {
        bool hasError = false;
        uint count = clang.getNumDiagnostics(tu);
        for (uint i = 0; i < count; i++)
        {
            CXDiagnostic diagnostic = new((nint)clang.getDiagnostic(tu, i));
            CXDiagnosticSeverity severity = clang.getDiagnosticSeverity(diagnostic);
            if (severity is CXDiagnosticSeverity.CXDiagnostic_Error or CXDiagnosticSeverity.CXDiagnostic_Fatal)
            {
                hasError = true;
                Console.Error.WriteLine(clang.formatDiagnostic(
                    diagnostic, clang.defaultDiagnosticDisplayOptions()).ToString());
            }

            clang.disposeDiagnostic(diagnostic);
        }

        return hasError;
    }

    // 展开 extern "C" { } 块，使其中的声明与顶层声明一视同仁
    private static List<CXCursor> Flatten(List<CXCursor> cursors)
    {
        List<CXCursor> result = [];
        foreach (CXCursor cursor in cursors)
        {
            if (clang.getCursorKind(cursor) is CXCursorKind.CXCursor_LinkageSpec)
                result.AddRange(Flatten(ChildrenOf(cursor)));
            else
                result.Add(cursor);
        }

        return result;
    }

    private bool IsInHeaders(CXCursor cursor)
    {
        clang.getCursorLocation(cursor).GetExpansionLocation(out CXFile file, out _, out _, out _);
        string name = clang.getFileName(file).ToString();
        return name.Length is not 0
            && _headers.Contains(Path.GetFullPath(name));
    }

    private void Collect(CXTranslationUnit tu)
    {
        List<CXCursor> top = Flatten(ChildrenOf(clang.getTranslationUnitCursor(tu)));

        foreach (CXCursor cursor in top.Where(c => clang.getCursorKind(c) is CXCursorKind.CXCursor_TypedefDecl))
        {
            // 匿名 struct/enum 借用 typedef 名称
            CXCursor declaration = clang.getTypeDeclaration(clang.getTypedefDeclUnderlyingType(cursor));
            if (clang.Cursor_isNull(declaration) is 0
                && IsAnonymous(declaration))
            {
                _anonymousNames.TryAdd(KeyOf(declaration), clang.getCursorSpelling(cursor).ToString());
            }
        }

        List<CXCursor> functions = [.. top.Where(c =>
            clang.getCursorKind(c) is CXCursorKind.CXCursor_FunctionDecl
            && IsInHeaders(c)
            && clang.getCursorLinkage(c) is not CXLinkageKind.CXLinkage_Internal)];

        foreach (CXCursor function in functions)
            DetectReleaseFunction(function);

        // 头文件中完整定义的类型即使未被函数引用也生成
        foreach (CXCursor cursor in top.Where(IsInHeaders))
        {
            CXCursorKind kind = clang.getCursorKind(cursor);
            if (kind is not (CXCursorKind.CXCursor_StructDecl
                or CXCursorKind.CXCursor_UnionDecl
                or CXCursorKind.CXCursor_EnumDecl)
                || clang.isCursorDefinition(cursor) is 0
                || OpaquePointerTypes.ContainsKey(clang.getCursorSpelling(cursor).ToString())
                || WellKnownRecordName(cursor) is not null
                || (IsAnonymous(cursor) && !_anonymousNames.ContainsKey(KeyOf(cursor))))
            {
                continue;
            }

            try
            {
                if (kind is CXCursorKind.CXCursor_EnumDecl)
                    EnumName(cursor);
                else
                    RecordName(cursor);
            }
            catch (NotSupportedException ex)
            {
                Console.Error.WriteLine($"跳过类型 {clang.getCursorSpelling(cursor)}：{ex.Message}");
            }
        }

        foreach (CXCursor function in functions)
        {
            string name = clang.getCursorSpelling(function).ToString();
            if (name.Length is 0
                || !_functionSources.Add(name))
            {
                continue;
            }

            try
            {
                _functions.Add((name, FunctionSource(function, name)));
            }
            catch (NotSupportedException ex)
            {
                _functionSources.Remove(name);
                Console.Error.WriteLine($"跳过函数 {name}：{ex.Message}");
            }
        }

        BuildMembers();
        EmitHandles();
    }

    private string FunctionSource(CXCursor function, string name)
    {
        CXType functionType = clang.getCursorType(function);
        if (clang.isFunctionTypeVariadic(functionType) is not 0)
            throw new NotSupportedException("变参函数无法直接 P/Invoke，请在原生侧提供定参包装");

        _currentFunction = name;
        CXType nativeReturn = clang.getCursorResultType(function);
        MappedType nativeReturnType = MapType(nativeReturn, TypePosition.Return);
        MappedType returnType = nativeReturnType;

        List<string> parameters = [];
        List<ParameterModel> models = [];
        Dictionary<int, ParameterModel> modelByNativeIndex = [];
        int count = clang.Cursor_getNumArguments(function);
        Dictionary<int, QtArrayParameter> qtArrays = [];
        HashSet<int> hiddenParameters = [];
        int? qtReturnCountIndex = null;
        string? qtReturnElementType = null;

        if (UseQtArrays)
        {
            for (int i = 0; i + 1 < count; i++)
            {
                CXCursor argument = clang.Cursor_getArgument(function, (uint)i);
                string nativeName = clang.getCursorSpelling(argument).ToString();
                if (!nativeName.EndsWith(QtArraySuffix, StringComparison.Ordinal))
                    continue;

                CXCursor countArgument = clang.Cursor_getArgument(function, (uint)(i + 1));
                string countName = clang.getCursorSpelling(countArgument).ToString();
                string expectedCountName = nativeName[..^QtArraySuffix.Length] + "__cppm_qt_count";
                CXType pointer = Peel(clang.getCursorType(argument), out _);
                _ = Peel(clang.getCursorType(countArgument), out string? countWellKnown);
                if (countName != expectedCountName
                    || pointer.kind is not CXTypeKind.CXType_Pointer
                    || countWellKnown is not "nuint")
                {
                    continue;
                }

                CXType nativeElement = clang.getPointeeType(pointer);
                string elementType = clang.getCanonicalType(nativeElement).kind is CXTypeKind.CXType_Bool
                    ? "bool"
                    : MapType(nativeElement, TypePosition.Nested).Type;
                string managedName = Identifier(nativeName[..^QtArraySuffix.Length]);
                qtArrays.Add(i, new(i, i + 1, managedName, elementType));
                hiddenParameters.Add(i + 1);
            }

            CXType result = Peel(nativeReturn, out _);
            if (result.kind is CXTypeKind.CXType_Pointer)
            {
                for (int i = 0; i < count; i++)
                {
                    CXCursor argument = clang.Cursor_getArgument(function, (uint)i);
                    if (clang.getCursorSpelling(argument).ToString() != QtCountName)
                        continue;

                    CXType countPointer = Peel(clang.getCursorType(argument), out _);
                    if (countPointer.kind is not CXTypeKind.CXType_Pointer)
                        continue;

                    _ = Peel(clang.getPointeeType(countPointer), out string? countWellKnown);
                    if (countWellKnown is "nuint")
                    {
                        qtReturnCountIndex = i;
                        CXType nativeElement = clang.getPointeeType(result);
                        qtReturnElementType = clang.getCanonicalType(nativeElement).kind is CXTypeKind.CXType_Bool
                            ? "bool"
                            : MapType(nativeElement, TypePosition.Nested).Type;
                        nativeReturnType = new(qtReturnElementType + "*");
                        hiddenParameters.Add(i);
                        returnType = new($"ReadOnlySpan<{qtReturnElementType}>");
                        break;
                    }
                }
            }
        }

        for (int i = 0; i < count; i++)
        {
            CXCursor argument = clang.Cursor_getArgument(function, (uint)i);
            MappedType parameter = MapType(clang.getCursorType(argument), TypePosition.Parameter);
            string parameterName = clang.getCursorSpelling(argument).ToString();
            string csName = parameterName.Length is 0 ? $"arg{i}" : Identifier(parameterName);

            if (qtArrays.TryGetValue(i, out QtArrayParameter? qtArray))
            {
                parameters.Add($"{qtArray.ElementType}* {csName}");
                ParameterModel model = new(qtArray.ManagedName, new($"ReadOnlySpan<{qtArray.ElementType}>"));
                models.Add(model);
                modelByNativeIndex.Add(i, model);
            }
            else
            {
                string importType = parameter.ImportType.Length is 0 ? parameter.Type : parameter.ImportType;
                parameters.Add($"{parameter.Attribute}{parameter.Modifier}{importType} {csName}");
                if (!hiddenParameters.Contains(i))
                {
                    ParameterModel model = new(csName, parameter);
                    models.Add(model);
                    modelByNativeIndex.Add(i, model);
                }
            }
        }

        _currentFunction = "";
        _models.Add(new(name, returnType, models));

        string attributes = nativeReturnType.Attribute.Length is 0
            ? ""
            : $"    [return: {nativeReturnType.Attribute.Trim().TrimStart('[').TrimEnd(']')}]\n";
        string nativeMethodName = qtArrays.Count is 0
            && qtReturnCountIndex is null
            ? Identifier(name)
            : Identifier(name) + "__Native";
        string signature = NativeSignature(function, name);
        string documentation = $$"""
            /// <summary>
            /// 原生声明：<c>{{XmlEscape(signature)}}</c>
            /// </summary>
            """;
        string import = $$"""
                [LibraryImport("{{Escape(options.LibraryName)}}", EntryPoint = "{{Escape(name)}}")]
                [UnmanagedCallConv(CallConvs = [typeof({{CallConvType(clang.getFunctionTypeCallingConv(functionType))}})])]
            {{attributes}}    internal static partial {{nativeReturnType.Type}} {{nativeMethodName}}({{string.Join(", ", parameters)}});
            """;

        if (qtArrays.Count is 0
            && qtReturnCountIndex is null)
        {
            return $"{documentation}\n{import}";
        }

        return $"{import}\n\n{documentation}\n{QtArrayWrapper(
            name,
            nativeMethodName,
            nativeReturnType,
            count,
            returnType,
            models,
            modelByNativeIndex,
            hiddenParameters,
            qtArrays,
            qtReturnCountIndex,
            qtReturnElementType,
            FreeMemoryFunctionName)}";
    }

    private static string QtArrayWrapper(
        string name,
        string nativeMethodName,
        MappedType nativeReturnType,
        int nativeParameterCount,
        MappedType returnType,
        List<ParameterModel> models,
        Dictionary<int, ParameterModel> modelByNativeIndex,
        HashSet<int> hiddenParameters,
        Dictionary<int, QtArrayParameter> arrays,
        int? returnCountIndex,
        string? returnElementType,
        string freeMemoryFunctionName)
    {
        List<string> declarations = models
            .Select(parameter => $"{parameter.Type.Modifier}{parameter.Type.Type} {parameter.Name}")
            .ToList();
        List<string> arguments = [];
        List<string> fixedStatements = [];
        Dictionary<int, string> pinnedNames = [];

        foreach (QtArrayParameter array in arrays.Values)
        {
            string pinned = $"_cppmArray{array.ArrayIndex}";
            pinnedNames.Add(array.ArrayIndex, pinned);
            fixedStatements.Add($"fixed ({array.ElementType}* {pinned} = {array.ManagedName})");
        }

        for (int i = 0; i < nativeParameterCount; i++)
        {
            if (arrays.TryGetValue(i, out QtArrayParameter? array))
            {
                arguments.Add(pinnedNames[array.ArrayIndex]);
                arguments.Add($"(nuint){array.ManagedName}.Length");
                i = array.CountIndex;
                continue;
            }

            if (returnCountIndex is { } countIndex
                && i == countIndex)
            {
                arguments.Add("&_cppmCount");
                continue;
            }

            if (hiddenParameters.Contains(i))
                continue;

            if (modelByNativeIndex.TryGetValue(i, out ParameterModel? model))
                arguments.Add(model.Type.Modifier + model.Name);
        }

        string call = $"{nativeMethodName}({string.Join(", ", arguments)})";
        string body;
        if (returnCountIndex is not null)
        {
            string elementType = returnElementType!;
            body = $$"""
                    {{nativeReturnType.Type}} _cppmResult = {{call}};
                    try
                    {
                        if (_cppmCount is 0)
                            return ReadOnlySpan<{{elementType}}>.Empty;
                        if (_cppmResult is null)
                            throw new InvalidOperationException("原生 QList 返回了空指针和非零长度");

                        {{elementType}}[] copy = new ReadOnlySpan<{{elementType}}>(
                            _cppmResult,
                            checked((int)_cppmCount)).ToArray();
                        return copy;
                    }
                    finally
                    {
                        NativeMethods.{{freeMemoryFunctionName}}((nint)_cppmResult);
                    }
                """;
            body = "nuint _cppmCount;\n" + body;
        }
        else
        {
            body = returnType.Type is "void"
                ? $"{call};"
                : $"return {call};";
        }

        for (int i = fixedStatements.Count - 1; i >= 0; i--)
            body = $"{fixedStatements[i]}\n{{\n{Indent(body, 4)}\n}}";

        return $$"""
                public static {{returnType.Type}} {{Identifier(name)}}({{string.Join(", ", declarations)}})
                {
            {{Indent(body, 4)}}
                }
            """;
    }

    private string NativeSignature(CXCursor function, string name)
    {
        if (OriginalSignatures.TryGetValue(name, out string? originalSignature))
            return originalSignature;

        string returnType = clang.getTypeSpelling(clang.getCursorResultType(function)).ToString();
        int count = clang.Cursor_getNumArguments(function);
        string parameters = count is 0
            ? "void"
            : string.Join(", ", Enumerable.Range(0, count)
                .Select(index => clang.getTypeSpelling(clang.getCursorType(
                    clang.Cursor_getArgument(function, (uint)index))).ToString()));
        return $"{returnType} {name}({parameters})";
    }

    private static string XmlEscape(string value)
        => value.Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal);

    private static string Indent(string value, int spaces)
        => string.Join("\n", value.Split('\n').Select(line => new string(' ', spaces) + line));

    private static string CallConvType(CXCallingConv convention)
        => convention switch
        {
            CXCallingConv.CXCallingConv_X86StdCall => "CallConvStdcall",
            CXCallingConv.CXCallingConv_X86FastCall => "CallConvFastcall",
            CXCallingConv.CXCallingConv_X86ThisCall => "CallConvThiscall",
            _ => "CallConvCdecl",
        };

    /// <summary>
    /// 每个 <c>NativeMethods</c> 分片最多包含的全局函数数量；超出后写入下一个 partial 分片文件。
    /// </summary>
    private const int FunctionsPerFile = 64;

    /// <summary>
    /// 成员扩展片段在 <see cref="_typeSources"/> 中的键后缀，用于与类型定义合并到同一文件。
    /// </summary>
    private const string MembersKeySuffix = "#members";

    /// <summary>
    /// 类型文件名主体的最大长度，避免过长的模板类型名产生超长路径。
    /// </summary>
    private const int MaxFileBaseNameLength = 120;

    private void Emit()
    {
        string output = Path.GetFullPath(options.CsharpOutput);
        Directory.CreateDirectory(output);
        foreach (string staleFile in Directory.EnumerateFiles(output, "*.g.cs", SearchOption.AllDirectories))
            File.Delete(staleFile);

        // 函数与类型一起参与文件名去重，避免类型名与 NativeMethods 分片重名
        HashSet<string> usedNames = [with(StringComparer.OrdinalIgnoreCase)];
        EmitMethods(output, usedNames);
        EmitTypes(output, usedNames);
    }

    /// <summary>
    /// 生成调用目标：函数已归入当前类型时直接调用，否则仍走 <c>NativeMethods</c>。
    /// </summary>
    private string CallTarget(string functionName, string? ownerType)
        => ownerType is not null
            && _functionOwners.TryGetValue(functionName, out string? owner)
            && owner == ownerType
                ? Identifier(functionName)
                : "NativeMethods." + Identifier(functionName);

    private void EmitMethods(string output, HashSet<string> usedNames)
    {
        // 只有无法归入任何类型的全局函数才留在 NativeMethods
        List<(string Name, string Source)> functions = [.. _functions
            .Where(function => !_functionOwners.ContainsKey(function.Name))
            .OrderBy(function => function.Name, StringComparer.Ordinal)];
        for (int offset = 0; offset < functions.Count; offset += FunctionsPerFile)
        {
            int part = offset / FunctionsPerFile;
            string baseName = part is 0 ? "NativeMethods" : $"NativeMethods.{part + 1}";
            _ = usedNames.Add(baseName);

            string methodSources = string.Join("\n\n", functions
                .Skip(offset)
                .Take(FunctionsPerFile)
                .Select(function => function.Source));
            WriteCSharpFile(Path.Combine(output, baseName + ".g.cs"), $$"""
                {{Header()}}
                public static unsafe partial class NativeMethods
                {
                {{methodSources}}
                }

                """);
        }
    }

    private void EmitTypes(string output, HashSet<string> usedNames)
    {
        Dictionary<string, List<string>> blocks = new(StringComparer.Ordinal);

        // 类型定义与成员扩展（#members）写入同一文件
        foreach ((string key, string source) in _typeSources.OrderBy(entry => entry.Key, StringComparer.Ordinal))
        {
            string typeName = key.EndsWith(MembersKeySuffix, StringComparison.Ordinal)
                ? key[..^MembersKeySuffix.Length]
                : key;
            AddTypeBlock(blocks, typeName, source);
        }

        // 导入声明写入其所属类型；没有归属的全局函数不在其中
        Dictionary<string, List<string>> imports = new(StringComparer.Ordinal);
        foreach ((string functionName, string source) in _functions.OrderBy(function => function.Name, StringComparer.Ordinal))
        {
            if (_functionOwners.TryGetValue(functionName, out string? owner))
                AddTypeBlock(imports, owner, source);
        }

        foreach ((string owner, List<string> sources) in imports.OrderBy(entry => entry.Key, StringComparer.Ordinal))
            AddTypeBlock(blocks, owner, ImportBlock(owner, string.Join("\n\n", sources)));

        foreach ((string typeName, List<string> typeBlocks) in blocks.OrderBy(entry => entry.Key, StringComparer.Ordinal))
        {
            string baseName = FileBaseName(typeName);
            string name = baseName;
            for (int suffix = 2; !usedNames.Add(name); suffix++)
                name = $"{baseName}_{suffix}";

            WriteCSharpFile(Path.Combine(output, name + ".g.cs"), $$"""
                {{Header()}}
                {{string.Join("\n\n", typeBlocks)}}

                """);
        }
    }

    private static void AddTypeBlock(Dictionary<string, List<string>> blocks, string typeName, string source)
    {
        if (!blocks.TryGetValue(typeName, out List<string>? list))
            blocks[typeName] = list = [];
        list.Add(source);
    }

    /// <summary>
    /// 把导入声明包进所属类型的 partial 声明，让 P/Invoke 与使用它的成员位于同一类型。
    /// </summary>
    private string ImportBlock(string typeName, string source)
    {
        string declaration = _owners.TryGetValue(typeName, out (string CName, bool IsHandle) owner) && owner.IsHandle
            ? $"public sealed unsafe partial class {typeName}"
            : $"public unsafe partial struct {typeName}";
        return $$"""
            {{declaration}}
            {
            {{source}}
            }
            """;
    }

    private static string FileBaseName(string key)
    {
        StringBuilder builder = new(Math.Min(key.Length, MaxFileBaseNameLength));
        foreach (char character in key)
        {
            if (builder.Length is MaxFileBaseNameLength)
                break;

            builder.Append(char.IsLetterOrDigit(character) || character is '_' ? character : '_');
        }

        return builder.Length is 0 ? "Type" : builder.ToString();
    }

    private static void WriteCSharpFile(string path, string content)
        => File.WriteAllText(path, content.ReplaceLineEndings("\n"), new UTF8Encoding(false));

    private string Header()
        => $$"""
            // <auto-generated>
            // Generated by OBSharp.Interop.NativeBindGenerator. Do not edit.
            // </auto-generated>

            #nullable enable

            using System;
            using System.Runtime.CompilerServices;
            using System.Runtime.InteropServices;
            using System.Runtime.InteropServices.Marshalling;

            namespace {{options.Namespace}};

            """;

    private static string Escape(string value)
        => value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal);
}
