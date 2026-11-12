namespace OBSharp.Interop.NativeBindGenerator;

/// <summary>
/// <c>fixed</c> 局部指针：把托管 Span 的存储钉住后交给原生调用。
/// </summary>
public sealed record WrapFixedPointer(string ElementType, string PointerName, string SpanName);
