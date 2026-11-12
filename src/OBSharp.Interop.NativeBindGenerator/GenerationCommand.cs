using DotMake.CommandLine;

using ClangSharp.Interop;

namespace OBSharp.Interop.NativeBindGenerator;

/// <summary>
/// 三个生成命令共享的参数与流程骨架。
/// </summary>
/// <remarks>
/// 用法：<c>native-bind-generator &lt;c|cpp|qt&gt; [选项] -- [clang 参数]</c>。
/// <c>--</c> 之后的所有参数原样交给 libclang，生成器不做任何内部处理。
/// 子命令以 <see cref="RunPipeline"/> 为唯一扩展点：共享部分负责参数校验与产物目录，
/// 子命令只需决定「用哪个收集器解析」与「是否需要经过 shim 中转」。
/// </remarks>
public abstract class GenerationCommand
{
    [CliOption(Description = "要解析的头文件；可重复指定")]
    public string[] Header { get; set; } = [];

    [CliOption(Description = "递归搜索头文件的目录；可重复指定。目录下的 .h/.hpp 会自动追加到 --header")]
    public string[] HeaderDirectory { get; set; } = [];

    [CliOption(Description = "递归搜索时按相对路径片段排除的目录或文件；可重复指定，例如 util/windows")]
    public string[] HeaderExclude { get; set; } = [];

    [CliOption(Alias = "n", Description = "生成 C# 代码的命名空间")]
    public string Namespace { get; set; } = "OBSharp.Interop.Generated";

    [CliOption(Alias = "l", Description = "LibraryImport 使用的原生库名")]
    public string LibraryName { get; set; } = "native";

    [CliOption(Description = "C# 产物目录")]
    public string CsharpOutput { get; set; } = "generated/csharp";

    [CliOption(Description = "native 产物目录：shim 头文件、实现、PCH 与 CMake 清单都写在这里")]
    public string NativeOutput { get; set; } = "generated/native";

    [CliOption(Description = "native 产物基名；产出 <基名>.h / <基名>.cpp / <基名>.pch.h")]
    public string NativeName { get; set; } = "cpp_shim";

    [CliOption(Description = "C 侧句柄与符号的前缀")]
    public string ExportPrefix { get; set; } = "cpp_shim";

    [CliOption(Description = "每个共享库的最大导出符号数；PE 导出表上限为 65535")]
    public int MaxExportsPerLibrary { get; set; } = 0xFFFF;

    [CliOption(Description = "每个实现文件的最大导出数")]
    public int MaxExportsPerSource { get; set; } = 4096;

    [CliOption(Description = "是否写出转换头文件；同一项目分批生成时置为 false 以避免重复")]
    public bool WriteMarshaller { get; set; } = true;

    [CliOption(Description = "每个托管方法文件最多包含的方法或属性数量")]
    public int MethodsPerFile { get; set; } = 128;

    [CliOption(Description = "bool 使用 4 字节（UnmanagedType.Bool），默认 1 字节（UnmanagedType.I1）")]
    public bool FourByteBoolean { get; set; }

    [CliOption(Description = "const char* 参数使用的自定义 marshaller 类型名，默认 LPUTF8Str")]
    public string StringMarshaller { get; set; } = "";

    [CliOption(Description = "const char* 返回值使用的自定义 marshaller 类型名；借用返回的字符串应使用只复制、不释放的 marshaller")]
    public string ReturnStringMarshaller { get; set; } = "";

    [CliOption(Description = "可空句柄参数（Xxx? ↔ 原生 NULL）使用的自定义 marshaller 类型名")]
    public string NullableHandleMarshaller { get; set; } = "";

    /// <summary>
    /// <c>--</c> 之后原样传给 clang 的参数。
    /// </summary>
    public string[] ClangArguments { get; private set; } = [];

    /// <summary>
    /// 执行命令。
    /// </summary>
    public int Run(CliContext context)
    {
        ClangArguments = [.. context.Result.ParseResult.UnmatchedTokens];

        try
        {
            ResolveHeaders();
        }
        catch (InvalidOperationException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }

        if (Header is { Length: 0 })
        {
            Console.Error.WriteLine("必须通过 --header 或 --header-directory 至少指定一个头文件");
            return 1;
        }

        try
        {
            RunPipeline();
        }
        catch (InvalidOperationException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }

        return 0;
    }

    /// <summary>
    /// 各子命令的实现入口：解析头文件并写出产物。
    /// </summary>
    protected abstract void RunPipeline();

    /// <summary>
    /// 用于构建 shim 的通用选项，直接映射共享参数。
    /// </summary>
    /// <summary>
    /// 构建通用 shim 选项，直接映射共享参数。
    /// </summary>
    protected CppShimGeneratorOptions BuildShimOptions()
        => new()
        {
            Headers = Header,
            ClangArguments = ClangArguments,
            Output = new DirectoryInfo(NativeOutput),
            BaseName = NativeName,
            ExportPrefix = ExportPrefix,
            WriteMarshaller = WriteMarshaller,
            TargetName = NativeName,
            MaxExportsPerLibrary = MaxExportsPerLibrary,
            MaxExportsPerSource = MaxExportsPerSource,
        };

