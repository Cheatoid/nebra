using Nebra.Diagnostics;
using Nebra.IR;

namespace Nebra.Compiler.Passes;

public sealed partial class InferTypesPass
{
    private static readonly HashSet<string> OperatorMetamethods = new(StringComparer.Ordinal)
    {
        "__add", "__sub", "__mul", "__div", "__mod", "__pow", "__unm", "__concat", "__len", "__eq",
        "__lt", "__le", "__idiv", "__band", "__bor", "__bxor", "__bnot", "__shl", "__shr",
    };

    private static readonly string[] InternalPrefixes = ["__nebra", "__get_", "__set_", "__op_", "__ext_"];

    /// <summary>
    /// Reports the members of a class written in Nebra whose names it cannot have: the
    /// metamethods <c>operator</c> declares, and the names Nebra uses on every class table.
    /// Operators themselves are stored under such names and are left alone.
    /// </summary>
    private void ReportReservedMemberNames(PassContext pc, ClassDecl cd)
    {
        if (cd.IsDeclare) return;

        foreach (var field in cd.Fields) ReportReservedMemberName(pc, field.Name);
        foreach (var method in cd.Methods.Where(method => !method.IsOperator)) ReportReservedMemberName(pc, method.Name);
        foreach (var accessor in cd.Accessors) ReportReservedMemberName(pc, accessor.Name);

        var callMethod = cd.Methods.FirstOrDefault(method => !method.IsOperator && method.Name.Name == "__call");
        if (callMethod != null && cd.Methods.Any(method => method is { IsOperator: true, Name.Name: "__op_call" }))
            pc.Diag.Report(callMethod.Name.Span, DiagnosticCode.ErrReservedMemberName, "__call",
                "the class declares 'operator ()', which already supplies '__call'");
    }

    /// <summary>The interface version of <see cref="ReportReservedMemberNames(PassContext, ClassDecl)"/>.</summary>
    private void ReportReservedMemberNames(PassContext pc, InterfaceDecl id)
    {
        if (id.IsDeclare) return;

        foreach (var field in id.Fields) ReportReservedMemberName(pc, field.Name);
        foreach (var method in id.Methods) ReportReservedMemberName(pc, method.Name);
    }

    /// <summary>
    /// Reports an operator declaring several return values. Lua keeps only the first value a
    /// metamethod returns, except for <c>__call</c>, which hands on everything like any call.
    /// </summary>
    private static void ReportOperatorReturns(PassContext pc, string metamethod, TypeRef? annotation, IR.Type returnType)
    {
        if (metamethod == "__op_call" || annotation == null) return;
        if (returnType is not TupleType { Fields.Count: > 1 } tuple) return;

        pc.Diag.Report(annotation.Span, DiagnosticCode.ErrOperatorMultipleReturns, OperatorSymbol(metamethod), tuple.Fields.Count);
    }

    /// <summary>
    /// Reports an assignment that replaces a class compiled from Nebra source, <c>A = ...</c>, or
    /// one of its methods or its constructor, <c>A.new = ...</c>. Static fields stay assignable.
    /// </summary>
    private void ReportClassReplacement(PassContext pc, Expr target)
    {
        switch (target)
        {
            case NameExpr name when IsClassReference(pc, name):
                pc.Diag.Report(target.Span, DiagnosticCode.ErrAssignToClass, $"the class '{name.Name.Name}'");
                break;
            case DotAccessExpr { Object: NameExpr owner } dot when IsClassReference(pc, owner) && IsClassFunction(pc, owner, dot.FieldName.Name):
                pc.Diag.Report(target.Span, DiagnosticCode.ErrAssignToClass, $"'{owner.Name.Name}.{dot.FieldName.Name}'");
                break;
        }
    }

    /// <summary>
    /// Reports <c>function A.new() end</c> or <c>function A:method() end</c> written outside the
    /// class for a function the class already has, which would replace it.
    /// </summary>
    private void ReportClassMemberReplacement(PassContext pc, FunctionDecl fd)
    {
        var member = fd switch
        {
            { MethodName: { } method, NamePath.Count: 1 } => method,
            { MethodName: null, NamePath.Count: 2 } => fd.NamePath[1],
            _ => null,
        };
        if (member == null) return;

        var owner = new NameExpr(fd.ID, fd.NamePath[0].Span, fd.NamePath[0]);
        if (!IsClassReference(pc, owner) || !IsClassFunction(pc, owner, member.Name)) return;

        pc.Diag.Report(member.Span, DiagnosticCode.ErrAssignToClass, $"'{fd.NamePath[0].Name}{(fd.MethodName != null ? ":" : ".")}{member.Name}'");
    }

