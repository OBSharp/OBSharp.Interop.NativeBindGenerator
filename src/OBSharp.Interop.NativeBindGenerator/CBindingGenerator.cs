using System.Globalization;
using System.Text;

using ClangSharp.Interop;

using Humanizer;

namespace OBSharp.Interop.NativeBindGenerator;

/// <summary>
/// 依据 C 头文件中的导出声明生成 C# 绑定，并按「类型定义 / Native 导入 / 托管封装」三个阶段分文件输出。
/// </summary>
public sealed class CBindingGenerator(CBindingGeneratorOptions options) : ClangCollector
{
    private readonly Dictionary<string, string> _typeNameMap = new(options.TypeMap, StringComparer.Ordinal);
    private readonly Dictionary<uint, TypeState> _typeStates = [];
    private readonly Dictionary<uint, HostKind> _hostKinds = [];
    private readonly Dictionary<uint, string> _nativeSpellings = [];
    private readonly Dictionary<string, string> _typeSources = [with(StringComparer.Ordinal)];
    private readonly Dictionary<uint, string> _createFunctions = [];
    private readonly Dictionary<uint, int> _createFunctionArity = [];
    private readonly Dictionary<uint, string> _releaseFunctions = [];
    private readonly Dictionary<uint, HostInfo> _handleHosts = [];
    private readonly List<HostInfo> _hosts = [];
    private readonly Dictionary<(HostInfo Host, string Candidate, bool IsInstance), List<PropertyDraft>> _propertyLookup = [];
    private readonly List<PropertyDraft> _propertyDrafts = [];
    private readonly HashSet<string> _usedTypeNames = [with(StringComparer.OrdinalIgnoreCase)];
    private readonly HashSet<string> _functionNames = [with(StringComparer.Ordinal)];
    private readonly HashSet<(string Host, string Name)> _usedMemberNames = [];
    private readonly List<FunctionInfo> _functions = [];
    private readonly Dictionary<string, string> _libraryBySymbol = [with(StringComparer.Ordinal)];
    private readonly List<ConstantInfo> _constants = [];
    private readonly HashSet<string> _constantNames = [with(StringComparer.Ordinal)];
    private HostInfo? _nativeMethodsHost;
    private CSharpWrapGenerator? _wrap;
    private string? _currentFunction;
    private int _anonymousCounter;

    /// <summary>
    /// 解析已构建的翻译单元并输出绑定。
    /// </summary>
    public void Invoke(CXTranslationUnit translationUnit)
    {
        _wrap = new CSharpWrapGenerator(new CSharpWrapGeneratorOptions
        {
            Output = options.ManagedMethodOutput,
            NameSpace = options.NameSpace,
            MethodsPerFile = options.MethodsPerFile,
        });

        LoadLibraryManifest();

        List<CXCursor> functions = [.. CollectFunctions(translationUnit, true)
            .Where(function => _functionNames.Add(function.Spelling.ToString()))];

        ScanHandleFunctions(functions);
        ProcessTypes(CollectTypes(functions));
        BuildHosts();
        AnalyzeFunctions(functions);
        AnalyzeConstants(CollectConstants(translationUnit));
        WriteOutputs(functions.Count);
    }

    /// <summary>
    /// 读取 <see cref="CBindingGeneratorOptions.LibraryManifest"/>：每行一条「库名 + TAB + 符号」。
    /// </summary>
    private void LoadLibraryManifest()
    {
        if (options.LibraryManifest is null || options.LibraryManifest.Exists is false)
            return;

        foreach (string line in File.ReadAllLines(options.LibraryManifest.FullName))
        {
            string trimmed = line.Trim();
            if (trimmed.Length is 0 || trimmed.StartsWith('#') || trimmed.StartsWith("//", StringComparison.Ordinal))
                continue;

            int separator = trimmed.LastIndexOf('\t');
            if (separator <= 0 || separator >= trimmed.Length - 1)
                separator = trimmed.IndexOf(' ');

            if (separator <= 0 || separator >= trimmed.Length - 1)
                continue;

            string library = trimmed[..separator].Trim();
            string symbol = trimmed[(separator + 1)..].Trim();
            if (library.Length is 0 || symbol.Length is 0)
                continue;

            _libraryBySymbol[symbol] = library;
        }
    }

    /// <summary>
    /// 函数所属的原生库：优先用清单记录，缺失时回退到 <see cref="CBindingGeneratorOptions.LibraryName"/>。
    /// </summary>
    private string LibraryOf(string symbol)
        => _libraryBySymbol.TryGetValue(symbol, out string? library) ? library : options.LibraryName;

    /// <summary>
    /// 阶段 1 前置：为所有签名中出现的类型分配名称并生成定义，字段类型在生成过程中递归补齐。
    /// </summary>
    private void ProcessTypes(IEnumerable<CXType> types)
    {
        foreach (CXType type in types)
        {
            try
            {
                EnsureTypeName(type);
            }
            catch (NotSupportedException ex)
            {
                Console.Error.WriteLine($"跳过类型 {type.Spelling}：{ex.Message}");
            }
        }
    }

    // ---- 类型映射 ----

    private MappedType MapType(CXType type, TypeUsage usage)
    {
        string spelling = type.Spelling.ToString();
        if (_typeNameMap.TryGetValue(spelling, out string? mappedName))
            return new(mappedName, [], spelling);

        // typedef 链上的已知类型（例如 const size_t、typedef 过的整型）同样按配置映射
        if (TryMapTypedef(type, out string? typedefMapped))
            return new(typedefMapped!, [], spelling);

        CXType shape = Unwrap(type);
        switch (shape.kind)
        {
            case CXTypeKind.CXType_Void:
                return new("void", [], spelling);

            case CXTypeKind.CXType_Bool:
                return usage is TypeUsage.Parameter or TypeUsage.Return
                    ? new(
                        "bool",
                        [options.FourByteBoolean ? "MarshalAs(UnmanagedType.Bool)" : "MarshalAs(UnmanagedType.I1)"],
                        spelling)
                    : new("bool", [], spelling);

            case CXTypeKind.CXType_WChar:
                // wchar_t 的宽度由目标平台决定：Windows 为 2 字节，Linux/macOS 为 4 字节
                return new(shape.SizeOf is 4 ? "uint" : "char", [], spelling);

            case CXTypeKind.CXType_Enum:
                return new(EnsureEnumName(shape.Declaration), [], spelling);

            case CXTypeKind.CXType_Record:
                {
                    CXCursor declaration = shape.Declaration;
                    if (declaration.Definition.IsNull)
                        throw new NotSupportedException($"类型 {spelling} 只有前向声明，不能按值使用");

                    return new(EnsureRecordName(declaration), [], spelling);
                }

            case CXTypeKind.CXType_Pointer:
                return MapPointer(shape, usage, spelling);

            case CXTypeKind.CXType_ConstantArray:
            case CXTypeKind.CXType_IncompleteArray:
            case CXTypeKind.CXType_VariableArray:
                // 数组在参数位置会退化为指针
                return new(MapType(shape.ArrayElementType, TypeUsage.Nested).Type + "*", [], spelling);

            case CXTypeKind.CXType_FunctionProto:
            case CXTypeKind.CXType_FunctionNoProto:
                return MapFunctionPointer(shape, spelling);

            case CXTypeKind.CXType_Unexposed:
                return MapType(shape.CanonicalType, usage);

            default:
                if (ScalarName(shape.kind) is { } scalar)
                    return new(scalar, [], spelling);

                throw new NotSupportedException($"类型 {spelling} 暂不支持");
        }
    }

    private MappedType MapPointer(CXType pointer, TypeUsage usage, string spelling)
    {
        CXType pointee = pointer.PointeeType;
        CXType canonicalPointee = pointee.CanonicalType;

        if (canonicalPointee.kind is CXTypeKind.CXType_FunctionProto or CXTypeKind.CXType_FunctionNoProto)
            return MapFunctionPointer(canonicalPointee, spelling);

        if (usage is TypeUsage.Parameter or TypeUsage.Return
            && (pointee.IsConstQualified || canonicalPointee.IsConstQualified)
            && canonicalPointee.kind is CXTypeKind.CXType_Char_S
                or CXTypeKind.CXType_Char_U
                or CXTypeKind.CXType_UChar
                or CXTypeKind.CXType_SChar)
        {
            return new(
                "string?",
                CharMarshalling(usage is TypeUsage.Return
                    ? options.ReturnStringMarshallerName
                    : options.StringMarshallerName),
                spelling);
        }

        if (usage is TypeUsage.Field)
            return new("nint", [], spelling);

        if (canonicalPointee.kind is CXTypeKind.CXType_Void)
            return new("nint", [], spelling);

        if (canonicalPointee.kind is CXTypeKind.CXType_Record)
        {
            CXCursor declaration = canonicalPointee.Declaration;
            if (declaration.Definition.IsNull)
            {
                // 句柄的创建/释放函数需要原始指针，不能被包装成 SafeHandle
                if (usage is TypeUsage.Nested
                    || IsHandleFunction(declaration)
                    || options.SafeHandleMarshallerName.Length is 0)
                {
                    return new("nint", [], spelling);
                }

                string handleName = EnsureRecordName(declaration);
                return new(
                    handleName + "?",
                    [$"MarshalUsing(typeof({SafeHandleMarshallerName(handleName)}))"],
                    spelling);
            }

            return new(EnsureRecordName(declaration) + "*", [], spelling);
        }

        if (canonicalPointee.kind is CXTypeKind.CXType_Enum)
            return new(EnsureEnumName(canonicalPointee.Declaration) + "*", [], spelling);

        return new(MapType(pointee, TypeUsage.Nested).Type + "*", [], spelling);
    }

