namespace OBSharp.Interop.NativeBindGenerator;

/// <summary>
/// <see cref="CppShimGenerator"/> 的选项。
/// </summary>
public class CppShimGeneratorOptions
{
    /// <summary>
    /// 要扫描的头文件；临时翻译单元按此顺序包含它们。
    /// </summary>
    public required IReadOnlyList<string> Headers { get; init; }

    /// <summary>
    /// 原样传给 clang 的参数（<c>--</c> 之后的全部内容）。
    /// </summary>
    public IReadOnlyList<string> ClangArguments { get; init; } = [];

    /// <summary>
    /// native 产物目录：shim 头文件、实现、PCH 与 CMake 清单都写在这里。
    /// </summary>
    public required DirectoryInfo Output { get; init; }

    /// <summary>
    /// 产物基名，<c>cpp_shim</c> 会产出 <c>cpp_shim.h</c> / <c>cpp_shim.cpp</c> / <c>cpp_shim.pch.h</c>。
    /// </summary>
    public string BaseName { get; set; } = "cpp_shim";

    /// <summary>
    /// C 侧句柄类型名的前缀。
    /// </summary>
    public string ExportPrefix { get; set; } = "cpp_shim";

    /// <summary>
    /// 是否写出转换头文件 cpp_shim_marshaller.hpp。
    /// </summary>
    /// <remarks>
    /// 同一个项目里分批多次运行生成器时置为 <c>false</c>，避免重复写出同一份头文件。
    /// </remarks>
    public bool WriteMarshaller { get; set; } = true;

    /// <summary>
    /// 生成的 CMake 目标名，同时作为产物库名。
    /// </summary>
    /// <remarks>
    /// 导出数超过 <see cref="MaxExportsPerLibrary"/> 时会拆分，第一个库用该名字，
    /// 后续库依次追加 <c>.2</c>、<c>.3</c> 等序号。
    /// </remarks>
    public string TargetName { get; set; } = "cpp_shim";

    /// <summary>
    /// 单个共享库的最大导出符号数。
    /// </summary>
    /// <remarks>
    /// PE 格式的导出表用 16 位序号，上限是 65535；Qt 这类大型 C++ 项目很容易超过，
    /// 因此按该上限拆分多个库，避免链接或加载时导出表溢出。
    /// </remarks>
    public int MaxExportsPerLibrary { get; set; } = 0xFFFF;

    /// <summary>
    /// 每个实现文件包含的导出数上限，用于把单个 <c>.cpp</c> 控制在不影响编译耗时的规模。
    /// </summary>
    public int MaxExportsPerSource { get; set; } = 4096;

    /// <summary>
    /// 导出符号到所属库名的映射清单文件名。
    /// </summary>
    /// <remarks>
    /// <see cref="CBindingGenerator"/> 读取该清单，为每个函数生成指向正确库的 <c>LibraryImport</c>。
    /// </remarks>
    public string LibraryManifestFileName { get; set; } = "cpp_shim_libraries.txt";

    /// <summary>
    /// PCH 与实现文件 preamble 里的 <c>#include</c> 是否改写成相对 native 产物目录的形式。
    /// </summary>
    /// <remarks>
    /// 默认写绝对路径；开启后换机或换目录不再改变头文件文本，便于 ccache/sccache 等编译缓存复用。
    /// </remarks>
    public bool RelativeIncludes { get; set; }

    /// <summary>
    /// 是否不输出进度信息。
    /// </summary>
    /// <remarks>
    /// 进度信息是中文 UTF-8；CMake 3.20 的 <c>execute_process</c> 只能用 ANSI 捕获，
    /// 下游构建日志会因此乱码，此时可开启本开关。
    /// </remarks>
    public bool Quiet { get; set; }
}
