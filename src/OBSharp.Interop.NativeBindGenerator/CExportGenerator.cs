using System.Runtime.InteropServices;
using System.Text;

using ClangSharp.Interop;

namespace OBSharp.Interop.NativeBindGenerator;

/// <summary>
/// 解析 C 头文件中的导出函数与相关类型，并生成 C# LibraryImport 绑定。
/// </summary>
internal sealed unsafe partial class CExportGenerator(ICExportGeneratorOptions options)
{
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
        Console.WriteLine($"生成了 {_functions.Count} 个函数、{_typeSources.Count} 个类型的 C# 绑定。");
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
        MappedType returnType = MapType(clang.getCursorResultType(function), TypePosition.Return);

        List<string> parameters = [];
        List<ParameterModel> models = [];
        int count = clang.Cursor_getNumArguments(function);
        for (int i = 0; i < count; i++)
        {
            CXCursor argument = clang.Cursor_getArgument(function, (uint)i);
            MappedType parameter = MapType(clang.getCursorType(argument), TypePosition.Parameter);
            string parameterName = clang.getCursorSpelling(argument).ToString();
            string csName = parameterName.Length is 0 ? $"arg{i}" : Identifier(parameterName);
            models.Add(new(csName, parameter));
            parameters.Add($"{parameter.Attribute}{parameter.Modifier}{parameter.Type} {csName}");
        }

        _currentFunction = "";
        _models.Add(new(name, returnType, models));

        string attributes = returnType.Attribute.Length is 0
            ? ""
            : $"    [return: {returnType.Attribute.Trim().TrimStart('[').TrimEnd(']')}]\n";
        return $$"""
                [LibraryImport(LibraryName, EntryPoint = "{{Escape(name)}}")]
                [UnmanagedCallConv(CallConvs = [typeof({{CallConvType(clang.getFunctionTypeCallingConv(functionType))}})])]
            {{attributes}}    public static partial {{returnType.Type}} {{Identifier(name)}}({{string.Join(", ", parameters)}});
            """;
    }

    private static string CallConvType(CXCallingConv convention)
        => convention switch
        {
            CXCallingConv.CXCallingConv_X86StdCall => "CallConvStdcall",
            CXCallingConv.CXCallingConv_X86FastCall => "CallConvFastcall",
            CXCallingConv.CXCallingConv_X86ThisCall => "CallConvThiscall",
            _ => "CallConvCdecl",
        };

    private void Emit()
    {
        string output = Path.GetFullPath(options.CsharpOutput);
        Directory.CreateDirectory(output);
        foreach (string staleFile in Directory.EnumerateFiles(output, "*.g.cs", SearchOption.AllDirectories))
            File.Delete(staleFile);

        string methodSources = string.Join("\n\n", _functions
            .OrderBy(f => f.Name, StringComparer.Ordinal)
            .Select(f => f.Source));
        string methods = $$"""
            {{Header()}}
            public static unsafe partial class NativeMethods
            {
                private const string LibraryName = "{{Escape(options.LibraryName)}}";

            {{methodSources}}
            }

            """;
        File.WriteAllText(Path.Combine(output, "NativeMethods.g.cs"), methods.ReplaceLineEndings("\n"), new UTF8Encoding(false));

        if (_typeSources.Count is 0)
            return;

        string typeSources = string.Join("\n\n", _typeSources
            .OrderBy(t => t.Key, StringComparer.Ordinal)
            .Select(t => t.Value));
        string types = $$"""
            {{Header()}}
            {{typeSources}}

            """;
        File.WriteAllText(Path.Combine(output, "NativeTypes.g.cs"), types.ReplaceLineEndings("\n"), new UTF8Encoding(false));
    }

    private string Header()
        => $$"""
            // <auto-generated>
            // Generated by OBSharp.Interop.NativeBindGenerator. Do not edit.
            // </auto-generated>

            #nullable enable

            using System.Runtime.CompilerServices;
            using System.Runtime.InteropServices;
            using System.Runtime.InteropServices.Marshalling;

            namespace {{options.Namespace}};

            """;

    private static string Escape(string value)
        => value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal);
}
