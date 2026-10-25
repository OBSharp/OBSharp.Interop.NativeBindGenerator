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
    string[] ClangArguments { get; }
}
