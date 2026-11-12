using DotMake.CommandLine;

namespace OBSharp.Interop.NativeBindGenerator;

/// <summary>
/// 生成器入口；按子命令选择 C / C++ / Qt 三种流程。
/// </summary>
/// <remarks>
/// 用法：<c>native-bind-generator &lt;c|cpp|qt&gt; [选项] -- [clang 参数]</c>。
/// <c>--</c> 之后的所有参数原样交给 libclang，生成器不做任何内部处理。
/// 根命令本身不执行生成逻辑，只把参数分发到子命令；未指定子命令时由 CLI 框架打印帮助。
/// </remarks>
[CliCommand(
    Description = "依据 C/C++/Qt 头文件生成 C# 绑定；`--` 之后的参数原样传给 clang",
    TreatUnmatchedTokensAsErrors = false,
    Children = [typeof(CppCommand), typeof(CCommand), typeof(QtCommand)])]
public class RootCommand
{
}
