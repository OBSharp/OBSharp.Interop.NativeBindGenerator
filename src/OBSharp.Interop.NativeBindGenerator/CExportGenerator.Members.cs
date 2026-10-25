using System.Text;

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

    private void BuildMembers()
    {
        Dictionary<string, List<(FunctionModel Function, string Remainder)>> groups = [];
        foreach (FunctionModel function in _models)
        {
            if (function.Parameters is not [{ } first, ..]
                || first.Type.Attribute.Length is not 0
                || first.Type.Modifier.Length is not 0
                || !_owners.TryGetValue(first.Type.Type, out (string CName, bool IsHandle) owner)
                || !function.Name.StartsWith(owner.CName + "_", StringComparison.Ordinal)
                || function.Name.Length == owner.CName.Length + 1)
            {
                continue;
            }

            string typeName = owner.IsHandle ? first.Type.Type : first.Type.Type[..^1];
            if (!groups.TryGetValue(typeName, out var list))
                groups[typeName] = list = [];
            list.Add((function, function.Name[(owner.CName.Length + 1)..]));
        }

        foreach ((string typeName, var members) in groups)
        {
            bool isHandle = _owners.ContainsKey(typeName);
            _typeSources[typeName + "#members"] = MembersSource(typeName, isHandle, members);
        }
    }

    private string MembersSource(
        string typeName,
        bool isHandle,
        List<(FunctionModel Function, string Remainder)> members)
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

        StringBuilder source = new();
        source.AppendLine(isHandle
            ? $"public sealed unsafe partial class {typeName}"
            : $"public unsafe partial struct {typeName}");
        source.AppendLine("{");

        List<string> blocks = [];
        foreach ((string name, PropertyModel property) in properties.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            StringBuilder block = new();
            block.AppendLine($"    public {property.Type} {name}");
            block.AppendLine("    {");
            if (property.Getter is not null)
            {
                block.AppendLine("        get");
                block.AppendLine("        {");
                AppendBody(block, property.Getter, typeName, isHandle, 12, "return ");
                block.AppendLine("        }");
            }

            if (property.Setter is not null)
            {
                block.AppendLine("        set");
                block.AppendLine("        {");
                AppendBody(block, property.Setter, typeName, isHandle, 12, "", "value");
                block.AppendLine("        }");
            }

            block.Append("    }");
            blocks.Add(block.ToString());
        }

        foreach ((FunctionModel function, string name) in methods.OrderBy(m => m.Name, StringComparer.Ordinal))
        {
            string parameters = string.Join(", ", function.Parameters.Skip(1)
                .Select(p => $"{p.Type.Modifier}{p.Type.Type} {p.Name}"));
            StringBuilder block = new();
            block.AppendLine($"    public {function.Return.Type} {name}({parameters})");
            block.AppendLine("    {");
            AppendBody(block, function, typeName, isHandle, 8, function.Return.Type is "void" ? "" : "return ");
            block.Append("    }");
            blocks.Add(block.ToString());
        }

        source.AppendLine(string.Join(Environment.NewLine + Environment.NewLine, blocks));
        source.Append('}');
        return source.ToString();
    }

    private static void AppendBody(
        StringBuilder builder,
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

        string call = $"{prefix}NativeMethods.{Identifier(function.Name)}({string.Join(", ", arguments)});";
        if (isHandle)
        {
            builder.AppendLine(pad + call);
            return;
        }

        builder.AppendLine($"{pad}fixed ({typeName}* self = &this)");
        builder.AppendLine($"{pad}    {call}");
    }
}
