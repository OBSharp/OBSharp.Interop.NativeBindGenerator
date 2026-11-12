namespace OBSharp.Interop.NativeBindGenerator;

/// <summary>
/// 宿主类型形态，决定封装 partial 声明的关键字。
/// </summary>
public enum WrapHostKind
{
    /// <summary>
    /// 有完整定义的记录类型，封装为 <c>partial struct</c>。
    /// </summary>
    Struct,

    /// <summary>
    /// 只有前向声明的记录类型，封装为 <c>SafeHandle</c> 派生的 <c>partial class</c>。
    /// </summary>
    Handle,

    /// <summary>
    /// 无法归类的默认宿主，封装为静态 <c>partial class</c>。
    /// </summary>
    NativeMethods,
}