    /// <summary>
    /// 用于 C# 绑定的通用选项；<paramref name="libraryManifest"/> 为 shim 生成的符号归属清单。
    /// </summary>
    /// <remarks>
    /// 命令行给出的 marshaller 为空时保持 <see cref="CBindingGeneratorOptions"/> 的默认值，
    /// 因为默认值本身就是完整的类型名而非可选的开关。
    /// </remarks>
    protected CBindingGeneratorOptions BuildBindingOptions(FileInfo? libraryManifest)
    {
        CBindingGeneratorOptions bindingOptions = new()
        {
            TypeDefineOutput = new DirectoryInfo(CsharpOutput),
            NativeMethodOutput = new DirectoryInfo(CsharpOutput),
            ManagedMethodOutput = new DirectoryInfo(CsharpOutput),
            ConstantOutput = new DirectoryInfo(CsharpOutput),
            NameSpace = Namespace,
            LibraryName = LibraryName,
            MethodsPerFile = MethodsPerFile,
            FourByteBoolean = FourByteBoolean,
            LibraryManifest = libraryManifest,
        };

        if (StringMarshaller.Length is not 0)
            bindingOptions.StringMarshallerName = StringMarshaller;

        if (ReturnStringMarshaller.Length is not 0)
            bindingOptions.ReturnStringMarshallerName = ReturnStringMarshaller;

        if (NullableHandleMarshaller.Length is not 0)
            bindingOptions.SafeHandleMarshallerName = NullableHandleMarshaller;

        return bindingOptions;
    }

    /// <summary>
    /// 直接把头文件解析为一个翻译单元，供纯 C 流程使用。
    /// </summary>
    /// <remarks>
    /// 头文件按顺序通过临时翻译单元包含；调用方负责释放返回的翻译单元。
    /// </remarks>
    protected CXTranslationUnit ParseHeaders(string extension)
    {
        LibClangLoader.Ensure();

        // 常量只应从用户头文件收集，避免把标准库与系统头里的常量当成导出候选
        ClangCollector.SourceFiles = [.. Header.Select(Path.GetFullPath)];

        string staging = Path.Combine(Path.GetTempPath(), "nbg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            string tuPath = Path.Combine(staging, "input" + extension);
            using (StringWriter writer = new() { NewLine = "\r\n" })
            {
                foreach (string header in Header)
                {
                    writer.Write("#include \"");
                    writer.Write(Path.GetFullPath(header).Replace('\\', '/'));
                    writer.WriteLine("\"");
                }

                File.WriteAllText(tuPath, writer.ToString(), new System.Text.UTF8Encoding(false));
            }

            CXIndex index = CXIndex.Create(false, false);
            try
            {
                CXTranslationUnit unit = CXTranslationUnit.Parse(
                    index,
                    tuPath,
                    [.. ClangArguments],
                    default,
                    CXTranslationUnit_Flags.CXTranslationUnit_SkipFunctionBodies);

                if (unit.Handle == IntPtr.Zero)
                    throw new InvalidOperationException("libclang 无法创建翻译单元，请检查 -- 之后的 clang 参数");

                return unit;
            }
            finally
            {
                index.Dispose();
            }
        }
        finally
        {
            Directory.Delete(staging, recursive: true);
        }
    }

    /// <summary>
    /// 递归枚举 <see cref="HeaderDirectory"/> 下的头文件，并排除 <see cref="HeaderExclude"/> 命中的相对路径。
    /// </summary>
    private void ResolveHeaders()
    {
        if (HeaderDirectory.Length is 0)
            return;

        StringComparer comparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        HashSet<string> seen = [with(comparer)];
        foreach (string header in Header)
            seen.Add(Path.GetFullPath(header));

        List<string> discovered = [];
        foreach (string directory in HeaderDirectory)
        {
            string root = Path.GetFullPath(directory);
            if (!Directory.Exists(root))
                throw new InvalidOperationException($"头文件目录不存在：{root}");

            IEnumerable<string> files = Directory
                .EnumerateFiles(root, "*", SearchOption.AllDirectories)
                .OrderBy(file => file, comparer);

            foreach (string file in files)
            {
                string extension = Path.GetExtension(file);
                bool supported = extension.Equals(".h", StringComparison.OrdinalIgnoreCase)
                    || extension.Equals(".hpp", StringComparison.OrdinalIgnoreCase);
                if (!supported)
                    continue;

                string relative = Path.GetRelativePath(root, file).Replace('\\', '/');
                if (HeaderExclude.Any(pattern => MatchesExclude(relative, pattern)))
                    continue;

                if (seen.Add(file))
                    discovered.Add(file);
            }
        }

        Header = [.. Header, .. discovered];
    }

    private static bool MatchesExclude(string relativePath, string pattern)
    {
        string normalized = pattern.Replace('\\', '/').Trim('/');
        return normalized.Length is not 0
            && relativePath.Contains(normalized, StringComparison.OrdinalIgnoreCase);
    }
}
