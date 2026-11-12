using System.Text;

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

/// <summary>
/// 宿主类型形态，决定封装 partial 声明的关键字。
/// </summary>
public enum WrapHostKind
{
    /// <summary>
    /// 有完整定义的记录类型，封装为 <c>partial struct</c>。
    /// </summary>
    Struct,

    /// <summary>
    /// 只有前向声明的记录类型，封装为 <c>SafeHandle</c> 派生的 <c>partial class</c>。
    /// </summary>
    Handle,

    /// <summary>
    /// 无法归类的默认宿主，封装为静态 <c>partial class</c>。
    /// </summary>
    NativeMethods,
}

/// <summary>
/// 封装方法对外暴露的形参。
/// </summary>
public sealed record WrapParameter(string Name, string Type);

/// <summary>
/// <c>fixed</c> 局部指针：把托管 Span 的存储钉住后交给原生调用。
/// </summary>
public sealed record WrapFixedPointer(string ElementType, string PointerName, string SpanName);

/// <summary>
/// 返回 Span 时的局部变量与类型信息。
/// </summary>
/// <param name="ElementType">元素类型</param>
/// <param name="ReadOnly">是否使用 <c>ReadOnlySpan</c></param>
/// <param name="CountLocal">接收原生长度的局部变量名</param>
/// <param name="CountType">长度局部变量的托管类型</param>
/// <param name="PointerLocal">接收原生指针的局部变量名</param>
public sealed record WrapSpanReturn(
    string ElementType,
    bool ReadOnly,
    string CountLocal,
    string CountType,
    string PointerLocal);

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

/// <summary>
/// 把阶段 2 的 <c>LibraryImport</c> 声明包装成公共 API，并按需把指针 + 长度参数对绑定为 Span 视图。
/// </summary>
public sealed class CSharpWrapGenerator(CSharpWrapGeneratorOptions options)
{
    /// <summary>
    /// 生成一个封装方法。
    /// </summary>
    public static string EmitMethod(WrapFunction function)
    {
        string signature = MethodSignature(function);
        string call = BuildCall(function);

        if (!NeedsBlock(function))
            return $"{signature} => {call};";

        using StringWriter writer = CreateWriter();
        writer.WriteLine(signature);
        writer.WriteLine("{");
        WriteBlockBody(writer, function, call);
        writer.Write("}");
        return writer.ToString();
    }

    /// <summary>
    /// 生成一个托管属性；getter/setter 各自决定能否写成表达式主体。
    /// </summary>
    public static string EmitProperty(WrapProperty property)
    {
        using StringWriter writer = CreateWriter();
        writer.Write(property.IsStatic ? "public static " : "public ");
        writer.Write(property.Type);
        writer.Write(' ');
        writer.WriteLine(property.Name);
        writer.WriteLine("{");

        if (property.Getter is { } getter)
        {
            WriteAccessor(writer, "get", getter, null);
            if (property.Setter is not null)
                writer.WriteLine();
        }

        if (property.Setter is { } setter)
            WriteAccessor(writer, "set", setter, "value");

        writer.Write("}");
        return writer.ToString();
    }

    /// <summary>
    /// 把宿主的全部封装成员按 <see cref="CSharpWrapGeneratorOptions.MethodsPerFile"/> 拆分写盘。
    /// </summary>
    public void WriteHostFiles(string hostName, WrapHostKind kind, IReadOnlyList<string> members)
    {
        if (members.Count is 0)
            return;

        string header = Header();
        int part = 0;
        for (int offset = 0; offset < members.Count; offset += options.MethodsPerFile)
        {
            part++;
            using StringWriter file = CreateWriter();
            file.Write(header);
            file.WriteLine(PartialDeclaration(hostName, kind));
            file.WriteLine("{");

            for (int i = offset; i < Math.Min(offset + options.MethodsPerFile, members.Count); i++)
            {
                if (i > offset)
                    file.WriteLine();

                WriteIndented(file, members[i], 4);
            }

            file.WriteLine();
            file.Write("}");
            WriteFile($"{hostName}.g.{part}.cs", file.ToString());
        }
    }