    private MappedType MapFunctionPointer(CXType function, string spelling)
    {
        if (function.IsFunctionTypeVariadic)
            throw new NotSupportedException("变参函数指针无法映射到 C# 函数指针");

        List<string> parts = [];
        int count = function.NumArgTypes;
        for (int i = 0; i < count; i++)
            parts.Add(MapType(function.GetArgType((uint)i), TypeUsage.Nested).Type);
        parts.Add(MapType(function.ResultType, TypeUsage.Nested).Type);

        string convention = function.FunctionTypeCallingConv switch
        {
            CXCallingConv.CXCallingConv_X86StdCall => "Stdcall",
            CXCallingConv.CXCallingConv_X86FastCall => "Fastcall",
            CXCallingConv.CXCallingConv_X86ThisCall => "Thiscall",
            _ => "Cdecl",
        };

        return new($"delegate* unmanaged[{convention}]<{string.Join(", ", parts)}>", [], spelling);
    }

    private static string[] CharMarshalling(string marshallerName)
        => marshallerName.Length is 0
            ? ["MarshalAs(UnmanagedType.LPUTF8Str)"]
            : [$"MarshalUsing(typeof({marshallerName}))"];

    private string SafeHandleMarshallerName(string handleName)
    {
        string template = options.SafeHandleMarshallerName;
        int index = template.IndexOf("<T>", StringComparison.Ordinal);
        return index is < 0
            ? template
            : template[..index] + "<" + handleName + ">" + template[(index + "<T>".Length)..];
    }

    /// <summary>
    /// 在 typedef 链上查找 <see cref="CBindingGeneratorOptions.TypeMap"/> 中的名称，处理 <c>const size_t</c> 等写法。
    /// </summary>
    private bool TryMapTypedef(CXType type, out string? mapped)
    {
        mapped = null;
        CXType current = type;
        for (int guard = 0; guard is < 64; guard++)
        {
            switch (current.kind)
            {
                case CXTypeKind.CXType_Elaborated:
                    current = current.NamedType;
                    break;
                case CXTypeKind.CXType_Attributed:
                    current = current.ModifiedType;
                    break;
                case CXTypeKind.CXType_Typedef:
                    {
                        CXCursor declaration = current.Declaration;
                        if (declaration.IsNull)
                        {
                            current = current.CanonicalType;
                            break;
                        }

                        if (_typeNameMap.TryGetValue(declaration.Spelling.ToString(), out mapped))
                            return true;

                        current = declaration.TypedefDeclUnderlyingType;
                        break;
                    }
                default:
                    return false;
            }
        }

        return false;
    }

    /// <summary>
    /// 剥去 typedef、elaborated、attributed 等语法糖；已知 typedef 名称在 <see cref="MapType"/> 入口处已经拦截。
    /// </summary>
    private static CXType Unwrap(CXType type)
    {
        while (true)
        {
            switch (type.kind)
            {
                case CXTypeKind.CXType_Elaborated:
                    type = type.NamedType;
                    continue;
                case CXTypeKind.CXType_Attributed:
                    type = type.ModifiedType;
                    continue;
                case CXTypeKind.CXType_Typedef:
                    {
                        CXCursor declaration = type.Declaration;
                        if (declaration.IsNull)
                            return type.CanonicalType;

                        type = declaration.TypedefDeclUnderlyingType;
                        continue;
                    }
                case CXTypeKind.CXType_Unexposed:
                case (CXTypeKind)177: // CXType_Paren
                    type = type.CanonicalType;
                    continue;
                default:
                    return type;
            }
        }
    }

    private static string? ScalarName(CXTypeKind kind)
        => kind switch
        {
            CXTypeKind.CXType_Char_S or CXTypeKind.CXType_Char_U or CXTypeKind.CXType_UChar => "byte",
            CXTypeKind.CXType_SChar => "sbyte",
            CXTypeKind.CXType_WChar or CXTypeKind.CXType_Char16 => "char",
            CXTypeKind.CXType_Char32 => "uint",
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
            CXTypeKind.CXType_Half or CXTypeKind.CXType_Float16 => "Half",
            _ => null,
        };

    // ---- 名称 ----

    private static uint KeyOf(CXCursor declaration)
        => declaration.CanonicalCursor.Hash;

    /// <summary>
    /// 取得原生类型名；匿名类型优先使用为其命名的 typedef，否则使用合成名称。
    /// </summary>
    private string NativeTypeName(CXCursor declaration)
    {
        string spelling = declaration.Spelling.ToString();
        if (!declaration.IsAnonymous && spelling.Length is not 0)
            return spelling;

        CXCursor typedef = declaration.TypedefNameForAnonDecl;
        return typedef.IsNull
            ? $"anonymous_{++_anonymousCounter}"
            : typedef.Spelling.ToString();
    }

    private string EnsureTypeName(CXType type)
    {
        CXType shape = Unwrap(type);
        return shape.kind switch
        {
            CXTypeKind.CXType_Record => EnsureRecordName(shape.Declaration),
            CXTypeKind.CXType_Enum => EnsureEnumName(shape.Declaration),
            _ => MapType(shape, TypeUsage.Nested).Type,
        };
    }

    private string EnsureRecordName(CXCursor declaration)
    {
        uint key = KeyOf(declaration);
        if (_typeStates.TryGetValue(key, out TypeState? existing))
        {
            if (existing.Error is { } error)
                throw new NotSupportedException(error);

            return existing.Name;
        }

        CXCursor definition = declaration.Definition;
        if (definition.IsNull)
        {
            string handleNativeName = NativeTypeName(declaration);
            string handleName = AllocateTypeName(handleNativeName, "_handle");
            TypeState handleState = new(handleName);
            _typeStates[key] = handleState;
            _hostKinds[key] = HostKind.Handle;
            _nativeSpellings[key] = handleNativeName;
            _handleHosts[key] = new(handleName, key, HostKind.Handle, handleNativeName);
            _typeSources[handleName] = EmitHandleType(
                handleName,
                EscapeIdentifierOrNull(_createFunctions.GetValueOrDefault(key)),
                _createFunctionArity.GetValueOrDefault(key, -1),
                EscapeIdentifierOrNull(_releaseFunctions.GetValueOrDefault(key)));
            return handleName;
        }

        string nativeName = NativeTypeName(declaration);
        TypeState state = new(AllocateTypeName(nativeName, "Type"));
        _typeStates[key] = state;
        _hostKinds[key] = HostKind.Struct;
        _nativeSpellings[key] = nativeName;
        try
        {
            _typeSources[state.Name] = EmitRecord(definition, state.Name, nativeName);
        }
        catch (NotSupportedException ex)
        {
            state.Error = ex.Message;
            throw;
        }

        return state.Name;
    }

    private string EnsureEnumName(CXCursor declaration)
    {
        uint key = KeyOf(declaration);
        if (_typeStates.TryGetValue(key, out TypeState? existing))
        {
            if (existing.Error is { } error)
                throw new NotSupportedException(error);

            return existing.Name;
        }

        CXCursor definition = declaration.Definition;
        if (definition.IsNull)
            throw new NotSupportedException($"枚举 {declaration.Spelling} 没有完整定义");

        string nativeName = NativeTypeName(declaration);
        TypeState state = new(AllocateTypeName(nativeName, "Type"));
        _typeStates[key] = state;
        _hostKinds[key] = HostKind.Enum;
        _nativeSpellings[key] = nativeName;
        try
        {
            _typeSources[state.Name] = EmitEnum(definition, state.Name);
        }
        catch (NotSupportedException ex)
        {
            state.Error = ex.Message;
            throw;
        }

        return state.Name;
    }

    private string AllocateTypeName(string nativeName, string conflictSuffix)
    {
        string name = EscapeIdentifier(nativeName.Pascalize());
        while (!_usedTypeNames.Add(name))
            name += conflictSuffix;

        return name;
    }

    private string? EscapeIdentifierOrNull(string? name)
        => name is { Length: > 0 } ? EscapeIdentifier(name) : null;

    private string EscapeIdentifier(string name)
    {
        if (name.Length is 0)
            return "_";

        using StringWriter writer = CreateWriter();
        if (char.IsDigit(name[0]))
            writer.Write('_');

        foreach (char character in name)
            writer.Write(char.IsLetterOrDigit(character) || character is '_' ? character : '_');

        string identifier = writer.ToString();
        return options.Keywords.Contains(identifier) ? "@" + identifier : identifier;
    }

    private static bool IsIdentifier(string name)
    {
        if (name.Length is 0 || !(char.IsLetter(name[0]) || name[0] is '_'))
            return false;

        foreach (char character in name)
        {
            if (!char.IsLetterOrDigit(character) && character is not '_')
                return false;
        }

        return true;
    }

    // ---- 阶段 1：类型定义 ----