    /// <summary>
    /// Whether <paramref name="name"/> refers to a class compiled from Nebra source. Declared
    /// classes describe tables the runtime owns, which Lua code may legitimately patch.
    /// </summary>
    private bool IsClassReference(PassContext pc, NameExpr name) =>
        pc.Pkg!.Syms.GetByID(name.Name.Sym, out var symbol) && symbol.Kind == SymbolKind.Class
        && pc.Types.GetByID(symbol.Type, out var type) && type is ClassType { IsCompiledFromSource: true };

    /// <summary>Whether <paramref name="name"/> is the constructor or a method, static or not, of the class <paramref name="owner"/> names.</summary>
    private bool IsClassFunction(PassContext pc, NameExpr owner, string name)
    {
        if (!pc.Types.GetByID(LookupSymbolType(pc, owner.Name.Sym), out var type) || type is not ClassType classType) return false;
        if (name == "new") return true;

        for (var current = classType; current != null; current = current.BaseClass)
        {
            if (current.Methods.ContainsKey(name) || current.StaticMethods.ContainsKey(name)) return true;
        }

        return false;
    }

    /// <summary>
    /// Reports a generic base class or interface named in <c>extends</c> or <c>implements</c> with
    /// another number of type arguments than it declares type parameters, none included.
    /// </summary>
    private static void ReportBaseTypeArguments(PassContext pc, NameRef baseName, int arity, List<TypeArgRef> arguments)
    {
        if (arity == 0 || arguments.Count == arity) return;
        pc.Diag.Report(baseName.Span, DiagnosticCode.ErrTypeParamArityMismatch, baseName.Name, arity, arguments.Count);
    }

    /// <summary>
    /// Reports an accessor with parameters it cannot be called with: a getter is read without any,
    /// and a setter receives exactly the assigned value.
    /// </summary>
    private static void ReportAccessorParameters(PassContext pc, AccessorKind kind, NameRef name, List<Parameter> parameters)
    {
        if (kind == AccessorKind.Getter && parameters.Count != 0)
            pc.Diag.Report(name.Span, DiagnosticCode.ErrAccessorParameters, $"the getter '{name.Name}' takes no parameters");
        else if (kind == AccessorKind.Setter && (parameters.Count != 1 || parameters[0].IsVararg))
            pc.Diag.Report(name.Span, DiagnosticCode.ErrAccessorParameters, $"the setter '{name.Name}' takes exactly one parameter, the assigned value");
    }

    private static void ReportReservedMemberName(PassContext pc, NameRef name)
    {
        if (ReservedMemberReason(name.Name) is { } reason)
            pc.Diag.Report(name.Span, DiagnosticCode.ErrReservedMemberName, name.Name, reason);
    }

    /// <summary>
    /// Why <paramref name="name"/> cannot name a member, or <c>null</c> when it can. Metamethods
    /// without a syntax of their own, such as <c>__tostring</c> or <c>__close</c>, stay allowed:
    /// as methods on the class table they act as those metamethods. So does <c>__call</c>, which
    /// <c>operator ()</c> keeps apart under a name of its own.
    /// </summary>
    private static string? ReservedMemberReason(string name)
    {
        if (OperatorMetamethods.Contains(name)) return $"declare it as 'operator {OperatorSymbol(name)}' instead";

        return name switch
        {
            "__index" => "declare 'operator []' instead",
            "__newindex" => "declare 'operator []=' instead",
            "__name" or "__interfaces" or "__metatable" => "Nebra uses it on every class table",
            _ when InternalPrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.Ordinal)) =>
                $"names starting with '{InternalPrefixes.First(prefix => name.StartsWith(prefix, StringComparison.Ordinal))}' are used by Nebra",
            _ => null,
        };
    }
}