    /// <summary>
    /// 清空输出目录中本阶段之前生成的文件，保留其他来源的文件。
    /// </summary>
    public void CleanOutput()
    {
        Directory.CreateDirectory(options.Output.FullName);
        foreach (FileInfo file in options.Output.EnumerateFiles("*.cs", SearchOption.TopDirectoryOnly))
        {
            if (file.Name.Contains(".g.", StringComparison.Ordinal))
                file.Delete();
        }
    }

    // ---- 方法体 ----

    /// <summary>
    /// 需要块主体的情形：结构体实例要取自身指针、有 Span 形参要钉住、返回 Span 要声明局部变量。
    /// </summary>
    private static bool NeedsBlock(WrapFunction function)
        => function.IsStructInstance
            || function.SpanReturn is not null
            || function.FixedPointers.Count is not 0;

    private static void WriteBlockBody(StringWriter writer, WrapFunction function, string call)
    {
        if (function.SpanReturn is { } spanReturn)
        {
            if (function.IsStructInstance)
                writer.WriteLine($"    {function.SelfType}* self = ({function.SelfType}*)global::System.Runtime.CompilerServices.Unsafe.AsPointer(ref this);");

            writer.WriteLine($"    {spanReturn.CountType} {spanReturn.CountLocal};");
            writer.WriteLine($"    {spanReturn.ElementType}* {spanReturn.PointerLocal} = {call};");
            writer.WriteLine($"    return {SpanConstruction(spanReturn)};");
            return;
        }

        int indent = 4;
        if (function.IsStructInstance)
        {
            writer.WriteLine($"    fixed ({function.SelfType}* self = &this)");
            indent = 8;
        }

        foreach (WrapFixedPointer fixedPointer in function.FixedPointers)
        {
            writer.WriteLine($"{new string(' ', indent)}fixed ({fixedPointer.ElementType}* {fixedPointer.PointerName} = {fixedPointer.SpanName})");
            indent += 4;
        }

        writer.Write(new string(' ', indent));
        writer.WriteLine(function.ReturnType is "void" ? $"{call};" : $"return {call};");
    }

    /// <summary>
    /// 生成 Span 构造表达式；<c>count</c> 是原生长度局部变量，超出 <see cref="int"/> 范围时按平台语义截断。
    /// </summary>
    private static string SpanConstruction(WrapSpanReturn spanReturn)
    {
        string spanType = SpanTypeName(spanReturn.ElementType, spanReturn.ReadOnly);
        return $"{spanReturn.PointerLocal} is null ? default : new {spanType}({spanReturn.PointerLocal}, checked((int){spanReturn.CountLocal}))";
    }

    private static void WriteAccessor(StringWriter writer, string keyword, WrapFunction accessor, string? valueArgument)
    {
        string call = BuildCall(accessor, valueArgument);

        if (!NeedsBlock(accessor))
        {
            writer.WriteLine($"    {keyword} => {call};");
            return;
        }

        writer.WriteLine($"    {keyword}");
        writer.WriteLine("    {");

        if (accessor.SpanReturn is { } spanReturn)
        {
            if (accessor.IsStructInstance)
                writer.WriteLine($"        {accessor.SelfType}* self = ({accessor.SelfType}*)global::System.Runtime.CompilerServices.Unsafe.AsPointer(ref this);");

            writer.WriteLine($"        {spanReturn.CountType} {spanReturn.CountLocal};");
            writer.WriteLine($"        {spanReturn.ElementType}* {spanReturn.PointerLocal} = {call};");
            writer.WriteLine($"        return {SpanConstruction(spanReturn)};");
            writer.WriteLine("    }");
            return;
        }

        int indent = 8;
        if (accessor.IsStructInstance)
        {
            writer.WriteLine($"        fixed ({accessor.SelfType}* self = &this)");
            indent = 12;
        }

        foreach (WrapFixedPointer fixedPointer in accessor.FixedPointers)
        {
            writer.WriteLine($"{new string(' ', indent)}fixed ({fixedPointer.ElementType}* {fixedPointer.PointerName} = {fixedPointer.SpanName})");
            indent += 4;
        }

        writer.Write(new string(' ', indent));
        writer.WriteLine(accessor.ReturnType is "void" ? $"{call};" : $"return {call};");
        writer.WriteLine("    }");
    }