    private string EmitRecord(CXCursor definition, string name, string nativeName)
    {
        long size = definition.Type.SizeOf;
        if (size is <= 0)
            throw new NotSupportedException($"类型 {name} 的大小未知，无法生成布局");

        List<string> members = [];
        Dictionary<long, string> bitFieldStorages = [];
        int index = 0;

        for (int i = 0; i < definition.NumFields; i++)
        {
            CXCursor field = definition.GetField((uint)i);
            index++;
            long offset = field.OffsetOfField / 8;
            string nativeFieldName = field.Spelling.ToString();

            if (field.IsBitField)
            {
                // 位域没有直接对应物，按存储单元暴露为私有字段，保证结构体大小与偏移正确。
                // OffsetOfField 是位偏移，需要向下对齐到存储单元的起点，否则同一单元内的多个位域会生成重叠字段。
                long storageSize = field.Type.SizeOf;
                long storageOffset = storageSize is > 0 ? offset / storageSize * storageSize : offset;
                string storage = BitFieldStorage(field.Type);
                if (bitFieldStorages.TryAdd(storageOffset, storage))
                {
                    // 匿名位域（含零宽位域）只影响布局，没有可暴露的名称为其标注
                    string label = field.IsUnnamedBitfield ? "（匿名）" : nativeFieldName;
                    members.Add($"    // 位域 {label} 无法直接映射，按 {storage} 存储单元处理");
                    members.Add($"    [FieldOffset({storageOffset})] private {storage} _bitfield{storageOffset};");
                }

                continue;
            }

            CXType fieldShape = Unwrap(field.Type);
            if (fieldShape.kind is CXTypeKind.CXType_IncompleteArray)
            {
                members.Add($"    // 柔性数组字段 {nativeFieldName} 长度不固定，已跳过");
                continue;
            }

            if (fieldShape.kind is CXTypeKind.CXType_ConstantArray && fieldShape.NumElements is <= 0)
            {
                members.Add($"    // 零长度数组字段 {nativeFieldName} 不占据存储空间，已跳过");
                continue;
            }

            string fieldName = nativeFieldName.Length is 0 ? $"Anonymous{index}" : EscapeIdentifier(nativeFieldName);
            string fieldType = MapFieldType(field.Type, name, fieldName);
            members.Add("    /// <summary>");
            members.Add($"    /// 原生字段类型：<c>{XmlEscape(field.Type.Spelling.ToString())}</c>。");
            members.Add("    /// </summary>");
            members.Add($"    [FieldOffset({offset})] public {fieldType} {fieldName};");
        }

        string rawType = definition.Type.Spelling.ToString();
        if (rawType.Contains("(unnamed", StringComparison.Ordinal)
            || rawType.Contains("(anonymous", StringComparison.Ordinal))
        {
            // 匿名类型的内置拼写包含源码绝对路径，改为稳定的合成名称
            string keyword = definition.kind is CXCursorKind.CXCursor_UnionDecl ? "union" : "struct";
            rawType = $"{keyword} {nativeName}";
        }

        using StringWriter writer = CreateWriter();
        writer.WriteLine("/// <summary>");
        writer.Write("/// 原生类型：<c>");
        writer.Write(XmlEscape(rawType));
        writer.WriteLine("</c>。");
        writer.WriteLine("/// </summary>");
        writer.Write("[StructLayout(LayoutKind.Explicit, Size = ");
        writer.Write(size.ToString(CultureInfo.InvariantCulture));
        writer.WriteLine(")]");
        writer.Write("public unsafe partial struct ");
        writer.WriteLine(name);
        writer.WriteLine("{");
        foreach (string member in members)
            writer.WriteLine(member);

        writer.Write("}");
        return writer.ToString();
    }

    private static string EmitHandleType(string name, string? createFunc, int createArity, string? releaseFunc)
    {
        using StringWriter writer = CreateWriter();
        writer.WriteLine("/// <summary>");
        writer.Write("/// 原生类型：<c>");
        writer.Write(XmlEscape(name + "*"));
        writer.WriteLine("</c> 的不透明句柄。");
        writer.WriteLine("/// </summary>");
        writer.Write("public unsafe partial class ");
        writer.Write(name);
        writer.Write(" : global::System.Runtime.InteropServices.SafeHandle");
        writer.WriteLine();
        writer.WriteLine("{");
        writer.WriteLine("    public override bool IsInvalid => handle is 0;");
        writer.WriteLine();
        writer.Write("    internal ");
        writer.Write(name);
        writer.WriteLine("(nint handle, bool ownsHandle) : base(handle, ownsHandle)");
        writer.WriteLine("    {");
        writer.WriteLine("    }");
        writer.WriteLine();

        // 只有无参构造函数才能表达成默认构造函数；带参数的由封装层单独暴露
        if (createFunc is not null && createArity is 0)
        {
            writer.Write("    public ");
            writer.Write(name);
            writer.Write("() : this(");
            writer.Write(createFunc);
            writer.WriteLine("(), true)");
            writer.WriteLine("    {");
            writer.WriteLine("    }");
            writer.WriteLine();
        }

        writer.WriteLine("    protected override bool ReleaseHandle()");
        writer.WriteLine("    {");
        if (releaseFunc is not null)
        {
            writer.Write("        ");
            writer.Write(releaseFunc);
            writer.WriteLine("(handle);");
        }

        writer.WriteLine("        return true;");
        writer.WriteLine("    }");
        writer.Write("}");
        return writer.ToString();
    }

    private string MapFieldType(CXType type, string owner, string fieldName)
    {
        CXType shape = Unwrap(type);
        if (shape.kind is CXTypeKind.CXType_ConstantArray)
        {
            long count = shape.NumElements;
            if (count is <= 0 or > 1_000_000)
                throw new NotSupportedException($"数组 {fieldName} 的长度 {count} 无法生成 InlineArray");

            string bufferName = AllocateTypeName($"{owner}_{fieldName.TrimStart('@')}_buffer", "Buffer");
            // 多维数组的元素本身也是定长数组，需要继续生成嵌套的缓冲区类型
            string element = MapFieldType(shape.ArrayElementType, bufferName, "Element");

            using StringWriter writer = CreateWriter();
            writer.WriteLine("/// <summary>");
            writer.Write("/// 定长数组缓冲区：<c>");
            writer.Write(XmlEscape(shape.Spelling.ToString()));
            writer.WriteLine("</c>。");
            writer.WriteLine("/// </summary>");
            writer.Write("[InlineArray(");
            writer.Write(count.ToString(CultureInfo.InvariantCulture));
            writer.WriteLine(")]");
            writer.Write("public partial struct ");
            writer.WriteLine(bufferName);
            writer.WriteLine("{");
            writer.Write("    private ");
            writer.Write(element);
            writer.WriteLine(" _element0;");
            writer.Write("}");
            _typeSources[bufferName] = writer.ToString();
            return bufferName;
        }

        return MapType(type, TypeUsage.Field).Type;
    }

    private static string BitFieldStorage(CXType type)
    {
        long size = type.SizeOf;
        bool unsigned = type.IsUnsigned;
        return (size, unsigned) switch
        {
            (1, true) => "byte",
            (1, false) => "sbyte",
            (2, true) => "ushort",
            (2, false) => "short",
            (8, true) => "ulong",
            (8, false) => "long",
            (_, true) => "uint",
            _ => "int",
        };
    }

    private string EmitEnum(CXCursor definition, string name)
    {
        CXType underlying = definition.EnumDecl_IntegerType;
        string underlyingName = EnumUnderlyingType(underlying);
        bool unsigned = underlying.IsUnsigned;

        using StringWriter members = CreateWriter();
        for (int i = 0; i < definition.NumEnumerators; i++)
        {
            CXCursor constant = definition.GetEnumerator((uint)i);
            string value = unsigned
                ? constant.EnumConstantDeclUnsignedValue.ToString(CultureInfo.InvariantCulture)
                : constant.EnumConstantDeclValue.ToString(CultureInfo.InvariantCulture);
            members.Write("    ");
            members.Write(EscapeIdentifier(constant.Spelling.ToString()));
            members.Write(" = ");
            members.Write(value);
            members.WriteLine(",");
        }

        using StringWriter writer = CreateWriter();
        writer.WriteLine("/// <summary>");
        writer.Write("/// 原生类型：<c>");
        writer.Write(XmlEscape(definition.Type.Spelling.ToString()));
        writer.WriteLine("</c>。");
        writer.WriteLine("/// </summary>");
        writer.Write("public enum ");
        writer.Write(name);
        writer.Write(" : ");
        writer.WriteLine(underlyingName);
        writer.WriteLine("{");
        foreach (string line in members.ToString().ReplaceLineEndings("\n").Split('\n', StringSplitOptions.RemoveEmptyEntries))
            writer.WriteLine(line);

        writer.Write("}");
        return writer.ToString();
    }

    private static string EnumUnderlyingType(CXType type)
    {
        CXType canonical = type.CanonicalType;
        long size = canonical.SizeOf;
        bool unsigned = canonical.IsUnsigned;
        return (size, unsigned) switch
        {
            (1, true) => "byte",
            (1, false) => "sbyte",
            (2, true) => "ushort",
            (2, false) => "short",
            (8, true) => "ulong",
            (8, false) => "long",
            (_, true) => "uint",
            _ => "int",
        };
    }

    // ---- 句柄创建/释放函数识别 ----

    private void ScanHandleFunctions(IEnumerable<CXCursor> functions)
    {
        foreach (CXCursor function in functions)
        {
            string name = function.Spelling.ToString();
            if (name.Length is 0)
                continue;

            CXType result = function.ResultType;
            if (result.CanonicalType.kind is CXTypeKind.CXType_Void
                && function.NumArguments is 1
                && LooksLikeRelease(name)
                && TryGetOpaqueRecord(function.GetArgument(0).Type, out CXCursor releaseTarget))
            {
                _releaseFunctions.TryAdd(KeyOf(releaseTarget), name);
                continue;
            }

            // 构造函数可能带参数（C++ 的 type_new(...)），因此不限定参数个数
            if (LooksLikeCreate(name)
                && TryGetOpaqueRecord(result, out CXCursor createTarget))
            {
                uint createKey = KeyOf(createTarget);
                _createFunctions.TryAdd(createKey, name);
                _createFunctionArity.TryAdd(createKey, function.NumArguments);
            }
        }
    }

