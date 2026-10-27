using Nebra.IR;

namespace Nebra.Compiler.Passes;

public sealed partial class InferTypesPass
{
    /// <summary>
    /// The type parameters of the generic declarations whose body is being checked. Inside such a
    /// body a type parameter stands for one type the caller chooses, so only a value of that type
    /// parameter fits it, and it fits only what its bounds fit.
    /// </summary>
    private readonly HashSet<TypID> _fixedTypeParams = [];

    /// <summary>
    /// Raised while call arguments are checked against a signature. A call fills in the callee's
    /// type parameters from its arguments, so there they stay open even inside the callee's own
    /// body, as a recursive call is.
    /// </summary>
    private int _openTypeParamDepth;

    /// <summary>
    /// Marks <paramref name="typeParams"/> as fixed until the returned scope is disposed, for the
    /// body of the declaration that declares them.
    /// </summary>
    private FixedTypeParamScope FixTypeParams(IEnumerable<TypeParamDef> typeParams)
    {
        var added = typeParams
            .Select(typeParam => typeParam.ResolvedType)
            .Where(id => id != TypID.Invalid && _fixedTypeParams.Add(id))
            .ToList();
        return new FixedTypeParamScope(_fixedTypeParams, added);
    }

    private bool IsFixed(TypeParameterType typeParam) => _openTypeParamDepth == 0 && _fixedTypeParams.Contains(typeParam.ID);

    /// <summary>
    /// Assignability to a fixed type parameter: only a value of that type parameter fits it, and
    /// <c>nil</c> while nil is not strict. Returns <c>null</c> for a union source, which is
    /// checked member by member.
    /// </summary>
    private bool? AssignableToFixedTypeParam(PassContext pc, TypID src)
    {
        if (!pc.Config.Rules.StrictNil && src == pc.Types.PrimNil.ID) return true;
        if (pc.Pkg!.Types.GetByID(src, out var source) && source is UnionType) return null;
        return false;
    }

    /// <summary>
    /// Assignability of a fixed type parameter to another type: it fits what one of its bounds
    /// fits. Returns <c>null</c> for a union destination, which is checked member by member.
    /// </summary>
    private bool? AssignableFromFixedTypeParam(PassContext pc, TypeParameterType typeParam, TypID dst)
    {
        if (pc.Pkg!.Types.GetByID(dst, out var destination) && destination is UnionType) return null;
        if (typeParam.ExtendsBound is { } extendsBound && IsTypeAssignable(pc, dst, extendsBound)) return true;
        return typeParam.ImplementsBounds.Any(bound => IsTypeAssignable(pc, dst, bound));
    }

    private sealed class FixedTypeParamScope(HashSet<TypID> fixedTypeParams, List<TypID> added) : IDisposable
    {
        public void Dispose()
        {
            foreach (var id in added) fixedTypeParams.Remove(id);
        }
    }
}
