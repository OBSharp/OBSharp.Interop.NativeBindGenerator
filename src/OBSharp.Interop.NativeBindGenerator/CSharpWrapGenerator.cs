using System.Text;

namespace OBSharp.Interop.NativeBindGenerator;

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
