using System.Text;

namespace OBSharp.Interop.NativeBindGenerator;

/// <summary>
/// <see cref="CppShimGenerator"/> 使用的 native 侧转换工具（<c>cpp_shim_marshal.hpp</c>）。
/// </summary>
/// <remarks>
/// 头文件内容以 EmbeddedResource 形式随程序集发布，需要时才从资源流读取并写出，
/// 不在源码里重复维护一份原始字符串。
/// </remarks>
internal static class CppShimMarshal
{
    /// <summary>
    /// 生成产物的文件名，也是嵌入资源的后缀。
    /// </summary>
    public const string FileName = "cpp_shim_marshal.hpp";

    /// <summary>
    /// 嵌入资源的完整名称。
    /// </summary>
    /// <remarks>
    /// 项目启用了 <c>EmbeddedResource</c> 且未改写 <c>LogicalName</c> 时，
    /// MSBuild 默认把 <c>cpp_shim_marshal.hpp</c> 注册为「命名空间 + 文件名」。
    /// </remarks>
    private const string ResourceName = "OBSharp.Interop.NativeBindGenerator.cpp_shim_marshal.hpp";

    /// <summary>
    /// 读取头文件内容；每次调用都从程序集资源流重新读出，避免缓存失效问题。
    /// </summary>
    /// <exception cref="InvalidOperationException">程序集中缺少嵌入式头文件资源。</exception>
    public static string ReadSource()
    {
        using Stream? stream = typeof(CppShimMarshal).Assembly.GetManifestResourceStream(ResourceName);
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
