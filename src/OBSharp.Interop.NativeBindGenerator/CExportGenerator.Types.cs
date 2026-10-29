using System.Globalization;
using System.Text.RegularExpressions;

using ClangSharp.Interop;

namespace OBSharp.Interop.NativeBindGenerator;

/// <summary>
/// C 类型到 C# 类型的映射，以及结构体、联合体、枚举与不透明句柄的生成。
/// </summary>
internal sealed unsafe partial class CExportGenerator
{
    private enum TypePosition
    {
        Parameter,
        Return,
        Field,

        /// <summary>
        /// 指针目标或函数指针签名内部，只能使用 blittable 类型。
        /// </summary>
        Nested,
    }

    private sealed record MappedType(string Type, string Attribute = "", string Modifier = "");

    private sealed record FieldInfo(
        string Name,
        string TypeText,
        string RawType,
        long OffsetBytes,
        long Size,
        long Align);

    private sealed record QtArrayParameter(
        int ArrayIndex,
        int CountIndex,
        string ManagedName,
        string ElementType);

    private static readonly HashSet<string> Keywords =
    [
        "abstract", "as", "base", "bool", "break", "byte", "case", "catch", "char", "checked", "class", "const",
        "continue", "decimal", "default", "delegate", "do", "double", "else", "enum", "event", "explicit", "extern",
        "false", "finally", "fixed", "float", "for", "foreach", "goto", "if", "implicit", "in", "int", "interface",
        "internal", "is", "lock", "long", "namespace", "new", "null", "object", "operator", "out", "override",
        "params", "private", "protected", "public", "readonly", "ref", "return", "sbyte", "sealed", "short",
        "sizeof", "stackalloc", "static", "string", "struct", "switch", "this", "throw", "true", "try", "typeof",
        "uint", "ulong", "unchecked", "unsafe", "ushort", "using", "virtual", "void", "volatile", "while",
    ];

    [GeneratedRegex(@"^(u?)int(8|16|32|64)_t$", RegexOptions.CultureInvariant)]
    private static partial Regex FixedWidthPattern();

