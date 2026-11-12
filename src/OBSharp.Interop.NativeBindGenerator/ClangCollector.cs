
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;

using ClangSharp.Interop;

namespace OBSharp.Interop.NativeBindGenerator;

public closed class ClangCollector
{
    /// <summary>
    /// 收集函数声明
    /// </summary>
    /// <param name="translationUnit"></param>
    /// <param name="cMode">是否仅收集 C 导出函数</param>
    /// <returns></returns>
    protected static unsafe IReadOnlyList<CXCursor> CollectFunctions(CXTranslationUnit translationUnit, bool cMode)
    {
        VisitorContext context = new([]);
        GCHandle handle = GCHandle.Alloc(context);

        try
        {
            CXClientData clientData = new((nint)handle);

            translationUnit.Cursor.VisitChildren(
                cMode
                    ? &CExportFunctionVisitor
                    : &CppFunctionVisitor,
                new CXClientData((nint)handle));

            if (context.Exception is not null)
                ExceptionDispatchInfo.Capture(context.Exception).Throw();
        }
        finally
        {
            handle.Free();
        }

        return context.Results.AsReadOnly();
    }

    /// <summary>
    /// 收集函数签名中的类型，并递归展开记录字段、数组元素、指针 pointee 与函数类型参数等依赖类型。
    /// </summary>
    /// <param name="cursors">函数声明列表</param>
    /// <returns>去重后的类型列表，按发现顺序排列</returns>
    protected static IEnumerable<CXType> CollectTypes(IEnumerable<CXCursor> cursors)
    {
        // 结果按发现顺序记录，另用哈希集合判重：类型名与匿名类型序号都按遍历顺序分配，
        // 显式保留发现顺序才能让同名冲突后缀（Type、_handle、anonymous_N）在多次运行间保持一致
        List<CXType> results = [];
        HashSet<CXType> seen = [];
        HashSet<uint> visitedRecords = [];
        HashSet<CXType> visitedFunctions = [];

        foreach (CXCursor cursor in cursors.Where(IsAnyFunction))
        {
            if (cursor is { kind: CXCursorKind.CXCursor_Constructor or CXCursorKind.CXCursor_Destructor })
                Collect(cursor.SemanticParent.Type);
            else
                Collect(cursor.ResultType);

            int argumentCount = cursor.NumArguments;
            for (int i = 0; i < argumentCount; i++)
                Collect(cursor.GetArgument((uint)i).Type);
        }

        return results;

        void Collect(CXType type)
        {
            CXType shape = Peel(type);
            if (shape.kind is CXTypeKind.CXType_Invalid)
                return;

            // 已收集过的类型不再重复展开，避免同一类型重复递归
            if (!seen.Add(shape))
                return;

            results.Add(shape);

            // 指针类（含块指针、成员指针、Objective-C 对象指针）的 pointee 有效，其余种类返回无效类型
            CXType pointee = shape.PointeeType;
            if (pointee.kind is not CXTypeKind.CXType_Invalid)
            {
                Collect(pointee);
                return;
            }

            switch (shape.kind)
            {
                case CXTypeKind.CXType_ConstantArray:
                case CXTypeKind.CXType_IncompleteArray:
                case CXTypeKind.CXType_VariableArray:
                    Collect(shape.ArrayElementType);
                    break;

                case CXTypeKind.CXType_FunctionProto:
                case CXTypeKind.CXType_FunctionNoProto:
                    {
                        // 递归终止已由 seen 的判重保证；这里额外按规范类型判重，
                        // 用于合并同一签名的等价描述符（例如不同的 typedef 写法），避免重复展开
                        CXType canonical = shape.CanonicalType;
                        if (!visitedFunctions.Add(canonical))
                            break;

                        Collect(shape.ResultType);

                        int argumentCount = shape.NumArgTypes;
                        for (int i = 0; i < argumentCount; i++)
                            Collect(shape.GetArgType((uint)i));

                        break;
                    }

                case CXTypeKind.CXType_Record:
                    {
                        CXCursor declaration = shape.Declaration;
                        CXCursor definition = declaration.Definition;
                        if (definition.IsNull)
                            break;

                        // 记录字段按规范声明只展开一次，避免 struct Node { Node* next; } 这类自引用无限递归；
                        // 同时合并同一记录的等价描述符，使字段不会重复遍历
                        if (!visitedRecords.Add(declaration.CanonicalCursor.Hash))
                            break;

                        int fieldCount = definition.NumFields;
                        for (int i = 0; i < fieldCount; i++)
                            Collect(definition.GetField((uint)i).Type);

                        break;
                    }
            }
        }
    }

    private static CXType Peel(CXType type)
    {
        while (true)
        {
            switch (type.kind)
            {
                case CXTypeKind.CXType_Elaborated:
                    type = type.NamedType;
                    break;
                case CXTypeKind.CXType_Attributed:
                    type = type.ModifiedType;
                    break;
                case CXTypeKind.CXType_Typedef:
                    {
                        CXCursor declaration = type.Declaration;
                        type = declaration.IsNull
                            ? type.CanonicalType
                            : declaration.TypedefDeclUnderlyingType;
                        break;
                    }
                case CXTypeKind.CXType_Unexposed:
                case (CXTypeKind)177: // CXType_Paren
                    {
                        // 规范类型仍可能是同类描述符，取到自身时停止，避免反复 canonical 造成死循环
                        CXType canonical = type.CanonicalType;
                        if (canonical == type)
                            return type;

                        type = canonical;
                        break;
                    }
                default:
                    return type;
            }
        }
    }

    /// <summary>
    /// 收集顶层常量声明，包括 <c>const</c> 限定的变量与具名枚举项。
    /// </summary>
    /// <remarks>
    /// 只接受来自 <see cref="SourceFiles"/> 的声明：shim 头文件会 include 标准库与转换头，
    /// 其常量（<c>nothrow</c>、<c>_MM_BROADCAST*</c> 等）数量可达上千个，全部当作候选会让后续类型解析失去意义。
    /// 未设置 <see cref="SourceFiles"/> 时不做来源过滤。
    /// </remarks>
    /// <param name="translationUnit">已构建的翻译单元</param>
    /// <returns>常量声明列表</returns>
    protected static unsafe IReadOnlyList<CXCursor> CollectConstants(CXTranslationUnit translationUnit)
    {
        VisitorContext context = new([]);
        GCHandle handle = GCHandle.Alloc(context);

        try
        {
            translationUnit.Cursor.VisitChildren(&ConstantVisitor, new CXClientData((nint)handle));

            if (context.Exception is not null)
                ExceptionDispatchInfo.Capture(context.Exception).Throw();
        }
        finally
        {
            handle.Free();
        }

        return context.Results.AsReadOnly();
    }

    /// <summary>
    /// 允许收集声明来源的文件；为空表示不做来源过滤。
    /// </summary>
    /// <remarks>
    /// 由调用方在解析前设置一次，避免标准库与被包含的实现头里的常量进入导出列表。
    /// </remarks>
    internal static IReadOnlyList<string>? SourceFiles { get; set; }

    /// <summary>
    /// 声明是否来自 <see cref="SourceFiles"/>。
    /// </summary>
    private static bool IsFromSourceFile(CXCursor cursor)
    {
        if (SourceFiles is not { Count: > 0 } files)
            return true;

        cursor.Location.GetExpansionLocation(out CXFile file, out _, out _, out _);
        string path = file.Name.ToString();
        if (path.Length is 0)
            return false;

        string fullPath = Path.GetFullPath(path);
        foreach (string source in files)
        {
            if (string.Equals(fullPath, source, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static unsafe CXChildVisitResult ConstantVisitor(CXCursor cursor, CXCursor parent, void* clientData)
    {
        if (GCHandle.FromIntPtr((nint)clientData).Target is not VisitorContext context)
            return CXChildVisitResult.CXChildVisit_Break;

        try
        {
            if (IsConstantDeclaration(cursor) && IsFromSourceFile(cursor))
                context.Results.Add(cursor);

            return CXChildVisitResult.CXChildVisit_Recurse;
        }
        catch (Exception ex)
        {
            context.Exception = ex;
            return CXChildVisitResult.CXChildVisit_Break;
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static unsafe CXChildVisitResult CExportFunctionVisitor(CXCursor cursor, CXCursor parent, void* clientData)
    {
        if (GCHandle.FromIntPtr((nint)clientData).Target is not VisitorContext context)
            return CXChildVisitResult.CXChildVisit_Break;

        try
        {
            // 只收集真正带导出标记的声明：__declspec(dllexport) 与 __attribute__((visibility("default")))。
            // 没有导出标记的函数不会进入 DLL 导出表，若为它生成绑定，运行时必然抛 EntryPointNotFoundException。
            if (cursor.kind is CXCursorKind.CXCursor_DLLExport)
            {
                if (IsExternCFunction(parent) && IsAcceptedName(parent))
                    context.Results.Add(parent);
            }
            else if (cursor.kind is CXCursorKind.CXCursor_VisibilityAttr && parent.Visibility is CXVisibilityKind.CXVisibility_Default)
            {
                if (IsExternCFunction(parent) && IsAcceptedName(parent))
                    context.Results.Add(parent);
            }

            return CXChildVisitResult.CXChildVisit_Recurse;
        }
        catch (Exception ex)
        {
            context.Exception = ex;
            return CXChildVisitResult.CXChildVisit_Break;
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static unsafe CXChildVisitResult CppFunctionVisitor(CXCursor cursor, CXCursor parent, void* clientData)
    {
        if (GCHandle.FromIntPtr((nint)clientData).Target is not VisitorContext context)
            return CXChildVisitResult.CXChildVisit_Break;

        try
        {
            if (IsAnyFunction(cursor) && IsAccessibleFunction(cursor))
                context.Results.Add(cursor);

            return CXChildVisitResult.CXChildVisit_Recurse;
        }
        catch (Exception ex)
        {
            context.Exception = ex;
            return CXChildVisitResult.CXChildVisit_Break;
        }
    }

    private static bool IsExternCFunction(CXCursor cursor) => cursor is not
    {
        Language: not CXLanguageKind.CXLanguage_C,
        IsExternC: false,
    } && cursor is
    {
        kind: CXCursorKind.CXCursor_FunctionDecl,
        Linkage: not CXLinkageKind.CXLinkage_Internal,
    };

    private static bool IsAccessibleFunction(CXCursor cursor) => cursor is
    {
        Linkage: not CXLinkageKind.CXLinkage_Internal,
        CXXAccessSpecifier: CX_CXXAccessSpecifier.CX_CXXInvalidAccessSpecifier or CX_CXXAccessSpecifier.CX_CXXPublic,
    };

    /// <summary>
    /// 收集 C 导出函数时可接受的名字前缀；为空表示接受全部 <c>extern "C"</c> 声明。
    /// </summary>
    /// <remarks>
    /// <see cref="CppShimGenerator"/> 生成的 shim 头文件会把转换工具头文件一并包含进来，
    /// 其内部的 <c>extern "C"</c> 函数（<c>CoTaskMemAlloc</c>、<c>ToC</c> 等）不是导出目标，
    /// 因此解析 shim 时把该前缀设为 shim 的导出前缀，只接受前缀命名的符号。
    /// </remarks>
    internal static string? ExportNamePrefix { get; set; }

    private static bool IsAnyFunction(CXCursor cursor) => cursor is
    {
        kind: CXCursorKind.CXCursor_FunctionDecl
            or CXCursorKind.CXCursor_CXXMethod
            or CXCursorKind.CXCursor_Constructor
            or CXCursorKind.CXCursor_Destructor
            or CXCursorKind.CXCursor_ConversionFunction,
    };

    /// <summary>
    /// 函数名是否符合当前限定的导出前缀。
    /// </summary>
    private static bool IsAcceptedName(CXCursor cursor)
        => ExportNamePrefix is not { Length: > 0 } prefix
            || cursor.Spelling.ToString().StartsWith(prefix, StringComparison.Ordinal);

    /// <summary>
    /// 顶层的可取值常量：<c>const</c> 限定的变量声明，以及枚举项。
    /// </summary>
    /// <remarks>
    /// 内部链接（<c>static const</c>）的常量在此处同样会被收集，其可用性由调用方按语言规则过滤。
    /// 枚举项是否可用（例如匿名枚举没有托管类型可引用）也由调用方判定。
    /// </remarks>
    private static bool IsConstantDeclaration(CXCursor cursor)
        => cursor.kind switch
        {
            CXCursorKind.CXCursor_VarDecl => cursor.Type.IsConstQualified,
            CXCursorKind.CXCursor_EnumConstantDecl => cursor.Spelling.ToString().Length is not 0,
            _ => false,
        };
}

file sealed class VisitorContext(List<CXCursor> results)
{
    public List<CXCursor> Results { get; } = results;

    public Exception? Exception { get; set; }
}
