using Nebra.Diagnostics;
using Nebra.IR;
using Type = Nebra.IR.Type;

namespace Nebra.Compiler.Passes;

public sealed partial class InferTypesPass
{
    private const string IndexOperator = "__op_index";
    private const string NewIndexOperator = "__op_newindex";
    private const string CallOperator = "__op_call";

    /// <summary>
    /// Types reading <c>object[key]</c> through <c>operator []</c>, declared by the object's class
    /// or by an extension of its type.
    /// </summary>
    private bool TryIndexOperator(PassContext pc, IndexAccessExpr idx, Type objectType, TypID key, out TypID result)
    {
        if (TryClassOperator(pc, objectType, IndexOperator, [key], idx.Index.Span, out result))
        {
            idx.UsesIndexOperator = true;
            return true;
        }

        if (!TryExtensionCallOperator(pc, objectType, IndexOperator, [key], idx.Span, out var extension, out result)) return false;

        idx.Extension = extension;
        idx.UsesIndexOperator = true;
        return true;
    }

    /// <summary>
    /// Types the assignment of <paramref name="value"/> to <c>object[key]</c> through
    /// <c>operator []=</c>, declared by the object's class or by an extension of its type.
    /// </summary>
    private bool TryNewIndexOperator(PassContext pc, IndexAccessExpr target, TypID value)
    {
        var objectType = StripNil(pc, SynthesizeExpr(pc, target.Object));
        if (!pc.Pkg!.Types.GetByID(objectType, out var type)) return false;

        var key = SynthesizeExpr(pc, target.Index);
        if (TryClassOperator(pc, type, NewIndexOperator, [key, value], target.Span, out _))
        {
            target.UsesIndexOperator = true;
            return true;
        }

        if (type is ClassType && TryClassOperator(pc, type, IndexOperator, [key], target.Index.Span, out _))
        {
            pc.Diag.Report(target.Span, DiagnosticCode.ErrIndexWithoutAssignment, TypeName(pc, objectType));
            return true;
        }

        if (!TryExtensionCallOperator(pc, type, NewIndexOperator, [key, value], target.Span, out var extension, out _)) return false;

        target.Extension = extension;
        target.UsesIndexOperator = true;
        return true;
    }

    /// <summary>
    /// Types calling a value through <c>operator ()</c>, declared by its class or by an extension
    /// of its type. Calling a class itself stays an error; instances are created with <c>new</c>.
    /// </summary>
    private bool TryCallOperator(PassContext pc, FunctionCallExpr call, Type calleeType, List<TypID> arguments, out TypID result)
    {
        result = pc.Types.PrimAny.ID;
        var isClassReference = call.Callee is NameExpr name
            && pc.Pkg!.Syms.GetByID(name.Name.Sym, out var symbol)
            && symbol.Kind == SymbolKind.Class;
        if (isClassReference) return false;

        if (TryClassOperator(pc, calleeType, CallOperator, arguments, call.Span, out result)) return true;
        if (!TryExtensionCallOperator(pc, calleeType, CallOperator, arguments, call.Span, out var extension, out result)) return false;

        call.Extension = extension;
        return true;
    }

    /// <summary>
    /// Types applying the class operator <paramref name="name"/> of <paramref name="type"/> to
    /// <paramref name="arguments"/>, reporting the use when none of its overloads takes them.
    /// Returns <c>false</c> when the class declares no such operator.
    /// </summary>
    private bool TryClassOperator(PassContext pc, Type type, string name, List<TypID> arguments, TextSpan span, out TypID result)
    {
        result = pc.Types.PrimAny.ID;
        if (type is not ClassType classType) return false;

        var (overloads, sides) = CollectMethodOverloads(pc, classType, name, staticOnly: false);
        if (overloads.Count == 0) return false;

        var picked = PickOverload(pc, overloads, sides, arguments, prefixSelf: true);
        if (picked == null)
        {
            pc.Diag.Report(span, DiagnosticCode.ErrFuncParamMismatch, $"operator {OperatorSymbol(name)}", arguments.Count);
            return true;
        }

        var withSelf = new List<TypID>(arguments.Count + 1) { classType.ID };
        withSelf.AddRange(arguments);
        result = SpecializedReturn(pc, picked, withSelf);
        return true;
    }

    /// <summary>
    /// Types applying the extension operator <paramref name="name"/> of <paramref name="type"/> to
    /// <paramref name="arguments"/>: the first of its overloads that takes them.
    /// </summary>
    private bool TryExtensionCallOperator(PassContext pc, Type type, string name, List<TypID> arguments, TextSpan span,
        out ExtensionMethodNode? extension, out TypID result)
    {
        result = pc.Types.PrimAny.ID;
        extension = null;
        if (type.Kind == TypeKind.PrimitiveAny) return false;

        var withSelf = new List<TypID>(arguments.Count + 1) { type.ID };
        withSelf.AddRange(arguments);
        extension = Type.ResolveExtensionOperators(type, name, pc.Types.PrimFunction)
            .FirstOrDefault(candidate => ScoreOverload(pc, candidate.Signature!, withSelf) >= 0);
        if (extension == null) return false;

        UseExtension(pc, extension, span, type);
        result = SpecializedReturn(pc, extension.Signature!, withSelf);
        return true;
    }
}
