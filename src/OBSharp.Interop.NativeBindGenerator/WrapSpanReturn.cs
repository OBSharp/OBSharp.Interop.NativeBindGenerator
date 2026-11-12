namespace OBSharp.Interop.NativeBindGenerator;

/// <summary>
/// 返回 Span 时的局部变量与类型信息。
/// </summary>
/// <param name="ElementType">元素类型</param>
/// <param name="ReadOnly">是否使用 <c>ReadOnlySpan</c></param>
/// <param name="CountLocal">接收原生长度的局部变量名</param>
/// <param name="CountType">长度局部变量的托管类型</param>
/// <param name="PointerLocal">接收原生指针的局部变量名</param>
public sealed record WrapSpanReturn(
    string ElementType,
    bool ReadOnly,
    string CountLocal,
    string CountType,
    string PointerLocal);
