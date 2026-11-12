namespace OBSharp.Interop.NativeBindGenerator;

public sealed class CBindingGeneratorOptions
{
    /// <summary>
    /// 字符串的 marshaller 名称
    /// </summary>
    public string StringMarshallerName { get; set; } = "global::System.Runtime.InteropServices.Marshalling.Utf8StringMarshaller";

    /// <summary>
    /// 返回字符串的 marshaller 名称
    /// </summary>
    /// <remarks>
    /// LibraryImport 会调用该 marshaller 的 <c>Free</c> 释放返回缓冲区；若原生函数返回静态或借用字符串，
    /// 必须换成只复制、不释放的 marshaller。
    /// </remarks>
    public string ReturnStringMarshallerName { get; set; } = "global::System.Runtime.InteropServices.Marshalling.Utf8StringMarshaller";

    /// <summary>
    /// <c>bool</c> 参数与返回值是否按 4 字节封送。
    /// </summary>
    /// <remarks>
    /// 默认按 1 字节（<c>UnmanagedType.I1</c>）封送，与常见的 C <c>bool</c>/<c>_Bool</c> 一致；
    /// 目标库声明为 <c>BOOL</c>/<c>int</c> 时需要改用 4 字节（<c>UnmanagedType.Bool</c>）。
    /// </remarks>
    public bool FourByteBoolean { get; set; }

    /// <summary>
    /// <see cref="System.Runtime.InteropServices.SafeHandle"/> 的 marshaller 名称
    /// </summary>
    public string SafeHandleMarshallerName { get; set; } = "global::System.Runtime.InteropServices.Marshalling.SafeHandleMarshaller<T>";

    /// <summary>
    /// NativeTypeNameAttribute 的名称 <br/>
    /// 默认为 null，表示不使用 NativeTypeNameAttribute <br/>
    /// 此特性应具有 <see cref="AttributeTargets.Parameter"/> 和 <see cref="AttributeTargets.ReturnValue"/> 的目标
    /// </summary>
    /// <remarks>
    /// 当存在此值时，生成 [Attribute("NativeTypeName")] <br/>
    /// 否则生成 /* NativeTypeName */
    /// </remarks>
    public string? NativeTypeNameAttributeName { get; set; }

    /// <summary>
    /// 类型映射，键为 C 类型，值为对应的 .Net 类型
    /// </summary>
    public Dictionary<string, string> TypeMap { get; } = new()
    {
        // 显式有符号整数类型
        ["signed char"] = "global::System.SByte",
        ["signed short"] = "global::System.Int16",
        ["signed int"] = "global::System.Int32",
        ["signed long"] = "global::System.Runtime.InteropServices.CLong",
        ["signed long long"] = "global::System.Int64",
        // 显式无符号整数类型
        ["unsigned char"] = "global::System.Byte",
        ["unsigned short"] = "global::System.UInt16",
        ["unsigned int"] = "global::System.UInt32",
        ["unsigned long"] = "global::System.Runtime.InteropServices.CULong",
        ["unsigned long long"] = "global::System.UInt64",
        // 整数类型
        ["short"] = "global::System.Int16",
        ["int"] = "global::System.Int32",
        ["long"] = "global::System.Runtime.InteropServices.CLong",
        ["long long"] = "global::System.Int64",

        // 字符类型
        ["char"] = "global::System.Byte", // C 标准 char 类型，通常为有符号 但在 C# 里通常对应为无符号 byte 类型
        // wchar_t 的宽度与目标平台有关，由 MapType 按实际大小处理
        ["char8_t"] = "global::System.Byte",
        ["char16_t"] = "global::System.Char",
        ["char32_t"] = "global::System.UInt32",

        // 浮点类型
        ["_Float16"] = "global::System.Half",
        ["float"] = "global::System.Single",
        ["double"] = "global::System.Double",

        // 布尔类型由 MapType 依据 CXType_Bool 处理，以便附加 MarshalAs(UnmanagedType.I1)

        // 指针类型
        ["void*"] = "global::System.IntPtr",

        // 平台相关整数类型
        ["size_t"] = "global::System.UIntPtr",
        ["intptr_t"] = "global::System.IntPtr",
        ["uintptr_t"] = "global::System.UIntPtr",
        ["ptrdiff_t"] = "global::System.IntPtr",
        ["ssize_t"] = "global::System.IntPtr",
        ["intmax_t"] = "global::System.Int64",
        ["uintmax_t"] = "global::System.UInt64",
    };

