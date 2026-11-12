using DotMake.CommandLine;

using ClangSharp.Interop;

namespace OBSharp.Interop.NativeBindGenerator;

/// <summary>
/// Qt 流程：在 C++ 导出层的基础上追加 Qt 特化，再生成 C# 绑定。
/// </summary>
/// <remarks>
/// 与 <see cref="CppCommand"/> 的区别全部由 <see cref="QTShimGenerator"/> 承担：
/// 信号转为连接导出、<c>QString</c> 走 UTF-8 借用、<c>QObject</c> 用 <c>deleteLater</c> 释放、过滤元对象成员。
/// </remarks>
[CliCommand(
    Name = "qt",
    Description = "为 Qt 类生成 C 导出层与 C# 绑定（信号、QString、QObject 生命周期）",
    TreatUnmatchedTokensAsErrors = false,
    ShortFormAutoGenerate = CliNameAutoGenerate.None)]
public class QtCommand : GenerationCommand
{
    [CliOption(Description = "信号连接句柄的 C 侧类型名")]
    public string ConnectionTypeName { get; set; } = "qt_shim_connection";

    [CliOption(Description = "断开信号连接并释放句柄的函数名")]
    public string DisconnectFunctionName { get; set; } = "qt_shim_disconnect";

    [CliOption(Description = "QObject 派生类型是否用 deleteLater 释放；关闭后回退为 delete")]
    public bool UseDeleteLater { get; set; } = true;

    /// <inheritdoc />
    protected override void RunPipeline()
    {
        // Qt 选项继承通用 shim 选项，字段为 init-only，需在一次对象初始值设定项中写全
        QTShimGeneratorOptions qtOptions = new()
        {
            Headers = Header,
            ClangArguments = ClangArguments,
            Output = new DirectoryInfo(NativeOutput),
            BaseName = NativeName,
            ExportPrefix = ExportPrefix,
            WriteMarshaller = WriteMarshaller,
            TargetName = NativeName,
            MaxExportsPerLibrary = MaxExportsPerLibrary,
            MaxExportsPerSource = MaxExportsPerSource,
            ConnectionHandleTypeName = ConnectionTypeName,
            DisconnectFunctionName = DisconnectFunctionName,
            UseDeleteLater = UseDeleteLater,
        };

        QTShimGenerator generator = new(qtOptions);
        generator.Generate();

        FileInfo manifest = new(Path.Combine(qtOptions.Output.FullName, qtOptions.LibraryManifestFileName));

        CXTranslationUnit unit = new QTShimGenerator(qtOptions).ParseShim();
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