    private static bool LooksLikeRelease(string name)
        => name.Contains("destroy", StringComparison.OrdinalIgnoreCase)
            || name.Contains("release", StringComparison.OrdinalIgnoreCase)
            || name.Contains("close", StringComparison.OrdinalIgnoreCase)
            || name.Contains("delete", StringComparison.OrdinalIgnoreCase)
            || name.Contains("dispose", StringComparison.OrdinalIgnoreCase)
            || name.Contains("finalize", StringComparison.OrdinalIgnoreCase)
            || name.Contains("dealloc", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith("_free", StringComparison.OrdinalIgnoreCase);

    private static bool LooksLikeCreate(string name)
        => name.Contains("create", StringComparison.OrdinalIgnoreCase)
            || name.Contains("alloc", StringComparison.OrdinalIgnoreCase)
            || name.Contains("new", StringComparison.OrdinalIgnoreCase)
            || name.Contains("open", StringComparison.OrdinalIgnoreCase);

    private static bool TryGetOpaqueRecord(CXType type, out CXCursor declaration)
    {
        declaration = default;
        if (type.CanonicalType.kind is not CXTypeKind.CXType_Pointer)
            return false;

        CXType pointee = type.CanonicalType.PointeeType;
        if (pointee.CanonicalType.kind is not CXTypeKind.CXType_Record)
            return false;

        CXCursor candidate = pointee.CanonicalType.Declaration;
        if (!candidate.Definition.IsNull)
            return false;

        declaration = candidate;
        return true;
    }

    private bool IsHandleFunction(CXCursor declaration)
    {
        if (_currentFunction is null)
            return false;

        uint key = KeyOf(declaration);
        return (_createFunctions.TryGetValue(key, out string? create) && create == _currentFunction)
            || (_releaseFunctions.TryGetValue(key, out string? release) && release == _currentFunction);
    }

    // ---- 宿主 ----

    /// <summary>
    /// 建立宿主表；枚举不能承载成员，因此不作为宿主，其派生函数统一归入 <c>NativeMethods</c>。
    /// </summary>
    private void BuildHosts()
    {
        foreach (HostInfo host in _handleHosts.Values)
            _hosts.Add(host);

        foreach ((uint key, TypeState state) in _typeStates)
        {
            if (state.Error is not null || _hostKinds.GetValueOrDefault(key) is not HostKind.Struct)
                continue;

            _hosts.Add(new HostInfo(state.Name, key, HostKind.Struct, _nativeSpellings.GetValueOrDefault(key, "")));
        }

        string nativeMethodsName = EscapeIdentifier(options.DefaultNativeMethodsClassName);
        _nativeMethodsHost = new HostInfo(nativeMethodsName, 0, HostKind.NativeMethods, "");
    }

    /// <summary>
    /// 以最长前缀匹配函数所属宿主；无法匹配时归入 <c>NativeMethods</c>。
    /// </summary>
    private HostInfo ResolveHost(string functionName, out string remainder)
    {
        HostInfo? best = null;
        string bestPrefix = "";

        foreach (HostInfo host in _hosts)
        {
            foreach (string prefix in PrefixCandidates(host))
            {
                if (prefix.Length is 0)
                    continue;

                if (prefix.Length <= bestPrefix.Length)
                    continue;

                if (!functionName.StartsWith(prefix + "_", StringComparison.OrdinalIgnoreCase))
                    continue;

                best = host;
                bestPrefix = prefix;
            }
        }

        if (best is null)
        {
            remainder = functionName;
            return _nativeMethodsHost!;
        }

        remainder = functionName[(bestPrefix.Length + 1)..];
        return best;
    }

    private static IEnumerable<string> PrefixCandidates(HostInfo host)
    {
        if (host.NativeSpelling.Length is 0)
            yield break;

        yield return host.NativeSpelling;

        string underscore = host.NativeSpelling.Underscore();
        if (underscore.Length is not 0 && underscore != host.NativeSpelling)
            yield return underscore;
    }

    // ---- 函数分析 ----

    private void AnalyzeFunctions(IEnumerable<CXCursor> functions)
    {
        foreach (CXCursor cursor in functions)
        {
            FunctionInfo info;
            try
            {
                info = AnalyzeFunction(cursor);
            }
            catch (NotSupportedException ex)
            {
                Console.Error.WriteLine($"跳过函数 {cursor.Spelling}：{ex.Message}");
                continue;
            }

            _functions.Add(info);
            EmitImport(info);

            if (info.IsHandleAssociate)
            {
                // create 函数无参时已由 SafeHandle 的默认构造函数覆盖；
                // 带参数时还要生成对应的托管构造函数重载，否则调用方无法按给定参数创建实例
                if (info.IsCreateAssociate && info.Parameters.Count is not 0)
                {
                    info.MemberName = info.Host.Name;
                    info.Host.Members.Add(BuildHandleConstructor(info));
                }

                continue;
            }

            if (info.Role is FunctionRole.Method || info.PropertyName is null)
            {
                info.MemberName = AllocateMemberName(info.Host, info.ManagedName);
                info.Host.Members.Add(CSharpWrapGenerator.EmitMethod(BuildWrapFunction(info)));
                continue;
            }

            AddAccessor(info);
        }

        EmitProperties();
    }

    /// <summary>
    /// 把已分析的函数转换为封装生成器的输入，并把「指针 + 长度」参数对折叠为 Span 视图。
    /// </summary>
    private static WrapFunction BuildWrapFunction(FunctionInfo info)
    {
        WrapSpanReturn? spanReturn = TryBuildSpanReturn(info);
        return new WrapFunction
        {
            ImportName = info.ImportName,
            MemberName = info.MemberName,
            ReturnType = spanReturn is null
                ? info.Return.Type
                : SpanTypeName(spanReturn.ElementType, spanReturn.ReadOnly),
            Parameters = [.. BuildWrapParameters(info)],
            Arguments = [.. BuildWrapArguments(info)],
            FixedPointers = [.. BuildFixedPointers(info)],
            IsInstance = info.IsInstance,
            IsStructInstance = info.IsStructInstance,
            SelfType = info.Self?.Mapped.Type.TrimEnd('*'),
            SpanReturn = spanReturn,
        };
    }

    /// <summary>
    /// 识别 <c>T* f(size_t* size)</c>：唯一参数是长度输出指针、返回值是元素指针时，托管侧返回 Span。
    /// </summary>
    /// <remarks>
    /// 缓冲区由原生侧分配并持有，因此暴露为 <c>ReadOnlySpan&lt;T&gt;</c>，避免调用方误以为可以改写或释放。
    /// </remarks>
    private static WrapSpanReturn? TryBuildSpanReturn(FunctionInfo info)
    {
        if (info.Parameters.Count is not 1 || info.Return.Type.EndsWith('*') is false)
            return null;

        ParameterInfo lengthParameter = info.Parameters[0];
        if (!IsLengthPointer(lengthParameter.NativeType))
            return null;

        string elementType = ElementTypeOf(info.Return);
        if (elementType is "void" or "nint" || elementType.EndsWith('*'))
            return null;

        // 长度指针在导入签名里是 T*，局部变量用 pointee 类型并以地址传入
        string countType = lengthParameter.Mapped.Type.TrimEnd('*');
        return new WrapSpanReturn(elementType, true, "count", countType, "pointer");
    }

    /// <summary>
    /// 识别相邻的「元素指针 + 长度」参数对；元素类型必须是可映射的 blittable 值类型。
    /// </summary>
    private static IEnumerable<(int LengthIndex, int PointerIndex)> FindArrayPairs(List<ParameterInfo> parameters)
    {
        for (int i = 0; i < parameters.Count; i++)
        {
            int neighbour;
            if (i > 0 && IsElementPointer(parameters[i - 1]) && IsSizeParameter(parameters[i].NativeType))
            {
                neighbour = i - 1;
                yield return (i, neighbour);
                continue;
            }

            if (i + 1 < parameters.Count
                && IsSizeParameter(parameters[i].NativeType)
                && IsElementPointer(parameters[i + 1]))
            {
                yield return (i, i + 1);
            }
        }
    }

    /// <summary>
    /// 长度参数：<c>size_t</c>、<c>ssize_t</c> 等与平台指针等宽的整型。
    /// </summary>
    private static bool IsSizeParameter(CXType nativeType)
    {
        string spelling = nativeType.Spelling.ToString();
        if (spelling is "size_t" or "ssize_t" or "uintptr_t" or "intptr_t" or "ptrdiff_t")
            return true;

        CXType shape = Unwrap(nativeType);
        return shape.SizeOf == IntPtr.Size
            && shape.kind is CXTypeKind.CXType_Int
                or CXTypeKind.CXType_UInt
                or CXTypeKind.CXType_Long
                or CXTypeKind.CXType_ULong
                or CXTypeKind.CXType_LongLong
                or CXTypeKind.CXType_ULongLong
                or CXTypeKind.CXType_Typedef
                or CXTypeKind.CXType_Elaborated;
    }

    private static bool IsLengthPointer(CXType nativeType)
    {
        CXType canonical = nativeType.CanonicalType;
        return canonical.kind is CXTypeKind.CXType_Pointer
            && IsSizeParameter(canonical.PointeeType);
    }

    /// <summary>
    /// 可作为 Span 元素来源的指针：指向 blittable 值类型，且不是 <c>void</c> 或函数指针。
    /// </summary>
    private static bool IsElementPointer(ParameterInfo parameter)
    {
        CXType canonical = parameter.NativeType.CanonicalType;
        if (canonical.kind is not CXTypeKind.CXType_Pointer)
            return false;

        if (parameter.Mapped.Type.EndsWith('*') is false)
            return false;

        // 泛型类型实参只能是值类型：指针、函数原型、数组等元素类型不能折叠成 Span，
        // 否则 const char ** 会生成非法的 System.Span<byte*>
        CXType pointee = Unwrap(canonical.PointeeType);
        return pointee.kind is not (CXTypeKind.CXType_Void
            or CXTypeKind.CXType_FunctionProto
            or CXTypeKind.CXType_FunctionNoProto
            or CXTypeKind.CXType_Pointer
            or CXTypeKind.CXType_ConstantArray
            or CXTypeKind.CXType_IncompleteArray
            or CXTypeKind.CXType_VariableArray);
    }

    /// <summary>
    /// 对外暴露的形参：数组对合并为一个 <c>Span&lt;T&gt;</c> 或 <c>ReadOnlySpan&lt;T&gt;</c>，
    /// 返回 Span 的形态下长度输出指针不暴露，其余保持原生映射结果。
    /// </summary>
    private static IEnumerable<WrapParameter> BuildWrapParameters(FunctionInfo info)
    {
        if (TryBuildSpanReturn(info) is not null)
            yield break;

        Dictionary<int, int> pointerToLength = ArrayPairs(info.Parameters);
        HashSet<int> consumed = [.. pointerToLength.Values];
        for (int i = 0; i < info.Parameters.Count; i++)
        {
            if (consumed.Contains(i))
                continue;

            ParameterInfo parameter = info.Parameters[i];
            if (pointerToLength.ContainsKey(i))
            {
                bool readOnly = parameter.NativeType.CanonicalType.PointeeType.IsConstQualified;
                yield return new(parameter.Name, SpanTypeName(ElementTypeOf(parameter), readOnly));
                continue;
            }

            yield return new(parameter.Name, parameter.Mapped.Type);
        }
    }

    /// <summary>
    /// 调用的实参序列，与原生形参个数一一对应：数组对的指针位置传 <c>pointerN</c>，长度位置传
    /// <c>(lengthType)span.Length</c>；返回 Span 的形态把长度指针按地址传入；其他位置沿用原生形参名。
    /// </summary>
    private static IEnumerable<string> BuildWrapArguments(FunctionInfo info)
    {
        if (TryBuildSpanReturn(info) is not null)
        {
            yield return "&count";
            yield break;
        }

        Dictionary<int, int> pointerToLength = ArrayPairs(info.Parameters);
        Dictionary<int, int> lengthToPointer = [];
        foreach ((int pointerIndex, int lengthIndex) in pointerToLength)
            lengthToPointer[lengthIndex] = pointerIndex;

        for (int i = 0; i < info.Parameters.Count; i++)
        {
            if (pointerToLength.ContainsKey(i))
            {
                yield return $"pointer{i}";
                continue;
            }

            if (lengthToPointer.TryGetValue(i, out int pointerIndex))
            {
                string spanName = info.Parameters[pointerIndex].Name;
                string lengthType = info.Parameters[i].Mapped.Type;
                yield return $"({lengthType}){spanName}.Length";
                continue;
            }

            yield return info.Parameters[i].Name;
        }
    }

    /// <summary>
    /// 需要先 <c>fixed</c> 钉住并取指针的 Span 形参。
    /// </summary>
    private static IEnumerable<WrapFixedPointer> BuildFixedPointers(FunctionInfo info)
    {
        Dictionary<int, int> pointerToLength = ArrayPairs(info.Parameters);
        foreach ((int pointerIndex, int lengthIndex) in pointerToLength)
        {
            ParameterInfo pointer = info.Parameters[pointerIndex];
            string elementType = ElementTypeOf(pointer);
            yield return new WrapFixedPointer(elementType, $"pointer{pointerIndex}", pointer.Name);
            _ = lengthIndex;
        }
    }

    /// <summary>
    /// 数组对的「指针索引 → 长度索引」映射；同一指针只配对一次。
    /// </summary>
    private static Dictionary<int, int> ArrayPairs(List<ParameterInfo> parameters)
    {
        Dictionary<int, int> pairs = [];
        foreach ((int lengthIndex, int pointerIndex) in FindArrayPairs(parameters))
        {
            if (pairs.ContainsKey(pointerIndex))
                continue;

            pairs[pointerIndex] = lengthIndex;
        }

        return pairs;
    }

    private static string ElementTypeOf(ParameterInfo pointer)
        => ElementTypeOf(pointer.Mapped);

    private static string ElementTypeOf(MappedType mapped)
        => mapped.Type.EndsWith('*') ? mapped.Type[..^1] : mapped.Type;

    private static string SpanTypeName(string elementType, bool readOnly)
        => readOnly
            ? $"global::System.ReadOnlySpan<{elementType}>"
            : $"global::System.Span<{elementType}>";

    // ---- 常量 ----

    /// <summary>
    /// 分析常量声明并按类型前缀归属宿主；无法归属的归入 <c>NativeMethods</c>。
    /// </summary>
    private void AnalyzeConstants(IEnumerable<CXCursor> constants)
    {
        foreach (CXCursor cursor in constants)
        {
            if (!_constantNames.Add(cursor.Spelling.ToString()))
                continue;

            try
            {
                ConstantInfo info = AnalyzeConstant(cursor);
                _constants.Add(info);
                info.Host.Constants.Add(EmitConstant(info));
            }
            catch (NotSupportedException ex)
            {
                Console.Error.WriteLine($"跳过常量 {cursor.Spelling}：{ex.Message}");
            }
        }
    }

    private ConstantInfo AnalyzeConstant(CXCursor cursor)
    {
        string name = cursor.Spelling.ToString();
        if (name.Length is 0)
            throw new NotSupportedException("常量声明没有名称");

        HostInfo host = ResolveHost(name, out string remainder);
        string memberName = EscapeIdentifier((remainder.Length is 0 ? name : remainder).Pascalize());

        (string managedType, string literal) = cursor.kind is CXCursorKind.CXCursor_EnumConstantDecl
            ? EnumConstantValue(cursor)
            : EvaluateConstant(cursor);
        return new ConstantInfo(host, name, memberName, managedType, literal);
    }

    /// <summary>
    /// 枚举项不参与通用求值，改用枚举常量专用的原始值接口。
    /// </summary>
    private (string ManagedType, string Literal) EnumConstantValue(CXCursor cursor)
    {
        CXCursor enumDecl = cursor.SemanticParent;
        if (enumDecl.IsAnonymous && enumDecl.TypedefNameForAnonDecl.IsNull)
        {
            // 匿名且没有具名 typedef 时没有稳定的枚举类型可供引用
            throw new NotSupportedException("匿名枚举项没有对应的托管枚举类型");
        }

        CXType underlying = enumDecl.EnumDecl_IntegerType;
        bool unsigned = IsUnsignedKind(underlying.CanonicalType.kind);
        string enumName = EnsureEnumName(enumDecl);

        if (unsigned)
        {
            string text = cursor.EnumConstantDeclUnsignedValue.ToString(CultureInfo.InvariantCulture);
            return (enumName, $"({enumName})({text}u)");
        }

        string signedText = cursor.EnumConstantDeclValue.ToString(CultureInfo.InvariantCulture);
        return (enumName, $"({enumName})({signedText})");
    }

    /// <summary>
    /// 判断 <see cref="CXTypeKind"/> 是否为无符号整型；<see cref="CXType.IsUnsigned"/> 对其中的多数种类不可靠。
    /// </summary>
    private static bool IsUnsignedKind(CXTypeKind kind)
        => kind is CXTypeKind.CXType_UChar
            or CXTypeKind.CXType_UShort
            or CXTypeKind.CXType_UInt
            or CXTypeKind.CXType_ULong
            or CXTypeKind.CXType_ULongLong
            or CXTypeKind.CXType_Char_U
            or CXTypeKind.CXType_Char32
            or CXTypeKind.CXType_UInt128;

    /// <summary>
    /// 对普通常量执行编译期求值并转换为托管类型与字面量。
    /// </summary>
    private static (string ManagedType, string Literal) EvaluateConstant(CXCursor cursor)
    {
        CXEvalResult? maybeValue = cursor.Evaluate;
        if (maybeValue is null)
            throw new NotSupportedException("无法在编译期求值");

        CXEvalResult value = maybeValue.Value;
        CXType shape = UnwrapStatic(cursor.Type);
        switch (value.Kind)
        {
            case CXEvalResultKind.CXEval_Float:
                {
                    string text = value.AsDouble.ToString("R", CultureInfo.InvariantCulture);
                    if (!text.Contains('.') && !text.Contains('e') && !text.Contains('E'))
                        text += ".0";

                    return shape.kind is CXTypeKind.CXType_Float
                        ? ("float", text + "f")
                        : ("double", text);
                }

            case CXEvalResultKind.CXEval_StrLiteral:
            case CXEvalResultKind.CXEval_ObjCStrLiteral:
            case CXEvalResultKind.CXEval_CFStr:
                return ("string", ConstantStringLiteral(value.AsStr));

            case CXEvalResultKind.CXEval_Int:
                return ConstantIntegerValue(shape, value);

            default:
                throw new NotSupportedException($"常量值种类 {value.Kind} 无法表示为 C# const");
        }
    }

    /// <summary>
    /// 把整数常量求值结果映射为托管类型与常量字面量。
    /// </summary>
    /// <remarks>
    /// 值域由 <see cref="CXTypeKind"/> 与原生宽度决定；值放不进目标类型时用
    /// <c>unchecked((T)value)</c> 保留原生的截断语义，而不是生成无法编译的常量。
    /// </remarks>
    private static (string ManagedType, string Literal) ConstantIntegerValue(CXType shape, CXEvalResult value)
    {
        long signedValue = value.AsLongLong;
        ulong unsignedValue = value.AsUnsigned;

        (string ManagedType, string Literal) result = shape.kind switch
        {
            CXTypeKind.CXType_Bool => ("bool", signedValue is 0 ? "false" : "true"),
            CXTypeKind.CXType_Char_S => ("byte", SignedLiteral(signedValue, "byte")),
            CXTypeKind.CXType_Char_U or CXTypeKind.CXType_UChar => ("byte", UnsignedLiteral(unsignedValue, "byte")),
            CXTypeKind.CXType_SChar => ("sbyte", SignedLiteral(signedValue, "sbyte")),
            CXTypeKind.CXType_WChar or CXTypeKind.CXType_Char16 => ("char", UnsignedLiteral(unsignedValue, "char")),
            CXTypeKind.CXType_Char32 => ("uint", UnsignedLiteral(unsignedValue, "uint")),
            CXTypeKind.CXType_Short => ("short", SignedLiteral(signedValue, "short")),
            CXTypeKind.CXType_UShort => ("ushort", UnsignedLiteral(unsignedValue, "ushort")),
            CXTypeKind.CXType_Int => ("int", SignedLiteral(signedValue, "int")),
            CXTypeKind.CXType_UInt => ("uint", UnsignedLiteral(unsignedValue, "uint")),
            // long 的宽度随目标平台变化：Windows 为 4 字节，Linux/macOS 为 8 字节
            CXTypeKind.CXType_Long => shape.SizeOf is 4
                ? ("int", SignedLiteral(signedValue, "int"))
                : ("long", SignedLiteral(signedValue, "long")),
            CXTypeKind.CXType_ULong => shape.SizeOf is 4
                ? ("uint", UnsignedLiteral(unsignedValue, "uint"))
                : ("ulong", UnsignedLiteral(unsignedValue, "ulong")),
            CXTypeKind.CXType_LongLong => ("long", SignedLiteral(signedValue, "long")),
            CXTypeKind.CXType_ULongLong => ("ulong", UnsignedLiteral(unsignedValue, "ulong")),
            _ => throw new NotSupportedException($"常量类型 {shape.Spelling} 无法表示为 C# const"),
        };

        return result;
    }

    /// <summary>
    /// 生成有符号常量字面量；放不进目标类型时按原生截断语义加 <c>unchecked</c>。
    /// </summary>
    private static string SignedLiteral(long value, string managedType)
        => FitsSigned(value, managedType)
            ? $"({managedType}){value.ToString(CultureInfo.InvariantCulture)}"
            : $"unchecked(({managedType}){value.ToString(CultureInfo.InvariantCulture)})";

    /// <summary>
    /// 生成无符号常量字面量；放不进目标类型时按原生截断语义加 <c>unchecked</c>。
    /// </summary>
    private static string UnsignedLiteral(ulong value, string managedType)
        => FitsUnsigned(value, managedType)
            ? $"({managedType}){value.ToString(CultureInfo.InvariantCulture)}"
            : $"unchecked(({managedType}){value.ToString(CultureInfo.InvariantCulture)})";

    private static bool FitsSigned(long value, string managedType)
        => managedType switch
        {
            "sbyte" => value is >= sbyte.MinValue and <= sbyte.MaxValue,
            "byte" => value is >= byte.MinValue and <= byte.MaxValue,
            "short" => value is >= short.MinValue and <= short.MaxValue,
            "ushort" => value is >= ushort.MinValue and <= ushort.MaxValue,
            "int" => value is >= int.MinValue and <= int.MaxValue,
            "uint" => value is >= uint.MinValue and <= uint.MaxValue,
            "long" => true,
            "ulong" => value >= 0,
            _ => true,
        };

    private static bool FitsUnsigned(ulong value, string managedType)
        => managedType switch
        {
            "sbyte" => value <= (ulong)sbyte.MaxValue,
            "byte" => value <= byte.MaxValue,
            "short" => value <= (ulong)short.MaxValue,
            "ushort" => value <= ushort.MaxValue,
            "int" => value <= (ulong)int.MaxValue,
            "uint" => value <= uint.MaxValue,
            "long" => value <= (ulong)long.MaxValue,
            "ulong" => true,
            _ => true,
        };

    private static string ConstantStringLiteral(string value)
    {
        using StringWriter writer = CreateWriter();
        writer.Write('"');
        foreach (char character in value)
            writer.Write(EscapeChar(character));

        writer.Write('"');
        return writer.ToString();
    }

    private static string EscapeChar(char character)
        => character switch
        {
            '\\' => "\\\\",
            '"' => "\\\"",
            '\n' => "\\n",
            '\r' => "\\r",
            '\t' => "\\t",
            _ => character.ToString(),
        };

    private static string EmitConstant(ConstantInfo info)
    {
        using StringWriter writer = CreateWriter();
        writer.WriteLine("/// <summary>");
        writer.Write("/// 原生常量：<c>");
        writer.Write(XmlEscape(info.NativeName));
        writer.WriteLine("</c>。");
        writer.WriteLine("/// </summary>");
        writer.Write("public const ");
        writer.Write(info.ManagedType);
        writer.Write(' ');
        writer.Write(info.MemberName);
        writer.Write(" = ");
        writer.Write(info.Literal);
        writer.Write(';');
        return writer.ToString();
    }

    /// <summary>
    /// 不经实例状态剥语法糖，供静态常量分析使用。
    /// </summary>
    /// <summary>
    /// 常量取值只需要类型种类，此处直接复用通用的剥糖逻辑。
    /// </summary>
    private static CXType UnwrapStatic(CXType type) => Unwrap(type);

    private FunctionInfo AnalyzeFunction(CXCursor cursor)
    {
        string name = cursor.Spelling.ToString();
        if (name.Length is 0)
            throw new NotSupportedException("函数声明没有名称");

        _currentFunction = name;
        try
        {
            HostInfo? handleHost = ResolveHandleHost(name);
            HostInfo host;
            string remainder;
            bool isCreate = false;
            if (handleHost is null)
            {
                host = ResolveHost(name, out remainder);
            }
            else
            {
                host = handleHost;
                remainder = name;
                isCreate = _createFunctions.TryGetValue(handleHost.Key, out string? createName) && createName == name;
            }

            CXCursor function = cursor;
            FunctionInfo info = new(function, name, host, remainder, handleHost is not null)
            {
                CallConv = function.Type.FunctionTypeCallingConv switch
                {
                    CXCallingConv.CXCallingConv_X86StdCall => "CallConvStdcall",
                    CXCallingConv.CXCallingConv_X86FastCall => "CallConvFastcall",
                    CXCallingConv.CXCallingConv_X86ThisCall => "CallConvThiscall",
                    _ => "CallConvCdecl",
                },
                IsVariadic = function.Type.IsFunctionTypeVariadic,
                IsCreateAssociate = isCreate,
            };

            MappedType returnType = MapType(function.ResultType, TypeUsage.Return);
            (string returnComment, string[] returnAttributes) = DescribeNativeType(returnType, function.ResultType);
            info.Return = returnType;
            info.ReturnComment = returnComment;
            info.ReturnAttributes = returnAttributes;
            info.SelfIndex = FindSelfIndex(function, host);

            if (info.SelfIndex >= 0)
            {
                CXCursor self = function.GetArgument((uint)info.SelfIndex);
                // 结构体 self 需要原生指针（fixed 取地址），句柄 self 使用 SafeHandle 封送
                TypeUsage selfUsage = host.Kind is HostKind.Struct ? TypeUsage.Nested : TypeUsage.Parameter;
                MappedType selfMapped = MapType(self.Type, selfUsage);
                string selfName = self.Spelling.ToString() is { Length: > 0 } spelling ? EscapeIdentifier(spelling) : "self";
                info.Self = new(selfName, selfMapped, "", [.. selfMapped.Attributes], self.Type);
            }

            for (int i = 0; i < function.NumArguments; i++)
            {
                if (i == info.SelfIndex)
                    continue;

                CXCursor argument = function.GetArgument((uint)i);
                string argumentName = argument.Spelling.ToString() is { Length: > 0 } spelling
                    ? EscapeIdentifier(spelling)
                    : $"arg{i}";
                MappedType mapped = MapType(argument.Type, TypeUsage.Parameter);
                (string comment, string[] attributes) = DescribeNativeType(mapped, argument.Type);
                info.Parameters.Add(new ParameterInfo(argumentName, mapped, comment, attributes, argument.Type));
            }

            info.Role = DetermineRole(info);
            info.PropertyName = info.Role is FunctionRole.Method
                ? null
                : TryGetPropertyName(name, host, remainder);
            info.ManagedName = ManagedMemberName(info);

            return info;
        }
        finally
        {
            _currentFunction = null;
        }
    }

    /// <summary>
    /// 句柄的创建/释放函数必须归属其句柄类型，否则托管封装无法访问 <c>private</c> 的导入方法。
    /// </summary>
    private HostInfo? ResolveHandleHost(string functionName)
    {
        foreach ((uint key, string create) in _createFunctions)
        {
            if (create == functionName)
                return _handleHosts.GetValueOrDefault(key);
        }

        foreach ((uint key, string release) in _releaseFunctions)
        {
            if (release == functionName)
                return _handleHosts.GetValueOrDefault(key);
        }

        return null;
    }

    private static int FindSelfIndex(CXCursor cursor, HostInfo host)
    {
        if (host.Kind is not (HostKind.Struct or HostKind.Handle) || cursor.NumArguments is 0)
            return -1;

        CXType canonical = cursor.GetArgument(0).Type.CanonicalType;
        if (canonical.kind is not CXTypeKind.CXType_Pointer)
            return -1;

        CXType pointee = canonical.PointeeType.CanonicalType;
        if (pointee.kind is not CXTypeKind.CXType_Record)
            return -1;

        return KeyOf(pointee.Declaration) == host.Key ? 0 : -1;
    }

    /// <summary>
    /// 角色判定只看类型签名：返回 <c>void</c> 且只有一个值类型参数为 setter；
    /// 非 <c>void</c> 且返回具名类型、除 self 外无参数为 getter。
    /// </summary>
    private static FunctionRole DetermineRole(FunctionInfo info)
    {
        if (info.Return.Type is "void")
        {
            if (info.Parameters is [{ Mapped.Type: var valueType }] && IsValueType(valueType))
                return FunctionRole.Setter;

            return FunctionRole.Method;
        }

        return info.Parameters.Count is 0 && IsValueType(info.Return.Type)
            ? FunctionRole.Getter
            : FunctionRole.Method;
    }

    /// <summary>
    /// 值类型：<c>void</c>、指针、引用、数组、函数指针以外的具名类型；
    /// 可空引用（<c>Xxx?</c>，例如句柄、字符串）属于引用而不是值类型。
    /// </summary>
    private static bool IsValueType(string mappedType)
        => mappedType is not "void" and not "nint"
            && !mappedType.EndsWith('*')
            && !mappedType.EndsWith('?')
            && !mappedType.StartsWith("delegate*", StringComparison.Ordinal);

    /// <summary>
    /// 属性名：去掉宿主类型前缀与 <c>set_</c>/<c>get_</c> 修饰词后按 Pascalize 处理；结果不是合法标识符时放弃映射。
    /// </summary>
    private static string? TryGetPropertyName(string functionName, HostInfo host, string remainder)
    {
        string name = host.Kind is HostKind.NativeMethods ? functionName : remainder;

        if (name.StartsWith("set_", StringComparison.OrdinalIgnoreCase))
            name = name[4..];
        else if (name.StartsWith("get_", StringComparison.OrdinalIgnoreCase))
            name = name[4..];

        if (name.Length is 0)
            return null;

        string pascalized = name.Pascalize();
        return IsIdentifier(pascalized) ? pascalized : null;
    }

    private static string ManagedMemberName(FunctionInfo info)
    {
        string source = info.Host.Kind is HostKind.NativeMethods || info.Remainder.Length is 0
            ? info.NativeName
            : info.Remainder;
        return source.Pascalize();
    }

    private string AllocateMemberName(HostInfo host, string candidate)
    {
        string name = EscapeIdentifier(candidate);
        while (!_usedMemberNames.Add((host.Name, name)))
            name += "_";

        return name;
    }

    // ---- 属性配对 ----

    private void AddAccessor(FunctionInfo info)
    {
        (HostInfo Host, string Candidate, bool IsInstance) key = (info.Host, info.PropertyName!, info.IsInstance);
        if (!_propertyLookup.TryGetValue(key, out List<PropertyDraft>? drafts))
        {
            drafts = [];
            _propertyLookup.Add(key, drafts);
        }

        foreach (PropertyDraft existing in drafts)
        {
            if (!CanAttach(existing, info))
                continue;

            Attach(existing, info);
            return;
        }

        string name = AllocateMemberName(info.Host, info.PropertyName!);
        PropertyDraft draft = new(info.Host, name, info.IsInstance);
        drafts.Add(draft);
        _propertyDrafts.Add(draft);
        Attach(draft, info);
    }

    private static bool CanAttach(PropertyDraft draft, FunctionInfo info)
        => info.Role switch
        {
            FunctionRole.Getter => draft.Getter is null
                && (draft.Setter is null || draft.Setter.Parameters[0].Mapped.Type == info.Return.Type),
            FunctionRole.Setter => draft.Setter is null
                && (draft.Getter is null || draft.Getter.Return.Type == info.Parameters[0].Mapped.Type),
            _ => false,
        };

    private static void Attach(PropertyDraft draft, FunctionInfo info)
    {
        if (info.Role is FunctionRole.Getter)
        {
            draft.Getter = info;
            return;
        }

        draft.Setter = info;
    }

    private void EmitProperties()
    {
        foreach (PropertyDraft draft in _propertyDrafts
                     .OrderBy(draft => draft.Host.Name, StringComparer.Ordinal)
                     .ThenBy(draft => draft.Name, StringComparer.Ordinal))
        {
            draft.Host.Members.Add(CSharpWrapGenerator.EmitProperty(BuildWrapProperty(draft)));
        }
    }

    /// <summary>
    /// 把属性草稿转换为封装生成器的输入；访问器复用方法封装逻辑，保持与普通方法一致的 Span 处理。
    /// </summary>
    private static WrapProperty BuildWrapProperty(PropertyDraft draft)
    {
        WrapFunction? getter = null;
        WrapFunction? setter = null;
        if (draft.Getter is { } getterInfo)
            getter = BuildWrapFunction(getterInfo);

        if (draft.Setter is { } setterInfo)
            setter = BuildWrapFunction(setterInfo);

        return new WrapProperty
        {
            Name = draft.Name,
            Type = draft.Type,
            IsStatic = draft.IsInstance is false,
            Getter = getter,
            Setter = setter,
        };
    }

    // ---- 阶段 2：Native 导入 ----

    /// <summary>
    /// 把带参数的 <c>create</c> 函数生成为托管构造函数重载；构造函数直接接管句柄所有权。
    /// </summary>
    /// <remarks>
    /// 无参 <c>create</c> 已由 SafeHandle 的默认构造函数覆盖，因此这里只处理带参数的情形，
    /// 否则调用方无法按给定参数创建实例。
    /// </remarks>
    private static string BuildHandleConstructor(FunctionInfo info)
    {
        using StringWriter writer = CreateWriter();
        writer.Write("public ");
        writer.Write(info.Host.Name);
        writer.Write('(');

        List<string> parameters = [];
        foreach (ParameterInfo parameter in info.Parameters)
            parameters.Add($"{parameter.Mapped.Type} {parameter.Name}");

        writer.Write(string.Join(", ", parameters));
        writer.Write(") : this((nint)");
        writer.Write(info.ImportName);
        writer.Write('(');
        writer.Write(string.Join(", ", info.Parameters.Select(parameter => parameter.Name)));
        writer.Write("), true)");
        writer.WriteLine();
        writer.WriteLine("{");
        writer.Write("}");
        return writer.ToString();
    }

    private void EmitImport(FunctionInfo info)
    {
        // 导入方法沿用原生函数名，便于在托管封装中直接对照原生原型
        info.ImportName = EscapeIdentifier(info.NativeName);

        using StringWriter source = CreateWriter();
        source.WriteLine("/// <summary>");
        source.Write("/// 原生声明：<c>");
        source.Write(XmlEscape(NativeSignature(info.Cursor, info.NativeName, info.IsVariadic)));
        source.WriteLine("</c>");
        source.WriteLine("/// </summary>");
        if (info.IsVariadic)
            source.WriteLine("/// <remarks>变参函数只绑定 <c>...</c> 之前的固定参数。</remarks>");

        source.Write("[LibraryImport(\"");
        source.Write(Escape(LibraryOf(info.NativeName)));
        source.Write("\", EntryPoint = \"");
        source.Write(Escape(info.NativeName));
        source.WriteLine("\")]");
        source.Write("[UnmanagedCallConv(CallConvs = [typeof(");
        source.Write(info.CallConv);
        source.WriteLine(")])]");
        if (info.ReturnAttributes.Length is not 0)
        {
            source.Write("[return: ");
            source.Write(string.Join(", ", info.ReturnAttributes));
            source.WriteLine("]");
        }

        source.Write(options.NativeMethodAccessibility);
        source.Write(" static partial ");
        source.Write(info.ReturnComment);
        source.Write(info.Return.Type);
        source.Write(' ');
        source.Write(info.ImportName);
        source.Write('(');
        source.Write(string.Join(", ", ImportParameters(info)));
        source.Write(");");

        info.Host.Imports.Add(source.ToString());
    }

    private static string[] ImportParameters(FunctionInfo info)
    {
        List<string> parts = [];
        if (info.Self is { } self)
            parts.Add(FormatParameter(self));

        foreach (ParameterInfo parameter in info.Parameters)
            parts.Add(FormatParameter(parameter));

        return [.. parts];
    }

    private static string FormatParameter(ParameterInfo parameter)
        => FormatParameter(parameter, includeAttributes: true, includeNativeComment: true);

    /// <summary>
    /// 生成参数文本；封送特性只属于阶段 2 的原生签名，托管封装签名不再重复携带。
    /// </summary>
    private static string FormatParameter(ParameterInfo parameter, bool includeAttributes, bool includeNativeComment)
    {
        using StringWriter writer = CreateWriter();
        if (includeAttributes)
        {
            foreach (string attribute in parameter.Attributes)
            {
                writer.Write('[');
                writer.Write(attribute);
                writer.Write("] ");
            }
        }

        if (includeNativeComment)
            writer.Write(parameter.Comment);

        writer.Write(parameter.Mapped.Type);
        writer.Write(' ');
        writer.Write(parameter.Name);
        return writer.ToString();
    }

    /// <summary>
    /// 生成参数/返回值的附加特性；原生拼写丢失时补充 NativeTypeName 特性或注释。
    /// </summary>
    private (string Comment, string[] Attributes) DescribeNativeType(MappedType mapped, CXType nativeType)
    {
        List<string> attributes = [.. mapped.Attributes];
        string comment = "";
        if (!ShouldAnnotate(nativeType))
            return (comment, [.. attributes]);

        string spelling = nativeType.Spelling.ToString();
        if (options.NativeTypeNameAttributeName is { Length: > 0 } attributeName)
            attributes.Add($"{attributeName}(\"{Escape(spelling)}\")");
        else
            comment = $"/* {spelling} */ ";

        return (comment, [.. attributes]);
    }

    /// <summary>
    /// 只有映射会丢失语法信息（typedef、指针、数组、函数指针、限定符）时才标注原生类型。
    /// </summary>
    private static bool ShouldAnnotate(CXType type)
    {
        if (type.IsConstQualified || type.IsVolatileQualified)
            return true;

        return type.kind is CXTypeKind.CXType_Typedef
            or CXTypeKind.CXType_Pointer
            or CXTypeKind.CXType_Elaborated
            or CXTypeKind.CXType_Attributed
            or CXTypeKind.CXType_ConstantArray
            or CXTypeKind.CXType_IncompleteArray
            or CXTypeKind.CXType_FunctionProto
            or CXTypeKind.CXType_FunctionNoProto;
    }

    private static string NativeSignature(CXCursor function, string name, bool variadic)
    {
        List<string> parameters = [with(function.NumArguments + (variadic ? 1 : 0))];
        for (int i = 0; i < function.NumArguments; i++)
            parameters.Add(function.GetArgument((uint)i).Type.Spelling.ToString());
        if (variadic)
            parameters.Add("...");

        string parameterList = parameters.Count is 0 ? "void" : string.Join(", ", parameters);
        return $"{function.ResultType.Spelling} {name}({parameterList})";
    }

    // ---- 输出 ----

    private void WriteOutputs(int functionCount)
    {
        string header = Header();
        CleanOutputDirectory(options.TypeDefineOutput);
        CleanOutputDirectory(options.NativeMethodOutput);
        CleanOutputDirectory(options.ConstantOutput);
        _wrap!.CleanOutput();

        // 阶段 1：类型定义
        foreach ((string name, string source) in _typeSources.OrderBy(entry => entry.Key, StringComparer.Ordinal))
            WriteFile(options.TypeDefineOutput, name + ".g.d.cs", header + Normalize(source) + "\r\n");

        // 阶段 2 写导入，阶段 3 交由 CSharpWrapGenerator 写托管封装，常量另起文件
        foreach (HostInfo host in EnumerateHosts())
        {
            WriteParts(options.NativeMethodOutput, host, ".g.n.", header, host.Imports);
            WriteConstantParts(host, header);

            if (host.Members.Count is 0)
                continue;

            _wrap.WriteHostFiles(host.Name, WrapKindOf(host), host.Members);
        }

        Console.WriteLine($"生成了 {_functions.Count} 个函数、{_typeSources.Count} 个类型的 C# 绑定。");
        if (functionCount > _functions.Count)
            Console.Error.WriteLine($"跳过了 {functionCount - _functions.Count} 个无法映射的函数。");
    }

    private static WrapHostKind WrapKindOf(HostInfo host)
        => host.Kind switch
        {
            HostKind.Struct => WrapHostKind.Struct,
            HostKind.NativeMethods => WrapHostKind.NativeMethods,
            _ => WrapHostKind.Handle,
        };

    /// <summary>
    /// 常量单独写入 <c>类型名.g.c.{序号}.cs</c>；默认宿主 <c>NativeMethods</c> 的常量同样落在其自己的文件里。
    /// </summary>
    private void WriteConstantParts(HostInfo host, string header)
    {
        if (host.Constants.Count is 0)
            return;

        WriteParts(options.ConstantOutput, host, ".g.c.", header, host.Constants);
    }

    private IEnumerable<HostInfo> EnumerateHosts()
    {
        foreach (HostInfo host in _hosts.OrderBy(host => host.Name, StringComparer.Ordinal))
            yield return host;

        if (_nativeMethodsHost is not null)
            yield return _nativeMethodsHost;
    }

    private void WriteParts(
        DirectoryInfo directory,
        HostInfo host,
        string suffix,
        string header,
        List<string> members)
    {
        if (members.Count is 0)
            return;

        int part = 0;
        for (int offset = 0; offset < members.Count; offset += options.MethodsPerFile)
        {
            part++;
            using StringWriter file = CreateWriter();
            file.Write(header);
            file.WriteLine(PartialDeclaration(host));
            file.WriteLine("{");

            for (int i = offset; i < Math.Min(offset + options.MethodsPerFile, members.Count); i++)
            {
                if (i > offset)
                    file.WriteLine();

                WriteIndented(file, members[i], 4);
            }

            file.WriteLine();
            file.Write("}");
            WriteFile(directory, $"{host.Name}{suffix}{part}.cs", file.ToString());
        }
    }

    private static void WriteIndented(StringWriter writer, string source, int spaces)
    {
        string padding = new(' ', spaces);
        foreach (string line in Normalize(source).ReplaceLineEndings("\n").Split('\n'))
            writer.WriteLine(line.Length is 0 ? line : padding + line);
    }

    private static string PartialDeclaration(HostInfo host)
        => host.Kind switch
        {
            HostKind.Struct => $"public unsafe partial struct {host.Name}",
            HostKind.NativeMethods => $"public static unsafe partial class {host.Name}",
            _ => $"public unsafe partial class {host.Name}",
        };

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

    private static void CleanOutputDirectory(DirectoryInfo directory)
    {
        Directory.CreateDirectory(directory.FullName);
        foreach (FileInfo file in directory.EnumerateFiles("*.cs", SearchOption.TopDirectoryOnly))
        {
            if (file.Name.Contains(".g.", StringComparison.Ordinal))
                file.Delete();
        }
    }

    /// <summary>
    /// 用文件流写入生成结果，避免整体读入内存后再落盘。
    /// </summary>
    private static void WriteFile(DirectoryInfo directory, string fileName, string content)
    {
        using FileStream stream = new(
            Path.Combine(directory.FullName, fileName),
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

    private static string Escape(string value)
        => value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal);

    private static string XmlEscape(string value)
        => value.Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal);

    // ---- 数据模型 ----

    /// <summary>
    /// 映射后的 C# 类型：类型文本与附加特性（不含方括号）。
    /// </summary>
    private readonly record struct MappedType(string Type, string[] Attributes, string NativeSpelling);

    /// <summary>
    /// 导入方法参数：托管名、映射结果、原生类型标注与原生类型本身。
    /// </summary>
    private sealed record ParameterInfo(string Name, MappedType Mapped, string Comment, string[] Attributes, CXType NativeType);

    /// <summary>
    /// 宿主类型形态，决定阶段 1 与阶段 3 使用的声明关键字。
    /// </summary>
    private enum HostKind
    {
        /// <summary>
        /// 有完整定义的记录类型，生成 <c>partial struct</c>。
        /// </summary>
        Struct,

        /// <summary>
        /// 只有前向声明的记录类型，生成 <c>SafeHandle</c> 派生的 <c>partial class</c>。
        /// </summary>
        Handle,

        /// <summary>
        /// 枚举，不能承载成员。
        /// </summary>
        Enum,

        /// <summary>
        /// 无法归类函数的默认宿主，生成静态 <c>partial class</c>。
        /// </summary>
        NativeMethods,
    }

    /// <summary>
    /// 宿主类型：承载阶段 2 导入方法与阶段 3 托管封装的托管类型。
    /// </summary>
    private sealed class HostInfo(string name, uint key, HostKind kind, string nativeSpelling)
    {
        public string Name { get; } = name;
        public uint Key { get; } = key;
        public HostKind Kind { get; } = kind;
        public string NativeSpelling { get; } = nativeSpelling;
        public List<string> Imports { get; } = [];
        public List<string> Members { get; } = [];
        public List<string> Constants { get; } = [];
    }

    /// <summary>
    /// 一个已分析的常量：原生名、托管成员名、托管类型与字面量。
    /// </summary>
    private sealed record ConstantInfo(HostInfo Host, string NativeName, string MemberName, string ManagedType, string Literal);

    /// <summary>
    /// 生成成员的角色：属性访问器或普通方法。
    /// </summary>
    private enum FunctionRole
    {
        Method,
        Getter,
        Setter,
    }

    /// <summary>
    /// 一个已分析的导出函数；阶段 2 与阶段 3 复用同一份映射结果，避免两次映射不一致。
    /// </summary>
    private sealed class FunctionInfo(CXCursor cursor, string nativeName, HostInfo host, string remainder, bool isHandleAssociate)
    {
        public CXCursor Cursor { get; } = cursor;
        public string NativeName { get; } = nativeName;
        public HostInfo Host { get; } = host;
        public string Remainder { get; } = remainder;
        public bool IsHandleAssociate { get; } = isHandleAssociate;

        /// <summary>
        /// 是否为句柄的 <c>create</c> 函数（相对 <c>release</c> 而言）。
        /// </summary>
        public bool IsCreateAssociate { get; set; }
        public MappedType Return { get; set; }
        public string ReturnComment { get; set; } = "";
        public string[] ReturnAttributes { get; set; } = [];
        public List<ParameterInfo> Parameters { get; } = [];
        public int SelfIndex { get; set; } = -1;
        public ParameterInfo? Self { get; set; }
        public bool IsVariadic { get; set; }
        public string CallConv { get; set; } = "CallConvCdecl";
        public FunctionRole Role { get; set; }
        public string? PropertyName { get; set; }
        public string ManagedName { get; set; } = "";
        public string MemberName { get; set; } = "";
        public string ImportName { get; set; } = "";

        public bool IsInstance => SelfIndex >= 0;

        public bool IsStatic => SelfIndex < 0;

        public bool IsStructInstance => IsInstance && Host.Kind is HostKind.Struct;
    }

    /// <summary>
    /// 属性草稿：getter 与 setter 逐条累积，配对完成后再统一生成属性文本。
    /// </summary>
    private sealed class PropertyDraft(HostInfo host, string name, bool isInstance)
    {
        public HostInfo Host { get; } = host;
        public string Name { get; } = name;
        public bool IsInstance { get; } = isInstance;
        public FunctionInfo? Getter { get; set; }
        public FunctionInfo? Setter { get; set; }

        public string Type => Getter?.Return.Type ?? Setter!.Parameters[0].Mapped.Type;
    }

    /// <summary>
    /// 类型生成状态；失败时记录原因，便于跳过依赖该类型的函数。
    /// </summary>
    private sealed class TypeState(string name)
    {
        public string Name { get; } = name;

        public string? Error { get; set; }
    }

    /// <summary>
    /// 类型出现的位置，决定字符串、句柄等类型能否使用托管封送。
    /// </summary>
    private enum TypeUsage
    {
        /// <summary>
        /// 函数参数，允许字符串与非托管句柄封送。
        /// </summary>
        Parameter,

        /// <summary>
        /// 函数返回值，允许字符串与非托管句柄封送。
        /// </summary>
        Return,

        /// <summary>
        /// 结构体字段，只能是 blittable 表示。
        /// </summary>
        Field,

        /// <summary>
        /// 指针目标或函数指针签名内部，只能是 blittable 表示。
        /// </summary>
        Nested,
    }
}
