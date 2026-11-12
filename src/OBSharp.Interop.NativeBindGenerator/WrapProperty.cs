namespace OBSharp.Interop.NativeBindGenerator;

/// <summary>
/// 一个待生成的托管属性。
/// </summary>
public sealed class WrapProperty
{
    public required string Name { get; init; }

    public required string Type { get; init; }

    public bool IsStatic { get; init; }

    public WrapFunction? Getter { get; init; }

    public WrapFunction? Setter { get; init; }
}