    // ---- 签名与调用 ----

    private static string MethodSignature(WrapFunction function)
    {
        using StringWriter writer = CreateWriter();
        writer.Write(function.IsInstance ? "public " : "public static ");
        writer.Write(function.ReturnType);
        writer.Write(' ');
        writer.Write(function.MemberName);
        writer.Write('(');

        List<string> parameters = [];
        foreach (WrapParameter parameter in function.Parameters)
            parameters.Add($"{parameter.Type} {parameter.Name}");

        writer.Write(string.Join(", ", parameters));
        writer.Write(')');
        return writer.ToString();
    }

    /// <summary>
    /// 生成调用表达式：实例成员补 <c>this</c>/<c>self</c>。
    /// setter 的原生实参是唯一一个值参数，因此用 <c>value</c> 替换它而不是追加。
    /// </summary>
    private static string BuildCall(WrapFunction function, string? valueArgument = null)
    {
        List<string> parameters = valueArgument is null
            ? [.. function.Arguments]
            : [valueArgument];

        List<string> arguments = [];
        if (function.IsInstance)
            arguments.Add(function.IsStructInstance ? "self" : "this");

        arguments.AddRange(parameters);
        return $"{function.ImportName}({string.Join(", ", arguments)})";
    }

    private static string SpanTypeName(string elementType, bool readOnly)
        => readOnly
            ? $"global::System.ReadOnlySpan<{elementType}>"
            : $"global::System.Span<{elementType}>";

    // ---- 输出 ----

    private string Header()
    {
        using StringWriter writer = CreateWriter();
        writer.WriteLine("// <auto-generated>");
        writer.WriteLine("//   This code was generated by OBSharp.Interop.NativeBindGenerator.");
        writer.WriteLine("//   DO NOT MODIFY IT MANUALLY.");
        writer.WriteLine("// </auto-generated>");
        writer.WriteLine();
        writer.WriteLine("#nullable enable");
        writer.WriteLine();
        writer.WriteLine("using System;");
        writer.WriteLine("using System.Runtime.CompilerServices;");
        writer.WriteLine("using System.Runtime.InteropServices;");
        writer.WriteLine("using System.Runtime.InteropServices.Marshalling;");
        writer.WriteLine();
        if (!string.IsNullOrEmpty(options.NameSpace))
        {
            writer.Write("namespace ");
            writer.Write(options.NameSpace);
            writer.WriteLine(";");
            writer.WriteLine();
        }

        return writer.ToString();
    }

    private static string PartialDeclaration(string hostName, WrapHostKind kind)
        => kind switch
        {
            WrapHostKind.Struct => $"public unsafe partial struct {hostName}",
            WrapHostKind.NativeMethods => $"public static unsafe partial class {hostName}",
            _ => $"public unsafe partial class {hostName}",
        };

    private static void WriteIndented(StringWriter writer, string source, int spaces)
    {
        string padding = new(' ', spaces);
        foreach (string line in Normalize(source).ReplaceLineEndings("\n").Split('\n'))
            writer.WriteLine(line.Length is 0 ? line : padding + line);
    }

    /// <summary>
    /// 用文件流写入生成结果，避免一次性把大文件读入内存后整体落盘。
    /// </summary>
    private void WriteFile(string fileName, string content)
    {
        using FileStream stream = new(
            Path.Combine(options.Output.FullName, fileName),
            FileMode.Create,
            FileAccess.Write,
            FileShare.Read);

        byte[] bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(Normalize(content));
        stream.Write(bytes);
        stream.Flush();
    }

    /// <summary>
    /// 创建换行符固定为 CRLF 的写入器，保证生成结果不随平台变化。
    /// </summary>
    private static StringWriter CreateWriter() => new() { NewLine = "\r\n" };

    private static string Normalize(string value) => value.ReplaceLineEndings("\r\n");
}
