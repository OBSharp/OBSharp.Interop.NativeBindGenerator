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

    private static List<CXCursor> ChildrenOf(CXCursor cursor)
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
            string tuPath = Path.Combine(staging, "exports.c");
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

    private static bool ReportErrors(CXTranslationUnit tu)
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

    private bool IsInHeaders(CXCursor cursor)
    {
        clang.getCursorLocation(cursor).GetExpansionLocation(out CXFile file, out _, out _, out _);
        string name = clang.getFileName(file).ToString();
        return name.Length is not 0
            && _headers.Contains(Path.GetFullPath(name));
    }

    private void Collect(CXTranslationUnit tu)
    {
        List<CXCursor> top = ChildrenOf(clang.getTranslationUnitCursor(tu));

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

        using StringWriter source = new()
        {
            NewLine = "\n"
        };
        source.WriteLine($"    [LibraryImport(LibraryName, EntryPoint = \"{Escape(name)}\")]");
        source.WriteLine(
            $"    [UnmanagedCallConv(CallConvs = [typeof({CallConvType(clang.getFunctionTypeCallingConv(functionType))})])]");
        if (returnType.Attribute.Length is not 0)
            source.WriteLine($"    [return: {returnType.Attribute.Trim().TrimStart('[').TrimEnd(']')}]");
        source.Write($"    public static partial {returnType.Type} {Identifier(name)}({string.Join(", ", parameters)});");
        return source.ToString();
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

        using StringWriter methods = new()
        {
            NewLine = "\n"
        };
        WriteHeader(methods);
        methods.WriteLine("public static unsafe partial class NativeMethods");
        methods.WriteLine("{");
        methods.WriteLine($"    private const string LibraryName = \"{Escape(options.LibraryName)}\";");
        foreach ((string _, string source) in _functions.OrderBy(f => f.Name, StringComparer.Ordinal))
        {
            methods.WriteLine();
            methods.WriteLine(source);
        }

        methods.WriteLine("}");
        File.WriteAllText(Path.Combine(output, "NativeMethods.g.cs"), methods.ToString(), new UTF8Encoding(false));

        if (_typeSources.Count is 0)
            return;

        using StringWriter types = new()
        {
            NewLine = "\n"
        };
        WriteHeader(types);
        types.WriteLine(string.Join(
            Environment.NewLine + Environment.NewLine,
            _typeSources.OrderBy(t => t.Key, StringComparer.Ordinal).Select(t => t.Value)));
        File.WriteAllText(Path.Combine(output, "NativeTypes.g.cs"), types.ToString(), new UTF8Encoding(false));
    }

    private void WriteHeader(StringWriter builder)
    {
        builder.WriteLine("// <auto-generated>");
        builder.WriteLine("// Generated by OBSharp.Interop.NativeBindGenerator. Do not edit.");
        builder.WriteLine("// </auto-generated>");
        builder.WriteLine("#nullable enable");
        builder.WriteLine("using System.Runtime.CompilerServices;");
        builder.WriteLine("using System.Runtime.InteropServices;");
        builder.WriteLine("using System.Runtime.InteropServices.Marshalling;");
        builder.WriteLine();
        builder.WriteLine($"namespace {options.Namespace};");
        builder.WriteLine();
    }

    private static string Escape(string value)
        => value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal);
}
