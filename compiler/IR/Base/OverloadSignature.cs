namespace Nebra.IR;

/// <summary>
/// Names the overloads of a function or method by their parameter types. Every overload of a
/// name that has several is emitted as its own Lua function under such a name, so a call whose
/// overload is known at compile time reaches it directly, while the plain name keeps a
/// dispatcher that picks the overload from the arguments at runtime.
/// </summary>
public static class OverloadSignature
{
    /// <summary>
    /// The name of the Lua function holding the overload <paramref name="function"/> of
    /// <paramref name="name"/>, such as <c>put__number</c> or <c>draw__Vector2_string</c>.
    /// The implicit <c>self</c> of an instance method is not part of it.
    /// </summary>
    public static string VariantName(string name, FunctionType function, bool hasSelf) =>
        name + "__" + Suffix(function, hasSelf);

    /// <summary>
    /// The parameter types of <paramref name="function"/> written as an identifier, which two
    /// overloads share exactly when they cannot be told apart.
    /// </summary>
    public static string Suffix(FunctionType function, bool hasSelf)
    {
        var parameters = function.ParamTypes.Skip(hasSelf ? 1 : 0).Select(TypeName).ToList();
        if (function.IsVararg) parameters.Add("varargs");
        return parameters.Count == 0 ? "none" : string.Join("_", parameters);
    }

    /// <summary>
    /// The overloads of the instance method <paramref name="name"/> that <paramref name="classType"/>
    /// declares or inherits, each with the class declaring it. An overload a class declares hides
    /// the one of the same parameter types it inherits.
    /// </summary>
    public static List<(ClassType Owner, FunctionType Function)> InstanceOverloads(ClassType classType, string name)
    {
        var overloads = new List<(ClassType Owner, FunctionType Function)>();
        var suffixes = new HashSet<string>(StringComparer.Ordinal);
        for (var current = classType; current != null; current = current.BaseClass)
        {
            if (!current.MethodOverloads.TryGetValue(name, out var functions)) continue;

            foreach (var function in functions)
            {
                if (suffixes.Add(Suffix(function, true))) overloads.Add((current, function));
            }
        }

        return overloads;
    }

    /// <summary>
    /// Whether <paramref name="owner"/> emits its own overloads of <paramref name="name"/> under
    /// their variant names: it is compiled from Nebra source, more than one overload is visible
    /// on it, and the ones it declares itself can all be told apart.
    /// </summary>
    public static bool IsSplit(ClassType owner, string name, bool isStatic)
    {
        if (!owner.IsCompiledFromSource) return false;

        var own = isStatic ? owner.StaticMethodOverloads : owner.MethodOverloads;
        if (!own.TryGetValue(name, out var declared) || !AreDistinct(declared, !isStatic)) return false;

        return isStatic ? declared.Count > 1 : InstanceOverloads(owner, name).Count > 1;
    }

    /// <summary>Whether no two of <paramref name="functions"/> share their parameter types.</summary>
    public static bool AreDistinct(IEnumerable<FunctionType> functions, bool hasSelf)
    {
        var suffixes = new HashSet<string>(StringComparer.Ordinal);
        return functions.All(function => suffixes.Add(Suffix(function, hasSelf)));
    }

    private static string TypeName(Type type)
    {
        var name = type switch
        {
            ClassType classType => classType.Name,
            InterfaceType interfaceType => interfaceType.Name,
            EnumType enumType => enumType.Name,
            TableArrayType array => TypeName(array.ElementType) + "_array",
            TableMapType => "map",
            UnionType union when union.Types.Any(member => member.Kind == TypeKind.PrimitiveNil) =>
                string.Join("_", union.Types.Where(member => member.Kind != TypeKind.PrimitiveNil).Select(TypeName)) + "_opt",
            UnionType union => string.Join("_or_", union.Types.Select(TypeName)),
            FunctionType => "function",
            _ => type.Kind switch
            {
                TypeKind.PrimitiveNil => "nil",
                TypeKind.PrimitiveAny => "any",
                TypeKind.PrimitiveNumber => "number",
                TypeKind.PrimitiveBool => "boolean",
                TypeKind.PrimitiveString => "string",
                TypeKind.PrimitiveFunction => "function",
                TypeKind.PrimitiveThread => "thread",
                TypeKind.PrimitiveUserdata => "userdata",
                _ => type.Kind.ToString().ToLowerInvariant(),
            },
        };
        return new string(name.Select(character => char.IsLetterOrDigit(character) ? character : '_').ToArray());
    }
}
