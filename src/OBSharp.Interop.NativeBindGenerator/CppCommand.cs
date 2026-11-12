using DotMake.CommandLine;

using ClangSharp.Interop;

namespace OBSharp.Interop.NativeBindGenerator;

/// <summary>
/// C++ 流程：先为 C++ 类生成 extern "C" 导出层，再据此生成 C# 绑定。
/// </summary>
/// <remarks>
/// 完整链路为 <see cref="CppShimGenerator"/> 产出 shim 头文件与实现 →
/// 重新解析 shim 头文件 → <see cref="CBindingGenerator"/> 生成绑定 → <see cref="CSharpWrapGenerator"/> 生成封装。
/// </remarks>
[CliCommand(
    Name = "cpp",
    Description = "为 C++ 类生成 C 导出层，再据此生成 C# 绑定",
    TreatUnmatchedTokensAsErrors = false,
    ShortFormAutoGenerate = CliNameAutoGenerate.None)]
public class CppCommand : GenerationCommand
{
    /// <inheritdoc />
    protected override void RunPipeline()
    {
        CppShimGeneratorOptions shimOptions = BuildShimOptions();
        new CppShimGenerator(shimOptions).Generate();

        FileInfo manifest = new(Path.Combine(shimOptions.Output.FullName, shimOptions.LibraryManifestFileName));

        CXTranslationUnit unit = new CppShimGenerator(shimOptions).ParseShim();
        try
        {
            CBindingGeneratorOptions bindingOptions = BuildBindingOptions(
                manifest.Exists ? manifest : null);
            new CBindingGenerator(bindingOptions).Invoke(unit);
        }
        finally
        {
            unit.Dispose();
        }
    }
}
