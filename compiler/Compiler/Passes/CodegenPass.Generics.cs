using Nebra.IR;
using Type = Nebra.IR.Type;

namespace Nebra.Compiler.Passes;

public sealed partial class CodegenPass
{
    /// <summary>
    /// The type arguments a <c>new</c> records for the object it creates, as a Lua table from the
    /// name of each generic class and interface the object is an instance of to its arguments:
    /// <c>{ ["Box"] = { "number" } }</c> for a <c>Box&lt;number&gt;</c>. Arguments that are type
    /// parameters are not known at runtime and leave their entry out. <c>null</c> when there is
    /// nothing to record.
    /// </summary>
    private static string? TypeArgumentRecord(PassContext ctx, TypID created)
    {
        if (!ctx.Types.GetByID(created, out var type) || type is not ClassType classType) return null;

        var entries = new List<string>();
        for (var current = classType; current != null; current = current.BaseClass)
        {
            if (current.GenericDefinition != null && RuntimeArguments(current.TypeArguments) is { } arguments)
                entries.Add($"[\"{current.Name}\"] = {arguments}");
        }

        foreach (var iface in Type.ImplementedInterfaces(classType).Where(iface => iface.GenericDefinition != null))
        {
            if (RuntimeArguments(iface.TypeArguments) is { } arguments)
                entries.Add($"[\"{iface.Name}\"] = {arguments}");
        }

        return entries.Count == 0 ? null : "{ " + string.Join(", ", entries.Distinct()) + " }";
    }

    /// <summary>
    /// <paramref name="arguments"/> as a Lua list of their runtime names, or <c>null</c> when one
    /// of them is a type parameter.
    /// </summary>
    private static string? RuntimeArguments(List<Type> arguments)
    {
        var names = arguments.Select(RuntimeTypeName).ToList();
        return names.Any(name => name == null) ? null : "{ " + string.Join(", ", names.Select(name => $"\"{name}\"")) + " }";
    }

    /// <summary>
    /// The name a type is recorded and checked under at runtime: the same for the same type, and
    /// <c>null</c> for one that contains a type parameter.
    /// </summary>
    private static string? RuntimeTypeName(Type type)
    {
        switch (type)
        {
            case TypeParameterType:
                return null;
            case ClassType { GenericDefinition: not null } instance:
                return RuntimeArgumentsText(instance.Name, instance.TypeArguments);
            case InterfaceType { GenericDefinition: not null } instance:
                return RuntimeArgumentsText(instance.Name, instance.TypeArguments);
            case ClassType classType:
                return classType.Name;
            case InterfaceType interfaceType:
                return interfaceType.Name;
            case EnumType enumType:
                return enumType.Name;
            case TableArrayType array:
                return RuntimeTypeName(array.ElementType) is { } element ? element + "[]" : null;
            case TableMapType map:
                return RuntimeTypeName(map.KeyType) is { } key && RuntimeTypeName(map.ValueType) is { } value ? "{" + key + ":" + value + "}" : null;
            case UnionType union:
            {
                var members = union.Types.Select(RuntimeTypeName).ToList();
                return members.Any(member => member == null) ? null : string.Join("|", members.OrderBy(member => member, StringComparer.Ordinal));
            }
            case FunctionType:
                return "function";
            case StructType or TupleType:
                return "table";
            default:
                return type.Kind switch
                {
                    TypeKind.PrimitiveNil => "nil",
                    TypeKind.PrimitiveNumber => "number",
                    TypeKind.PrimitiveString => "string",
                    TypeKind.PrimitiveBool => "boolean",
                    TypeKind.PrimitiveFunction => "function",
                    TypeKind.PrimitiveThread => "thread",
                    TypeKind.PrimitiveUserdata => "userdata",
                    _ => "any",
                };
        }
    }

    private static string? RuntimeArgumentsText(string name, List<Type> arguments)
    {
        var names = arguments.Select(RuntimeTypeName).ToList();
        return names.Any(argument => argument == null) ? null : $"{name}<{string.Join(",", names)}>";
    }
}
