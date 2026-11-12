namespace OBSharp.Interop.NativeBindGenerator;

/// <summary>
/// 一个待生成的封装方法或属性访问器。
/// </summary>
public sealed class WrapFunction
{
    public required string ImportName { get; init; }

    public required string MemberName { get; init; }

    /// <summary>
    /// 封装方法的返回类型；返回 Span 时该类型为 <c>Span&lt;T&gt;</c> 或 <c>ReadOnlySpan&lt;T&gt;</c>。
    /// </summary>
    public string ReturnType { get; init; } = "void";

    /// <summary>
    /// 对外暴露的形参，Span 绑定后的长度参数不在此列。
    /// </summary>
    public IReadOnlyList<WrapParameter> Parameters { get; init; } = [];

    /// <summary>
    /// 调用实参（不含 self），Span 绑定已展开为指针与长度两种实参。
    /// </summary>
    public IReadOnlyList<string> Arguments { get; init; } = [];

    /// <summary>
    /// 调用前需要钉住并取地址的 Span 参数。
    /// </summary>
    public IReadOnlyList<WrapFixedPointer> FixedPointers { get; init; } = [];

    /// <summary>
    /// 是否实例成员；结构体实例需要先取得自身指针，句柄实例直接传 <c>this</c>。
    /// </summary>
    public bool IsInstance { get; init; }

    public bool IsStructInstance { get; init; }

    /// <summary>
    /// 结构体实例自身的元素类型名，用于生成 <c>fixed (T* self = &amp;this)</c>。
    /// </summary>
    public string? SelfType { get; init; }

    /// <summary>
    /// 非 null 时表示该封装返回 Span。
    /// </summary>
    public WrapSpanReturn? SpanReturn { get; init; }
}
