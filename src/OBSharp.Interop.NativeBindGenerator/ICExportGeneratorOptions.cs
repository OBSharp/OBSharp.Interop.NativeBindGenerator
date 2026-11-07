namespace OBSharp.Interop.NativeBindGenerator;

public interface ICExportGeneratorOptions
{
    string[] Header { get; set; }
    string Namespace { get; set; }
    string LibraryName { get; set; }
    string CsharpOutput { get; set; }
    bool FourByteBoolean { get; set; }
    string StringMarshaller { get; set; }
    string ReturnStringMarshaller { get; set; }
    string NullableHandleMarshaller { get; set; }

    /// <summary>
    /// 是否允许为变参（<c>...</c>）C 声明生成绑定；仅绑定定参部分，默认跳过。
    /// </summary>
    bool AllowVariadic { get; set; }

    string[] ClangArguments { get; }
}
