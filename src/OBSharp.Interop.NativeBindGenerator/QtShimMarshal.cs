using System.Text;

namespace OBSharp.Interop.NativeBindGenerator;

/// <summary>
/// <see cref="QTShimGenerator"/> 使用的 Qt 类型转换工具（<c>qt_shim_marshal.hpp</c>）。
/// </summary>
/// <remarks>
/// 只包含 Qt 的 <c>QString</c> / <c>QList</c> 与 C 类型之间的借用式转换，不负责内存分配与释放：
/// 缓冲区由调用方提供，或按借用语义交给托管侧复制。
/// 与通用的 <see cref="CppShimMarshal"/> 分开维护，避免不需要 Qt 的项目也拖入 Qt 依赖。
/// </remarks>
internal static class QtShimMarshal
{
    /// <summary>
    /// 生成产物的文件名，也是嵌入资源的后缀。
    /// </summary>
    public const string FileName = "qt_shim_marshal.hpp";

    /// <summary>
    /// 嵌入资源的完整名称。
    /// </summary>
    /// <remarks>
    /// 项目启用了 <c>EmbeddedResource</c> 且未改写 <c>LogicalName</c> 时，
    /// MSBuild 默认把 <c>qt_shim_marshal.hpp</c> 注册为「命名空间 + 文件名」。
    /// </remarks>
    private const string ResourceName = "OBSharp.Interop.NativeBindGenerator.qt_shim_marshal.hpp";

    /// <summary>
    /// 读取头文件内容；每次调用都从程序集资源流重新读出，避免缓存失效问题。
    /// </summary>
    /// <exception cref="InvalidOperationException">程序集中缺少嵌入式头文件资源。</exception>
    public static string ReadSource()
    {
        using Stream? stream = typeof(QtShimMarshal).Assembly.GetManifestResourceStream(ResourceName);
        if (stream is null)
            throw new InvalidOperationException($"程序集中缺少嵌入资源 {ResourceName}");

        using StreamReader reader = new(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false);
        return reader.ReadToEnd();
    }

    /// <summary>
    /// 把嵌入式头文件写入指定目录。
    /// </summary>
    public static void WriteTo(string directory)
    {
        string path = Path.Combine(directory, FileName);
        File.WriteAllText(
            path,
            ReadSource().ReplaceLineEndings("\r\n"),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }
}
