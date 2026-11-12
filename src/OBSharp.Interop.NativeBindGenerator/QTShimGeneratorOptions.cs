namespace OBSharp.Interop.NativeBindGenerator;

/// <summary>
/// <see cref="QTShimGenerator"/> 的选项。
/// </summary>
public sealed class QTShimGeneratorOptions : CppShimGeneratorOptions
{
    /// <summary>
    /// 信号连接句柄的 C 侧类型名。
    /// </summary>
    public string ConnectionHandleTypeName { get; set; } = "qt_shim_connection";

    /// <summary>
    /// 断开信号连接并释放连接句柄的函数名。
    /// </summary>
    public string DisconnectFunctionName { get; set; } = "qt_shim_disconnect";

    /// <summary>
    /// <c>QObject</c> 派生类型是否改用 <c>deleteLater</c> 释放。
    /// </summary>
    /// <remarks>
    /// <c>QObject</c> 可能仍被事件循环或其它线程引用，直接 <c>delete</c> 会崩溃；
    /// 改为投递到所属线程的事件循环回收。关闭后回退为 <c>delete</c>。
    /// </remarks>
    public bool UseDeleteLater { get; set; } = true;
}
