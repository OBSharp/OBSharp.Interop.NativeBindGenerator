namespace OBSharp.Interop.NativeBindGenerator;

/// <summary>
/// 托管封装代码生成（阶段 3）的选项。
/// </summary>
public sealed class CSharpWrapGeneratorOptions
{
    /// <summary>
    /// 输出托管封装的目录
    /// </summary>
    public required DirectoryInfo Output { get; set; }

    /// <summary>
    /// C# 命名空间名称
    /// </summary>
    public string? NameSpace { get; set; }

    /// <summary>
    /// 每个文件最多包含的方法或属性数量
    /// </summary>
    public int MethodsPerFile { get; set; } = 128;
}
