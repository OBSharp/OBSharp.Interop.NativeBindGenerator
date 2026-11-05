using Humanizer;

namespace OBSharp.Interop.NativeBindGenerator;

/// <summary>
/// 按函数名前缀把首参为某类型指针的函数归并为该类型的实例成员。
/// </summary>
internal sealed partial class CExportGenerator
{
    private sealed record ParameterModel(string Name, MappedType Type);

    private sealed record FunctionModel(string Name, MappedType Return, List<ParameterModel> Parameters);

    private sealed class PropertyModel(string type)
    {
        public string Type { get; } = type;

        public FunctionModel? Getter { get; set; }

        public FunctionModel? Setter { get; set; }
    }

    private static readonly HashSet<string> ReservedHandleMembers =
    [
        "IsInvalid", "IsClosed", "Dispose", "Close", "SetHandleAsInvalid", "DangerousGetHandle",
        "DangerousAddRef", "DangerousRelease", "ReleaseHandle", "GetType", "ToString", "Equals", "GetHashCode",
    ];

    private readonly List<FunctionModel> _models = [];

    /// <summary>
    /// C 函数名 → 所属 C# 类型名；未登记的函数属于全局函数，仍写入 NativeMethods。
    /// </summary>
    private readonly Dictionary<string, string> _functionOwners = new(StringComparer.Ordinal);

    /// <summary>
    /// 首参 C# 类型文本 → (C 类型名, 是否为 SafeHandle 类)。
    /// </summary>
    private readonly Dictionary<string, (string CName, bool IsHandle)> _owners = [];

    private static readonly HashSet<string> ActionVerbs = new(StringComparer.Ordinal)
    {
        "set", "is", "has", "add", "remove", "load", "unload", "open", "close", "start", "stop", "run", "update",
        "create", "destroy", "free", "release", "delete", "clear", "reset", "init", "begin", "end", "enable",
        "disable", "show", "hide", "save", "read", "write", "send", "post", "emit", "find", "make", "copy",
        "clone", "connect", "disconnect", "register", "unregister", "lock", "unlock", "push", "pop", "wait",
        "process", "apply", "parse", "compare", "equals", "to", "from", "do", "try", "refresh", "reload",
    };

    /// <summary>
    /// 无 get_ 前缀的零参函数：首个单词不是动作动词时视为属性读取器（如 name、binary_path）。
    /// </summary>
    private static bool IsImplicitGetter(string remainder)
        => !ActionVerbs.Contains(remainder.Split('_', 2)[0]);

    private static string Pascal(string name)
    {
        string result = name.Pascalize();
        return result.Length is 0 || char.IsDigit(result[0]) ? "_" + result : result;
    }

    /// <summary>
    /// 去掉可空注解，用于按具体类型查找归属。
    /// </summary>
    private static string NonNullableType(string type)
        => type.EndsWith('?') ? type[..^1] : type;

