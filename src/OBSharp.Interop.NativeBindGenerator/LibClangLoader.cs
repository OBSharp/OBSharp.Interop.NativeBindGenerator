using System.Runtime.InteropServices;
using System.Xml.Linq;

namespace OBSharp.Interop.NativeBindGenerator;

/// <summary>
/// 预加载 libclang 与 libClangSharp 原生库。
/// </summary>
/// <remarks>
/// 构建配置未声明 RuntimeIdentifiers 时 deps.json 不含原生资产，ClangSharp 默认解析器找不到它们；
/// 这里按全路径从 NuGet 包缓存加载，之后按基名匹配即可命中。
/// 不能改用 SetDllImportResolver：ClangSharp.Interop 的静态构造也注册解析器，重复注册会抛异常。
/// </remarks>
internal static class LibClangLoader
{
    private static IntPtr s_libClang;
    private static IntPtr s_libClangSharp;

    /// <summary>
    /// 确保原生库已加载；加载失败时交回默认解析。
    /// </summary>
    public static void Ensure()
    {
        if (s_libClang != IntPtr.Zero)
            return;

        s_libClang = PreloadNative("libclang.runtime.", "libclang");
        s_libClangSharp = PreloadNative("libclangsharp.runtime.", "libClangSharp");
    }

    private static IntPtr PreloadNative(string packagePrefix, string baseName)
    {
        string suffix = OperatingSystem.IsWindows() ? ".dll"
            : OperatingSystem.IsMacOS() ? ".dylib"
            : ".so";

        foreach (string candidate in NativeCandidates(packagePrefix, baseName + suffix))
        {
            if (NativeLibrary.TryLoad(candidate, out IntPtr handle))
                return handle;
        }

        return IntPtr.Zero;
    }

    private static IEnumerable<string> NativeCandidates(string packagePrefix, string fileName)
    {
        string rid = CurrentRid();
        foreach (string root in NuGetPackageRoots())
        {
            string packageDir = Path.Combine(root, packagePrefix + rid);
            if (!Directory.Exists(packageDir))
                continue;

            foreach (string versionDir in Directory.EnumerateDirectories(packageDir)
                         .OrderByDescending(d => VersionKey(Path.GetFileName(d))))
            {
                string candidate = Path.Combine(versionDir, "runtimes", rid, "native", fileName);
                if (File.Exists(candidate))
                    yield return candidate;
            }
        }
    }

    private static HashSet<string> NuGetPackageRoots()
    {
        HashSet<string> roots = [with(StringComparer.OrdinalIgnoreCase)];
        if (Environment.GetEnvironmentVariable("NUGET_PACKAGES") is { Length: not 0 } envRoot)
            roots.Add(envRoot);

        string userConfig = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NuGet", "NuGet.Config");
        if (File.Exists(userConfig))
        {
            try
            {
                XDocument document = XDocument.Load(userConfig);
                string? configured = document.Descendants("add")
                    .FirstOrDefault(e => (string?)e.Attribute("key") is "globalPackagesFolder")
                    ?.Attribute("value")?.Value;
                if (configured is { Length: not 0 })
                    roots.Add(configured);
            }
            catch (System.Xml.XmlException)
            {
                // 配置损坏时继续尝试默认位置
            }
        }

        roots.Add(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages"));
        return roots;
    }

    private static string CurrentRid()
    {
        string os = OperatingSystem.IsWindows() ? "win"
            : OperatingSystem.IsMacOS() ? "osx"
            : "linux";
        string arch = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.Arm64 => "arm64",
            Architecture.X86 => "x86",
            _ => "x64",
        };
        return $"{os}-{arch}";
    }

    private static Version VersionKey(string name)
        => Version.TryParse(name, out Version? version) ? version : new(0, 0);
}