    [GeneratedRegex("destroy|free|release|close|delete|dispose", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ReleaseNamePattern();

    private readonly Dictionary<uint, string> _anonymousNames = [];
    private readonly Dictionary<uint, string> _recordNames = [];
    private readonly Dictionary<uint, string> _enumNames = [];
    private readonly Dictionary<uint, string> _handleNames = [];
    private readonly Dictionary<uint, string> _handleNativeTypes = [];
    private readonly Dictionary<uint, string> _handleReleases = [];
    private readonly Dictionary<string, uint> _releaseFunctions = [];
    private readonly Dictionary<string, string> _typeSources = [];
    private string _currentFunction = "";
    private int _anonymousCounter;

    private static uint KeyOf(CXCursor declaration)
        => clang.hashCursor(clang.getCanonicalCursor(declaration));

    private static bool IsAnonymous(CXCursor declaration)
    {
        string spelling = clang.getCursorSpelling(declaration).ToString();
        return spelling.Length is 0
            || spelling.Contains("(unnamed", StringComparison.Ordinal)
            || spelling.Contains("(anonymous", StringComparison.Ordinal);
    }

    private string DeclarationName(CXCursor declaration)
    {
        if (!IsAnonymous(declaration))
            return clang.getCursorSpelling(declaration).ToString();

        return _anonymousNames.TryGetValue(KeyOf(declaration), out string? name)
            ? name
            : $"Anonymous{++_anonymousCounter}";
    }

    private static string Identifier(string name)
        => Keywords.Contains(name) ? "@" + name : name;

    private static string? WellKnownTypedef(string name)
        => name switch
        {
            "size_t" or "uintptr_t" => "nuint",
            "ptrdiff_t" or "ssize_t" or "intptr_t" => "nint",
            "wchar_t" => "char",
            _ => FixedWidthPattern().Match(name) is { Success: true } match
                ? FixedWidthName(match.Groups[1].Length is not 0, match.Groups[2].Value)
                : null,
        };

    private static string FixedWidthName(bool isUnsigned, string bits)
        => (isUnsigned, bits) switch
        {
            (false, "8") => "sbyte",
            (true, "8") => "byte",
            (false, "16") => "short",
            (true, "16") => "ushort",
            (false, "32") => "int",
            (true, "32") => "uint",
            (false, _) => "long",
            (true, _) => "ulong",
        };

    /// <summary>
    /// 剥去 typedef / elaborated 等语法糖；沿途遇到 size_t、intN_t 等已知 typedef 时提前返回其 C# 类型。
    /// </summary>
    private static CXType Peel(CXType type, out string? wellKnown)
    {
        wellKnown = null;
        CXType current = type;
        while (true)
        {
            switch (current.kind)
            {
                case CXTypeKind.CXType_Elaborated:
                    current = clang.Type_getNamedType(current);
                    continue;
                case CXTypeKind.CXType_Attributed:
                    current = clang.Type_getModifiedType(current);
                    continue;
                case CXTypeKind.CXType_Typedef:
                    wellKnown = WellKnownTypedef(clang.getTypedefName(current).ToString());
                    if (wellKnown is not null)
                        return current;

                    current = clang.getTypedefDeclUnderlyingType(clang.getTypeDeclaration(current));
                    continue;
                case CXTypeKind.CXType_Unexposed:
                case (CXTypeKind)177: // CXType_Paren
                    return clang.getCanonicalType(current);
                default:
                    return current;
            }
        }
    }

    private static string? ScalarName(CXTypeKind kind)
        => kind switch
        {
            CXTypeKind.CXType_Char_S or CXTypeKind.CXType_Char_U or CXTypeKind.CXType_UChar => "byte",
            CXTypeKind.CXType_SChar => "sbyte",
            CXTypeKind.CXType_WChar or CXTypeKind.CXType_Char16 => "char",
            CXTypeKind.CXType_Short => "short",
            CXTypeKind.CXType_UShort => "ushort",
            CXTypeKind.CXType_Int => "int",
            CXTypeKind.CXType_UInt => "uint",
            CXTypeKind.CXType_Long => "CLong",
            CXTypeKind.CXType_ULong => "CULong",
            CXTypeKind.CXType_LongLong => "long",
            CXTypeKind.CXType_ULongLong => "ulong",
            CXTypeKind.CXType_Float => "float",
            CXTypeKind.CXType_Double => "double",
            _ => null,
        };

    private static bool IsPlainChar(CXType type)
        => type.kind is CXTypeKind.CXType_Char_S or CXTypeKind.CXType_Char_U;

    private MappedType MapType(CXType type, TypePosition position)
    {
        CXType shape = Peel(type, out string? wellKnown);
        if (wellKnown is not null)
            return new(wellKnown);

        return shape.kind switch
        {
            CXTypeKind.CXType_Void => new("void"),
            CXTypeKind.CXType_Bool => position is TypePosition.Parameter or TypePosition.Return
                ? new("bool", $"[MarshalAs(UnmanagedType.{(options.FourByteBoolean ? "Bool" : "I1")})] ")
                : new("byte"),
            CXTypeKind.CXType_Enum => new(EnumName(clang.getTypeDeclaration(shape))),
            CXTypeKind.CXType_Record => new(RecordName(clang.getTypeDeclaration(shape))),
            CXTypeKind.CXType_Pointer => MapPointer(shape, position),
            CXTypeKind.CXType_ConstantArray or CXTypeKind.CXType_IncompleteArray or CXTypeKind.CXType_VariableArray 
                => new(MapType(clang.getArrayElementType(shape), TypePosition.Nested).Type + "*"),
            CXTypeKind.CXType_FunctionProto or CXTypeKind.CXType_FunctionNoProto => new(FunctionPointerType(shape)),
            _ => ScalarName(shape.kind) is { } scalar
                ? new(scalar)
                : throw new NotSupportedException($"类型 {clang.getTypeSpelling(type)} 暂不支持"),
        };
    }

    private MappedType MapPointer(CXType pointer, TypePosition position)
    {
        CXType pointee = clang.getPointeeType(pointer);
        CXType peeled = Peel(pointee, out string? pointeeWellKnown);
        bool isConst = clang.isConstQualifiedType(pointee) is not 0;
        bool isMarshalled = position is TypePosition.Parameter or TypePosition.Return;

        if (peeled.kind is CXTypeKind.CXType_FunctionProto or CXTypeKind.CXType_FunctionNoProto)
            return new(FunctionPointerType(peeled));

        if (isMarshalled
            && isConst
            && pointeeWellKnown is "char")
        {
            return new("string?", "[MarshalAs(UnmanagedType.LPWStr)] ");
        }

        if (position is TypePosition.Field)
            return new("nint");

        if (pointeeWellKnown is null)
        {
            if (isMarshalled
                && isConst
                && IsPlainChar(peeled))
            {
                return new("string?", StringAttribute(position));
            }

            if (position is TypePosition.Parameter
                && !isConst
                && peeled.kind is CXTypeKind.CXType_Pointer
                && Peel(clang.getPointeeType(peeled), out string? innerWellKnown) is { } inner
                && innerWellKnown is null
                && IsPlainChar(inner))
            {
                return new("string?", StringAttribute(position), "out ");
            }

            if (peeled.kind is CXTypeKind.CXType_Void)
                return new("nint");

            if (peeled.kind is CXTypeKind.CXType_Record)
            {
                CXCursor declaration = clang.getTypeDeclaration(peeled);
                string nativeTypeName = clang.getCursorSpelling(declaration).ToString();
                if (OpaquePointerTypes.TryGetValue(nativeTypeName, out var opaqueType))
                {
                    _typeSources.TryAdd(opaqueType.ManagedName, $$"""
                        /// <summary>
                        /// 原生不透明指针类型：<c>{{XmlEscape(opaqueType.NativeName)}}</c>。
                        /// </summary>
                        public record struct {{opaqueType.ManagedName}}();
                        """);
                    return new(opaqueType.ManagedName + "*");
                }

                if (!isMarshalled)
                    return new("nint");

                uint key = KeyOf(declaration);
                if (_releaseFunctions.TryGetValue(_currentFunction, out uint releaseKey)
                    && releaseKey == key)
                {
                    return new("nint");
                }

                return new(HandleName(declaration));
            }
        }

        return new(MapType(pointee, TypePosition.Nested).Type + "*");
    }

    private string StringAttribute(TypePosition position)
    {
        string marshaller = position is TypePosition.Return
            ? options.ReturnStringMarshaller
            : options.StringMarshaller;
        return marshaller.Length is 0
            ? "[MarshalAs(UnmanagedType.LPUTF8Str)] "
            : $"[MarshalUsing(typeof({marshaller}))] ";
    }

    private string FunctionPointerType(CXType function)
    {
        if (clang.isFunctionTypeVariadic(function) is not 0)
            throw new NotSupportedException("变参函数指针无法映射");

        string convention = clang.getFunctionTypeCallingConv(function) switch
        {
            CXCallingConv.CXCallingConv_X86StdCall => "Stdcall",
            CXCallingConv.CXCallingConv_X86FastCall => "Fastcall",
            CXCallingConv.CXCallingConv_X86ThisCall => "Thiscall",
            _ => "Cdecl",
        };

        List<string> parts = [];
        int count = Math.Max(clang.getNumArgTypes(function), 0);
        for (int i = 0; i < count; i++)
            parts.Add(MapType(clang.getArgType(function, (uint)i), TypePosition.Nested).Type);
        parts.Add(MapType(clang.getResultType(function), TypePosition.Nested).Type);
        return $"delegate* unmanaged[{convention}]<{string.Join(", ", parts)}>";
    }

    // ---- 不透明句柄 ----

    private string HandleName(CXCursor declaration)
    {
        uint key = KeyOf(declaration);
        if (!_handleNames.TryGetValue(key, out string? name))
        {
            string cName = DeclarationName(declaration);
            name = Pascal(cName) + (_recordNames.ContainsKey(key) ? "Handle" : "");
            while (_recordNames.Values.Contains(name, StringComparer.Ordinal)
                || _handleNames.Values.Contains(name, StringComparer.Ordinal))
            {
                name += "Handle";
            }

            _handleNames[key] = name;
            _handleNativeTypes[key] = clang.getTypeSpelling(clang.getCursorType(declaration)).ToString();
            _owners[name] = (cName, true);
        }

        return name;
    }

    /// <summary>
    /// 识别「无返回值、仅接收一个不透明指针」且名称含 destroy/free 等字样的释放函数。
    /// </summary>
    private void DetectReleaseFunction(CXCursor function)
    {
        string name = clang.getCursorSpelling(function).ToString();
        if (!ReleaseNamePattern().IsMatch(name)
            || clang.getCursorResultType(function).kind is not CXTypeKind.CXType_Void
            || clang.Cursor_getNumArguments(function) is not 1)
        {
            return;
        }

        CXType parameter = Peel(clang.getCursorType(clang.Cursor_getArgument(function, 0)), out _);
        if (parameter.kind is not CXTypeKind.CXType_Pointer)
            return;

        CXType pointee = Peel(clang.getPointeeType(parameter), out string? wellKnown);
        if (wellKnown is not null
            || pointee.kind is not CXTypeKind.CXType_Record)
        {
            return;
        }

        CXCursor declaration = clang.getTypeDeclaration(pointee);
        uint key = KeyOf(declaration);
        if (_handleReleases.TryAdd(key, name))
            _releaseFunctions[name] = key;
    }

    private void EmitHandles()
    {
        foreach ((uint key, string name) in _handleNames)
        {
            bool hasRelease = _handleReleases.TryGetValue(key, out string? release);
            string releaseCall = hasRelease
                ? $"NativeMethods.{Identifier(release!)}(handle);"
                : "// 头文件中没有识别到释放函数，句柄不拥有原生对象";
            string rawType = _handleNativeTypes[key];
            _typeSources[name] = $$"""
                /// <summary>
                /// 原生类型：<c>{{XmlEscape(rawType)}}</c>。
                /// </summary>
                public sealed partial class {{name}} : SafeHandle
                {
                    public {{name}}() : base(IntPtr.Zero, {{(hasRelease ? "true" : "false")}})
                    {
                    }

                    public override bool IsInvalid => handle == IntPtr.Zero;

                    protected override bool ReleaseHandle()
                    {
                        {{releaseCall}}
                        return true;
                    }
                }
                """;
        }
    }

    // ---- 枚举 ----

    private string EnumName(CXCursor declaration)
    {
        uint key = KeyOf(declaration);
        if (_enumNames.TryGetValue(key, out string? existing))
            return existing;

        CXCursor definition = clang.getCursorDefinition(declaration);
        if (clang.Cursor_isNull(definition) is not 0)
            throw new NotSupportedException($"枚举 {clang.getCursorSpelling(declaration)} 没有完整定义");

        string name = DeclarationName(declaration);
        _enumNames[key] = name;

        string underlying = MapType(clang.getEnumDeclIntegerType(definition), TypePosition.Field).Type;
        bool isSigned = underlying is "sbyte" or "short" or "int" or "long";

        string members = string.Join("\n", ChildrenOf(definition)
            .Where(c => clang.getCursorKind(c) is CXCursorKind.CXCursor_EnumConstantDecl)
            .Select(constant =>
            {
                string value = isSigned
                    ? clang.getEnumConstantDeclValue(constant).ToString(CultureInfo.InvariantCulture)
                    : clang.getEnumConstantDeclUnsignedValue(constant).ToString(CultureInfo.InvariantCulture);
                return $"    {Identifier(clang.getCursorSpelling(constant).ToString())} = {value},";
            }));

        string rawType = clang.getTypeSpelling(clang.getCursorType(definition)).ToString();
        _typeSources[name] = $$"""
            /// <summary>
            /// 原生类型：<c>{{XmlEscape(rawType)}}</c>。
            /// </summary>
            public enum {{name}} : {{underlying}}
            {
            {{members}}
            }
            """;
        return name;
    }

    // ---- 结构体与联合体 ----

    private string RecordName(CXCursor declaration)
    {
        uint key = KeyOf(declaration);
        if (_recordNames.TryGetValue(key, out string? existing))
            return existing;

        CXCursor definition = clang.getCursorDefinition(declaration);
        if (clang.Cursor_isNull(definition) is not 0)
            throw new NotSupportedException($"类型 {clang.getCursorSpelling(declaration)} 只有前向声明，不能按值使用");

        string name = DeclarationName(declaration);
        _recordNames[key] = name;
        _owners[name + "*"] = (name, false);
        try
        {
            _typeSources[name] = RecordSource(definition, name);
        }
        catch
        {
            _recordNames.Remove(key);
            throw;
        }

        return name;
    }

    private string RecordSource(CXCursor definition, string name)
    {
        bool isUnion = clang.getCursorKind(definition) is CXCursorKind.CXCursor_UnionDecl;
        long structSize = clang.Type_getSizeOf(clang.getCursorType(definition));
        bool hasBitFields = false;
        List<FieldInfo> fields = [];
        int index = 0;

        foreach (CXCursor field in ChildrenOf(definition)
            .Where(c => clang.getCursorKind(c) is CXCursorKind.CXCursor_FieldDecl))
        {
            index++;
            if (clang.Cursor_isBitField(field) is not 0)
            {
                hasBitFields = true;
                continue;
            }

            string spelling = clang.getCursorSpelling(field).ToString();
            string fieldName = spelling.Length is 0 ? $"Anonymous{index}" : Identifier(spelling);
            if (fieldName == name)
                fieldName += "_";

            CXType fieldType = clang.getCursorType(field);
            if (Peel(fieldType, out _) is { kind: CXTypeKind.CXType_IncompleteArray })
                continue;

            string typeText = FieldType(fieldType, name, fieldName);
            fields.Add(new(
                fieldName,
                typeText,
                clang.getTypeSpelling(fieldType).ToString(),
                clang.Cursor_getOffsetOfField(field) / 8,
                clang.Type_getSizeOf(fieldType),
                clang.Type_getAlignOf(fieldType)));
        }

        int? pack = isUnion || hasBitFields ? null : FindPack(fields, structSize);
        bool isExplicit = pack is null;
        string layout = isExplicit
            ? $"[StructLayout(LayoutKind.Explicit, Size = {structSize})]"
            : pack is 0
                ? "[StructLayout(LayoutKind.Sequential)]"
                : $"[StructLayout(LayoutKind.Sequential, Pack = {pack})]";

        List<string> lines = [];
        if (hasBitFields)
            lines.Add("    // 位域无法直接映射，整体按存储单元处理");
        if (hasBitFields
            && fields.Count is 0
            && BitStorageType(structSize) is { } storage)
        {
            lines.Add("    [FieldOffset(0)]");
            lines.Add($"    public {storage} Value;");
        }

        foreach (FieldInfo field in fields)
        {
            lines.Add("    /// <summary>");
            lines.Add($"    /// 原生字段类型：<c>{XmlEscape(field.RawType)}</c>。");
            lines.Add("    /// </summary>");
            if (isExplicit)
                lines.Add($"    [FieldOffset({field.OffsetBytes})]");
            lines.Add($"    public {field.TypeText} {field.Name};");
        }

        string body = string.Join("\n", lines);
        string rawType = clang.getTypeSpelling(clang.getCursorType(definition)).ToString();
        return $$"""
            /// <summary>
            /// 原生类型：<c>{{XmlEscape(rawType)}}</c>。
            /// </summary>
            {{layout}}
            public unsafe partial struct {{name}}
            {
            {{body}}
            }
            """;
    }

    private static string? BitStorageType(long size)
        => size switch
        {
            1 => "byte",
            2 => "ushort",
            4 => "uint",
            8 => "ulong",
            _ => null,
        };

    private string FieldType(CXType type, string owner, string fieldName)
    {
        CXType shape = Peel(type, out string? wellKnown);
        if (wellKnown is null
            && shape.kind is CXTypeKind.CXType_ConstantArray)
        {
            string element = MapType(clang.getArrayElementType(shape), TypePosition.Field).Type;
            long count = clang.getNumElements(shape);
            if (count is <= 0 or > int.MaxValue)
                throw new NotSupportedException($"定长数组 {fieldName} 的长度 {count} 无法映射到 InlineArray");

            string bufferName = $"{owner}_{fieldName.TrimStart('@')}_Buffer";
            _typeSources[bufferName] = $$"""
                [InlineArray({{count.ToString(CultureInfo.InvariantCulture)}})]
                public struct {{bufferName}}
                {
                    private {{element}} _element0;
                }
                """;
            return bufferName;
        }

        return MapType(type, TypePosition.Field).Type;
    }

    /// <summary>
    /// 寻找能复现 clang 字段偏移与总大小的 Sequential 打包值；0 表示默认对齐，找不到返回 null。
    /// </summary>
    private static int? FindPack(List<FieldInfo> fields, long structSize)
    {
        foreach (int pack in (int[])[0, 1, 2, 4, 8])
        {
            long offset = 0;
            long maxAlign = 1;
            bool matches = true;
            foreach (FieldInfo field in fields)
            {
                long align = pack is 0 ? field.Align : Math.Min(field.Align, pack);
                offset = AlignUp(offset, align);
                if (offset != field.OffsetBytes)
                {
                    matches = false;
                    break;
                }

                offset += field.Size;
                maxAlign = Math.Max(maxAlign, align);
            }

            if (matches
                && AlignUp(offset, maxAlign) == structSize)
            {
                return pack;
            }
        }

        return null;
    }

    private static long AlignUp(long value, long align)
        => align <= 1 ? value : (value + align - 1) / align * align;
}
