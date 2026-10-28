using Nebra.Diagnostics;
using Nebra.IR;
using Type = Nebra.IR.Type;

namespace Nebra.Compiler.Passes;

public sealed partial class InferTypesPass
{
    /// <summary>
    /// Records the signature of every constructor <paramref name="cd"/> declares on
    /// <paramref name="classType"/>: the first as its <see cref="ClassType.ConstructorType"/>, all
    /// of them as its overloads.
    /// </summary>
    private void RegisterConstructors(PassContext pc, ClassDecl cd, ClassType classType)
    {
        classType.ConstructorOverloads.Clear();
        classType.LocalConstructors.Clear();
        foreach (var constructor in cd.Constructors)
        {
            var signature = ConstructorTypeOf(pc, constructor, classType);
            classType.ConstructorOverloads.Add(signature);
            if (constructor.IsLocal) classType.LocalConstructors.Add(signature);
        }

        classType.ConstructorType = classType.ConstructorOverloads.Count > 0 ? classType.ConstructorOverloads[0] : null;
    }

    /// <summary>Reports a constructor taking the same parameter types as one declared before it.</summary>
    private void ReportDuplicateConstructors(PassContext pc, ClassDecl cd, ClassType classType)
    {
        var suffixes = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < classType.ConstructorOverloads.Count; i++)
        {
            if (!suffixes.Add(OverloadSignature.Suffix(classType.ConstructorOverloads[i], false)))
                pc.Diag.Report(cd.Constructors[i].Span, DiagnosticCode.ErrDuplicateConstructor, cd.Name.Name);
        }
    }

    /// <summary>
    /// The constructors a <c>new</c> on <paramref name="classType"/> may run, with the class that
    /// declares them: its own, or else those of the nearest base class declaring any.
    /// </summary>
    private static (ClassType Owner, List<FunctionType> Overloads)? ResolveConstructorOverloads(ClassType classType)
    {
        for (var current = classType; current != null; current = current.BaseClass)
        {
            if (current.ConstructorOverloads.Count > 0) return (current, current.ConstructorOverloads);
            if (current.ConstructorType != null) return (current, [current.ConstructorType]);
        }

        return null;
    }

    /// <summary>
    /// The constructor of <paramref name="overloads"/> the arguments pick, and the name of its Lua
    /// function when the call can be bound to it at compile time, such as <c>new__string</c>.
    /// Reports arguments no constructor takes and returns <c>null</c> for them.
    /// </summary>
    private FunctionType? PickConstructor(PassContext pc, TextSpan span, string className, ClassType owner, List<FunctionType> overloads,
        List<Expr> arguments, List<TypID> argTypes, out string? overloadName)
    {
        overloadName = null;
        if (overloads.Count == 1) return overloads[0];

        var picked = PickOverload(pc, overloads, [], WithLiteralTypes(pc, arguments, argTypes), false, out var certain);
        if (picked == null)
        {
            var signatures = overloads.Select(overload => "(" + string.Join(", ", overload.ParamTypes.Select(type => TypeName(pc, type.ID))) + ")");
            pc.Diag.Report(span, DiagnosticCode.ErrNoMatchingConstructor, className,
                string.Join(", ", argTypes.Select(type => TypeName(pc, type))), string.Join(", ", signatures));
            return null;
        }

        var definition = owner.Definition;
        var index = overloads.IndexOf(picked);
        if (certain && definition.IsCompiledFromSource && index >= 0 && index < definition.ConstructorOverloads.Count
            && OverloadSignature.AreDistinct(definition.ConstructorOverloads, false))
            overloadName = OverloadSignature.VariantName("new", definition.ConstructorOverloads[index], false);

        return picked;
    }

    /// <summary>Whether the base class of a class with its own constructor can be created without arguments.</summary>
    private static bool HasArgumentlessConstructor(ClassType classType)
    {
        var inherited = ResolveConstructorOverloads(classType);
        return inherited == null || inherited.Value.Overloads.Any(constructor => constructor.MinParamCount == 0);
    }
}
