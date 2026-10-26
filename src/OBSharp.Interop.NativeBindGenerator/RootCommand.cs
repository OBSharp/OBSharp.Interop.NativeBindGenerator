using DotMake.CommandLine;

namespace OBSharp.Interop.NativeBindGenerator;

/// <summary>
/// 依据 C 导出声明生成 C# 绑定；可选地先为 C++ 类生成 C 式导出。
/// </summary>
/// <remarks>
/// 用法：<c>NativeBindGenerator [生成器参数] -- [clang 参数]</c>。
/// <c>--</c> 之后的所有参数原样交给 libclang，生成器不做任何内部处理。
/// </remarks>
[CliCommand(
    Description = "依据 C 导出声明生成 C# 绑定；`--` 之后的参数原样传给 clang",
    TreatUnmatchedTokensAsErrors = false)]
public class RootCommand : ICExportGeneratorOptions
{
    [CliOption(Description = "要解析的头文件；可重复指定")]
    public string[] Header { get; set; } = [];

    [CliOption(Alias = "n", Description = "生成 C# 代码的命名空间")]
    public string Namespace { get; set; } = "OBSharp.Interop.Generated";

    [CliOption(Alias = "l", Description = "LibraryImport 使用的原生库名")]
    public string LibraryName { get; set; } = "native";

    [CliOption(Description = "C# 产物目录")]
    public string CsharpOutput { get; set; } = "generated/csharp";

    [CliOption(Description = "bool 使用 4 字节（UnmanagedType.Bool），默认 1 字节（UnmanagedType.I1）")]
    public bool FourByteBoolean { get; set; }

    [CliOption(Description = "const char* 参数使用的自定义 marshaller 类型名，默认 LPUTF8Str")]
    public string StringMarshaller { get; set; } = "";

    [CliOption(Description = "const char* 返回值使用的自定义 marshaller 类型名，默认 LPUTF8Str")]
    public string ReturnStringMarshaller { get; set; } = "";

    [CliOption(Description = "把 --header 中的 C++ 类自动转发为 C 式导出（生成 native 代码），再据此生成 C# 绑定")]
    public bool CppRedirect { get; set; }

    [CliOption(Description = "--cpp-redirect 的 native 产物目录")]
    public string NativeOutput { get; set; } = "generated/native";

    [CliOption(Description = "--cpp-redirect 生成的 native 文件基名（.h / .cpp）")]
    public string NativeName { get; set; } = "cpp_exports";

    /// <summary>
    /// <c>--</c> 之后原样传给 clang 的参数。
    /// </summary>
    public string[] ClangArguments { get; private set; } = [];

    public int Run(CliContext context)
    {
        ClangArguments = [.. context.Result.ParseResult.UnmatchedTokens];

        if (Header is { Length: 0 })
        {
            Console.Error.WriteLine("必须通过 --header 至少指定一个头文件");
            return 1;
        }

        try
        {
            if (CppRedirect)
                new CppRedirectGenerator(this).Generate();
            else
                new CExportGenerator(this).Generate();
        }
        catch (InvalidOperationException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }

        return 0;
    }
}