    /// <summary>
    /// LibraryImport 使用的原生库名
    /// </summary>
    /// <remarks>
    /// 指定 <see cref="LibraryManifest"/> 后，每个函数改用清单中记录的库名；
    /// 清单里没有的符号仍回退到该值。
    /// </remarks>
    public string LibraryName { get; set; } = "native";

    /// <summary>
    /// 导出符号到共享库名的映射清单（由 <see cref="CppShimGenerator"/> 生成）。
    /// </summary>
    /// <remarks>
    /// 单个 PE 导出表上限为 65535 个符号，shim 会按该上限拆出多个库；
    /// 生成 C# 绑定时必须让每个 <c>LibraryImport</c> 指向真正包含它的库。
    /// </remarks>
    public FileInfo? LibraryManifest { get; set; }

    /// <summary>
    /// 输出 C# 类型定义的目录（阶段 1）
    /// </summary>
    public required DirectoryInfo TypeDefineOutput { get; set; }

    /// <summary>
    /// 输出本机方法绑定的目录（阶段 2）
    /// </summary>
    public required DirectoryInfo NativeMethodOutput { get; set; }

    /// <summary>
    /// 输出托管封装代码的目录（阶段 3）
    /// </summary>
    public required DirectoryInfo ManagedMethodOutput { get; set; }

    /// <summary>
    /// 输出常量代码的目录（阶段 4，文件名为 <c>类型名.g.c.{序号}.cs</c>）
    /// </summary>
    public required DirectoryInfo ConstantOutput { get; set; }

    /// <summary>
    /// C# 命名空间名称
    /// </summary>
    public string? NameSpace { get; set; }

    /// <summary>
    /// 阶段 2 中 Native 导入方法的访问修饰符，默认 <c>private</c>，使阶段 3 的托管封装可在同一 partial 类型内调用。
    /// </summary>
    public string NativeMethodAccessibility { get; set; } = "private";

    /// <summary>
    /// 宿主类型匹配不上的函数与封装代码归入的类名，默认 <c>NativeMethods</c>。
    /// </summary>
    public string DefaultNativeMethodsClassName { get; set; } = "NativeMethods";

    /// <summary>
    /// C# 关键字列表，用于避免生成的类型或成员名称与关键字冲突。
    /// </summary>
    public IReadOnlyList<string> Keywords { get; } =
    [
        "abstract", "as", "base", "bool", "break", "byte", "case", "catch", "char", "checked", "class", "const",
        "continue", "decimal", "default", "delegate", "do", "double", "else", "enum", "event", "explicit", "extern",
        "false", "finally", "fixed", "float", "for", "foreach", "goto", "if", "implicit", "in", "int", "interface",
        "internal", "is", "lock", "long", "namespace", "new", "null", "object", "operator", "out", "override",
        "params", "private", "protected", "public", "readonly", "ref", "return", "sbyte", "sealed", "short",
        "sizeof", "stackalloc", "static", "string", "struct", "switch", "this", "throw", "true", "try", "typeof",
        "uint", "ulong", "unchecked", "unsafe", "ushort", "using", "virtual", "void", "volatile", "while",
    ];

    /// <summary>
    /// 阶段 2、阶段 3 每个生成文件最多包含的方法或属性数量，避免单个文件过大。
    /// </summary>
    public int MethodsPerFile { get; set; } = 128;
}
