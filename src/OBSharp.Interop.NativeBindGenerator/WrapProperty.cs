namespace OBSharp.Interop.NativeBindGenerator;

/// <summary>
/// 一个待生成的托管属性。
/// </summary>
public sealed class WrapProperty
{
    public required string Name { get; init; }

    /// <summary>
    /// 额外的 XML 注释行（已含 <c>///</c> 前缀）；为空时只输出原生名说明。
    /// </summary>
    public IReadOnlyList<string> CommentLines { get; init; } = [];

    public required string Type { get; init; }

    public bool IsStatic { get; init; }

    public WrapFunction? Getter { get; init; }

    public WrapFunction? Setter { get; init; }
}