    private void BuildMembers()
    {
        Dictionary<string, List<(FunctionModel Function, string Remainder)>> groups = [];
        Dictionary<string, List<(FunctionModel Function, string Remainder)>> statics = [];
        HashSet<FunctionModel> grouped = [];
        foreach (FunctionModel function in _models)
        {
            // 可空句柄参数在托管签名里是 Xxx?，归属判断仍需用去掉 ? 的具体类型
            if (function.Parameters is not [{ } first, ..]
                || first.Type.Modifier.Length is not 0
                || !_owners.TryGetValue(NonNullableType(first.Type.Type), out (string CName, bool IsHandle) owner)
                || !function.Name.StartsWith(owner.CName + "_", StringComparison.Ordinal)
                || function.Name.Length == owner.CName.Length + 1)
            {
                continue;
            }

            string firstType = NonNullableType(first.Type.Type);
            string typeName = owner.IsHandle ? firstType : firstType[..^1];
            _functionOwners[function.Name] = typeName;
            if (!groups.TryGetValue(typeName, out var list))
                groups[typeName] = list = [];
            list.Add((function, function.Name[(owner.CName.Length + 1)..]));
            grouped.Add(function);
        }

        // 名称带类型前缀、但首参不是该类型的函数（如 X_create）归为静态成员
        foreach (FunctionModel function in _models.Where(f => !grouped.Contains(f) && !_releaseFunctions.ContainsKey(f.Name)))
        {
            var match = _owners
                .Where(o => function.Name.Length > o.Value.CName.Length + 1
                    && function.Name.StartsWith(o.Value.CName + "_", StringComparison.Ordinal))
                .OrderByDescending(o => o.Value.CName.Length)
                .Select(o => (Key: o.Key, o.Value))
                .FirstOrDefault();
            if (match.Key is null)
                continue;

            string typeName = match.Value.IsHandle ? match.Key : match.Key[..^1];
            _functionOwners[function.Name] = typeName;
            if (!statics.TryGetValue(typeName, out var list))
                statics[typeName] = list = [];
            list.Add((function, function.Name[(match.Value.CName.Length + 1)..]));
        }

        // 返回某句柄类型的工厂/转换函数（如 Type* type_create()）也归入该类型作为静态成员
        foreach (FunctionModel function in _models.Where(f =>
            !_functionOwners.ContainsKey(f.Name)
            && !_releaseFunctions.ContainsKey(f.Name)
            && f.Return.Attribute.Length is 0
            && f.Return.Modifier.Length is 0))
        {
            if (!_owners.TryGetValue(function.Return.Type, out (string CName, bool IsHandle) owner)
                || !owner.IsHandle)
            {
                continue;
            }

            string typeName = function.Return.Type;
            _functionOwners[function.Name] = typeName;
            if (!statics.TryGetValue(typeName, out var list))
                statics[typeName] = list = [];
            list.Add((function, function.Name.Length > owner.CName.Length + 1
                && function.Name.StartsWith(owner.CName + "_", StringComparison.OrdinalIgnoreCase)
                    ? function.Name[(owner.CName.Length + 1)..]
                    : function.Name));
        }

        // 释放函数的首参被映射为 nint（避免句柄自引用），需按记录键单独归入对应句柄类
        foreach ((uint key, string functionName) in _handleReleases)
        {
            if (_handleNames.TryGetValue(key, out string? handleName))
                _functionOwners.TryAdd(functionName, handleName);
        }

        foreach (string typeName in groups.Keys.Union(statics.Keys))
        {
            bool isHandle = _owners.ContainsKey(typeName);
            _typeSources[typeName + "#members"] = MembersSource(
                typeName,
                isHandle,
                groups.GetValueOrDefault(typeName) ?? [],
                statics.GetValueOrDefault(typeName) ?? []);
        }
    }

