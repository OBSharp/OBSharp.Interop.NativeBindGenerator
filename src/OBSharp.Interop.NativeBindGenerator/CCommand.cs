using DotMake.CommandLine;

using ClangSharp.Interop;

namespace OBSharp.Interop.NativeBindGenerator;

/// <summary>
/// 纯 C 流程：直接解析 C 头文件并生成 C# 绑定。
/// </summary>
/// <remarks>
/// 适用于 <c>extern "C"</c> 导出或 <c>__declspec(dllexport)</c> 标注的 C 接口：
/// 不做 native 侧转发，<c>LibraryImport</c> 直接指向被绑定的库。
/// </remarks>
[CliCommand(
    Name = "c",
    Description = "直接从 C 头文件生成 C# 绑定（无 native shim）",
    TreatUnmatchedTokensAsErrors = false,
    ShortFormAutoGenerate = CliNameAutoGenerate.None)]
public class CCommand : GenerationCommand
{
    /// <inheritdoc />
    protected override void RunPipeline()
    {
        CXTranslationUnit unit = ParseHeaders(".c");
        try
        {
            CBindingGeneratorOptions bindingOptions = BuildBindingOptions(libraryManifest: null);
            new CBindingGenerator(bindingOptions).Invoke(unit);
        }
        finally
        {
            unit.Dispose();
        }
    }
}