    private string MembersSource(
        string typeName,
        bool isHandle,
        List<(FunctionModel Function, string Remainder)> members,
        List<(FunctionModel Function, string Remainder)> staticMembers)
    {
        Dictionary<string, PropertyModel> properties = new(StringComparer.Ordinal);
        List<(FunctionModel Function, string Name)> methods = [];

        string MemberName(string raw)
        {
            string name = Pascal(raw);
            return name == typeName || (isHandle && ReservedHandleMembers.Contains(name)) ? name + "_" : name;
        }

        foreach ((FunctionModel function, string remainder) in members)
        {
            int extra = function.Parameters.Count - 1;
            bool returnsValue = function.Return.Type is not "void";

            string? getterRaw = remainder.StartsWith("get_", StringComparison.Ordinal) && remainder.Length > 4
                ? remainder[4..]
                : IsImplicitGetter(remainder) ? remainder : null;

            if (getterRaw is not null
                && extra is 0
                && returnsValue)
            {
                string name = MemberName(getterRaw);
                if (properties.TryGetValue(name, out PropertyModel? existing)
                    && existing.Getter is not null)
                {
                    methods.Add((function, MemberName(remainder)));
                    continue;
                }

                if (existing is not null
                    && existing.Type != function.Return.Type)
                {
                    methods.Add((function, MemberName(remainder)));
                    continue;
                }

                if (existing is null)
                    properties[name] = existing = new(function.Return.Type);
                existing.Getter = function;
            }
            else if (remainder.StartsWith("set_", StringComparison.Ordinal)
                && remainder.Length > 4
                && extra is 1
                && !returnsValue
                && function.Parameters[1].Type.Modifier.Length is 0)
            {
                string name = MemberName(remainder[4..]);
                string type = function.Parameters[1].Type.Type;
                if (properties.TryGetValue(name, out PropertyModel? existing))
                {
                    if (existing.Type != type
                        || existing.Setter is not null)
                    {
                        methods.Add((function, MemberName(remainder)));
                        continue;
                    }
                }
                else
                {
                    properties[name] = existing = new(type);
                }

                existing.Setter = function;
            }
            else if ((remainder.StartsWith("is_", StringComparison.Ordinal)
                    || remainder.StartsWith("has_", StringComparison.Ordinal))
                && extra is 0
                && function.Return.Type is "bool")
            {
                string name = MemberName(remainder);
                if (properties.ContainsKey(name))
                    methods.Add((function, name));
                else
                    properties[name] = new(function.Return.Type) { Getter = function };
            }
            else
            {
                methods.Add((function, MemberName(remainder)));
            }
        }

        string kind = isHandle
            ? $"public sealed unsafe partial class {typeName}"
            : $"public unsafe partial struct {typeName}";

        List<string> blocks = [];
        foreach ((string name, PropertyModel property) in properties.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            List<string> accessors = [];
            if (property.Getter is not null)
            {
                accessors.Add($$"""
                            get
                            {
                    {{Body(property.Getter, typeName, isHandle, 12, "return ")}}
                            }
                    """);
            }

            if (property.Setter is not null)
            {
                accessors.Add($$"""
                            set
                            {
                    {{Body(property.Setter, typeName, isHandle, 12, "", "value")}}
                            }
                    """);
            }

            blocks.Add($$"""
                    public {{property.Type}} {{name}}
                    {
                {{string.Join("\n", accessors)}}
                    }
                """);
        }

        HashSet<string> methodSignatures = [];
        string UniqueMethodName(string methodName, string parameterTypes)
        {
            string name = methodName;
            string signature = $"{name}({parameterTypes})";
            if (properties.ContainsKey(name)
                || !methodSignatures.Add(signature))
            {
                for (int suffix = 2; ; suffix++)
                {
                    name = $"{methodName}_{suffix}";
                    signature = $"{name}({parameterTypes})";
                    if (!properties.ContainsKey(name)
                        && methodSignatures.Add(signature))
                    {
                        break;
                    }
                }
            }

            return name;
        }

        foreach ((FunctionModel function, string methodName) in methods.OrderBy(m => m.Name, StringComparer.Ordinal))
        {
            string parameterTypes = string.Join(",", function.Parameters.Skip(1)
                .Select(p => p.Type.Modifier + p.Type.Type));
            string name = UniqueMethodName(methodName, parameterTypes);

            string parameters = string.Join(", ", function.Parameters.Skip(1)
                .Select(p => $"{p.Type.Modifier}{p.Type.Type} {p.Name}"));
            blocks.Add($$"""
                    public {{function.Return.Type}} {{name}}({{parameters}})
                    {
                {{Body(function, typeName, isHandle, 8, function.Return.Type is "void" ? "" : "return ")}}
                    }
                """);
        }

        foreach ((FunctionModel function, string raw) in staticMembers.OrderBy(m => m.Remainder, StringComparer.Ordinal))
        {
            string parameters = string.Join(", ", function.Parameters
                .Select(p => $"{p.Type.Modifier}{p.Type.Type} {p.Name}"));
            string arguments = string.Join(", ", function.Parameters.Select(p => p.Type.Modifier + p.Name));
            string call = $"{CallTarget(function.Name, typeName)}({arguments})";

            string methodName = MemberName(raw);
            string parameterTypes = string.Join(",", function.Parameters
                .Select(p => p.Type.Modifier + p.Type.Type));
            methodName = UniqueMethodName(methodName, parameterTypes);
            blocks.Add($$"""
                    public static {{function.Return.Type}} {{methodName}}({{parameters}})
                    {
                        {{(function.Return.Type is "void" ? "" : "return ")}}{{call}};
                    }
                """);
        }

        string body = string.Join("\n\n", blocks);
        return $$"""
            {{kind}}
            {
            {{body}}
            }
            """;
    }

    private string Body(
        FunctionModel function,
        string typeName,
        bool isHandle,
        int indent,
        string prefix,
        string? valueArgument = null)
    {
        string pad = new(' ', indent);
        List<string> arguments = [isHandle ? "this" : "self"];
        for (int i = 1; i < function.Parameters.Count; i++)
        {
            ParameterModel parameter = function.Parameters[i];
            arguments.Add(valueArgument is not null && i is 1
                ? valueArgument
                : parameter.Type.Modifier + parameter.Name);
        }

        string call = $"{prefix}{CallTarget(function.Name, typeName)}({string.Join(", ", arguments)});";
        return isHandle
            ? pad + call
            : $$"""
                {{pad}}fixed ({{typeName}}* self = &this)
                {{pad}}    {{call}}
                """;
    }
}
