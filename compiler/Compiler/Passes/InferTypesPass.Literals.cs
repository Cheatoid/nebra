using Nebra.Diagnostics;
using Nebra.IR;
using Type = Nebra.IR.Type;

namespace Nebra.Compiler.Passes;

public sealed partial class InferTypesPass
{
    /// <summary>
    /// The declared return type of the function whose body is being checked, so a literal it
    /// returns can keep its literal type; <see cref="TypID.Invalid"/> where there is none.
    /// </summary>
    private TypID _declaredReturn = TypID.Invalid;

    /// <summary>
    /// <paramref name="type"/> with its <c>keyof</c> and indexed access types worked out as far as
    /// what they refer to is known: <c>keyof Events</c> becomes the union of the names of the
    /// fields of <c>Events</c>, <c>Events["user:login"]</c> the type of that field.
    /// </summary>
    private Type Evaluate(PassContext pc, Type type)
    {
        return type switch
        {
            KeyOfType keyOf => EvaluateKeyOf(pc, keyOf),
            IndexedAccessType indexed => EvaluateIndexedAccess(pc, indexed),
            UnionType union when union.Types.Any(member => member is KeyOfType or IndexedAccessType) => EvaluateUnion(pc, union),
            _ => type,
        };
    }

    private Type EvaluateUnion(PassContext pc, UnionType union)
    {
        var members = union.Types.Select(member => Evaluate(pc, member)).ToList();
        return members.Zip(union.Types).All(pair => ReferenceEquals(pair.First, pair.Second))
            ? union
            : GetType(pc, pc.Types.UnionOf(members));
    }

    private TypID Evaluate(PassContext pc, TypID type) =>
        pc.Pkg!.Types.GetByID(type, out var resolved) ? Evaluate(pc, resolved).ID : type;

    private Type EvaluateKeyOf(PassContext pc, KeyOfType keyOf)
    {
        var target = Evaluate(pc, keyOf.Target);
        var names = Type.FieldNamesOf(target);
        if (names == null) return ContainsTypeParameter(target) ? keyOf : pc.Types.PrimAny;

        var literals = names.Select(name => (Type)pc.Types.LiteralOf(LiteralValue.Of(name))).ToList();
        return literals.Count switch
        {
            0 => pc.Types.PrimNever,
            1 => literals[0],
            _ => GetType(pc, pc.Types.UnionOf(literals)),
        };
    }

    private Type EvaluateIndexedAccess(PassContext pc, IndexedAccessType indexed)
    {
        var target = Evaluate(pc, indexed.Target);
        var index = Evaluate(pc, indexed.Index);

        var keys = LiteralKeys(index);
        if (keys == null)
        {
            if (ContainsTypeParameter(index)) return indexed;
            return target switch
            {
                TableMapType map => map.ValueType,
                TableArrayType list when IsTypeAssignable(pc, pc.Types.PrimNumber.ID, index.ID) => list.ElementType,
                _ => indexed,
            };
        }

        var fields = keys.Select(key => Type.FieldTypeOf(target, key)).ToList();
        if (fields.Any(field => field == null)) return pc.Types.PrimAny;

        var distinct = fields.DistinctBy(field => field!.ID).Select(field => field!).ToList();
        return distinct.Count == 1 ? distinct[0] : GetType(pc, pc.Types.UnionOf(distinct));
    }

    /// <summary>The field names a string literal type or a union of them stands for; <c>null</c> for any other type.</summary>
    private static List<string>? LiteralKeys(Type index)
    {
        if (index is LiteralType { Value.StringValue: { } key }) return [key];
        if (index is not UnionType union) return null;

        var keys = union.Types.Select(member => member is LiteralType { Value.StringValue: { } name } ? name : null).ToList();
        return keys.All(key => key != null) ? keys.Select(key => key!).ToList() : null;
    }

    /// <summary>
    /// The type a value of <paramref name="type"/> is used as: the evaluated type, and for a field
    /// <c>Events[K]</c> with <c>K extends keyof Events</c> the union of the types of every field
    /// <c>K</c> may name.
    /// </summary>
    private TypID ApparentType(PassContext pc, TypID type)
    {
        var evaluated = Evaluate(pc, type);
        if (!pc.Pkg!.Types.GetByID(evaluated, out var resolved)) return evaluated;

        return resolved switch
        {
            IndexedAccessType { Index: TypeParameterType { ExtendsBound: { } bound } } indexed
                when pc.Pkg.Types.GetByID(bound, out var boundType) =>
                Evaluate(pc, pc.Pkg.Types.DeclareType(new IndexedAccessType(indexed.Target, boundType)).ID),
            KeyOfType => pc.Types.PrimString.ID,
            _ => evaluated,
        };
    }

    /// <summary>
    /// The base type of a literal type, or of a union of literals of one base type such as
    /// <c>"a" | "b"</c>, whose members are those of the base type; <c>null</c> for any other type.
    /// </summary>
    private static Type? LiteralBase(Type type)
    {
        if (type is LiteralType literal) return literal.BaseType;
        if (type is not UnionType union || !union.Types.All(member => member is LiteralType)) return null;

        var bases = union.Types.Select(member => ((LiteralType)member).BaseType).DistinctBy(baseType => baseType.ID).ToList();
        return bases.Count == 1 ? bases[0] : null;
    }

    /// <summary><paramref name="type"/>, or the base type of the literals it is made of.</summary>
    private TypID WithoutLiterals(PassContext pc, TypID type) =>
        pc.Pkg!.Types.GetByID(type, out var resolved) && LiteralBase(resolved) is { } literalBase ? literalBase.ID : type;

    /// <summary>
    /// Assignability to or from an indexed access that cannot be worked out yet, such as
    /// <c>Events[K]</c> inside the generic body declaring <c>K</c>: it stands for the type of any
    /// field <c>K</c> may name, and for anything where not even that is known. <c>null</c> where
    /// neither side is one.
    /// </summary>
    private bool? UnevaluatedAccessFits(PassContext pc, TypID dst, TypID src)
    {
        if (pc.Pkg!.Types.GetByID(dst, out var destination) && pc.Pkg.Types.GetByID(src, out var accessed)
            && SameKeyAccessFits(pc, destination, accessed) is { } keyFits)
            return keyFits;

        if (destination is IndexedAccessType)
        {
            var apparent = ApparentType(pc, dst);
            return apparent == dst || IsTypeAssignable(pc, apparent, src);
        }

        if (pc.Pkg.Types.GetByID(src, out var source) && source is IndexedAccessType)
        {
            var apparent = ApparentType(pc, src);
            return apparent == src || IsTypeAssignable(pc, dst, apparent);
        }

        return null;
    }

    /// <summary>
    /// Assignability of <c>B[K]</c> to <c>A[K]</c> for the same type parameter <c>K</c> bounded by
    /// literal keys: the field of each key <c>K</c> may be fits the field of the same key, which
    /// keeps the key and the field together where a comparison of every field type would not.
    /// </summary>
    private bool? SameKeyAccessFits(PassContext pc, Type destination, Type source)
    {
        if (destination is not IndexedAccessType { Index: TypeParameterType { ExtendsBound: { } bound } key } target) return null;
        if (source is not IndexedAccessType accessed || accessed.Index.ID != key.ID) return null;
        if (LiteralKeys(GetType(pc, Evaluate(pc, bound))) is not { } keys) return null;

        return keys.All(name =>
        {
            var literal = pc.Types.LiteralOf(LiteralValue.Of(name));
            var targetField = Evaluate(pc, pc.Pkg!.Types.DeclareType(new IndexedAccessType(target.Target, literal)));
            var sourceField = Evaluate(pc, pc.Pkg.Types.DeclareType(new IndexedAccessType(accessed.Target, literal)));
            return IsTypeAssignable(pc, targetField.ID, sourceField.ID);
        });
    }

    /// <summary>
    /// <paramref name="method"/> with its own type parameters replaced by those of
    /// <paramref name="declared"/> at the same position, so a generic method is compared with
    /// the generic signature it implements as one signature, not two unrelated ones.
    /// </summary>
    private FunctionType WithTypeParametersOf(PassContext pc, FunctionType method, FunctionType declared)
    {
        var own = TypeParametersIn(method).ToList();
        var theirs = TypeParametersIn(declared).ToList();
        var bindings = new Dictionary<TypID, Type>();
        foreach (var typeParameter in own.Where(candidate => theirs.All(other => other.ID != candidate.ID)))
        {
            var match = theirs.Where(other => other.Index == typeParameter.Index && own.All(mine => mine.ID != other.ID)).ToList();
            if (match.Count == 1) bindings[typeParameter.ID] = match[0];
        }

        return bindings.Count == 0 ? method : (FunctionType)pc.Pkg!.Types.Substitute(method, bindings);
    }

    private static IEnumerable<TypeParameterType> TypeParametersIn(Type type)
    {
        IEnumerable<Type> parts = type switch
        {
            TypeParameterType typeParameter => [],
            FunctionType function => function.ParamTypes.Append(function.ReturnType),
            UnionType union => union.Types,
            TableArrayType list => [list.ElementType],
            TableMapType map => [map.KeyType, map.ValueType],
            KeyOfType keyOf => [keyOf.Target],
            IndexedAccessType indexed => [indexed.Target, indexed.Index],
            _ => [],
        };

        var found = type is TypeParameterType self ? [self] : parts.SelectMany(TypeParametersIn);
        return found.DistinctBy(typeParameter => typeParameter.ID);
    }

    /// <summary>
    /// The type an argument is reported against, worked out as far as it is known: for a type
    /// parameter bounded by literal types or <c>keyof</c>, such as <c>K extends keyof Events</c>,
    /// the keys it takes rather than its name.
    /// </summary>
    private TypID ExpectedForMessage(PassContext pc, TypID expected)
    {
        if (!pc.Pkg!.Types.GetByID(expected, out var type) || type is not TypeParameterType { ExtendsBound: { } bound }) return Evaluate(pc, expected);
        return WantsLiteral(pc, bound) ? Evaluate(pc, bound) : expected;
    }

    /// <summary>
    /// Whether a literal written where a value of <paramref name="expected"/> is wanted keeps its
    /// literal type: the expected type is a literal type, a union holding one, <c>keyof</c>, or a
    /// type parameter bounded by one of those.
    /// </summary>
    private bool WantsLiteral(PassContext pc, TypID expected)
    {
        if (!pc.Pkg!.Types.GetByID(expected, out var type)) return false;

        return type switch
        {
            LiteralType or KeyOfType => true,
            UnionType union => union.Types.Any(member => WantsLiteral(pc, member.ID)),
            TypeParameterType { ExtendsBound: { } bound } => WantsLiteral(pc, bound),
            IndexedAccessType when Evaluate(pc, type) is var evaluated && evaluated != type => WantsLiteral(pc, evaluated.ID),
            _ => false,
        };
    }

    /// <summary>
    /// The type <paramref name="value"/> takes from <paramref name="expected"/>: the literal type
    /// of a literal where literal types are wanted, or the enum of a name without a declaration
    /// that names one of its members; <c>null</c> otherwise.
    /// </summary>
    private TypID? ContextualTypeFor(PassContext pc, Expr value, TypID expected)
    {
        if (EnumMemberFor(pc, value, expected) is { } member) return member;
        if (expected == TypID.Invalid || LiteralValue.Of(value) is not { } literal || !WantsLiteral(pc, expected)) return null;
        return pc.Types.LiteralOf(literal).ID;
    }

    /// <summary>Gives the literal arguments of a call the literal types their parameters ask for.</summary>
    private void AdoptLiteralArguments(PassContext pc, FunctionType fnType, List<Expr> arguments, List<TypID> argTypes, int offset)
    {
        for (var i = 0; i < arguments.Count; i++)
        {
            var position = i + offset;
            if (position >= argTypes.Count) break;

            var parameter = position < fnType.ParamTypes.Count ? fnType.ParamTypes[position] : fnType.IsVararg ? fnType.VarargType : null;
            if (parameter != null && ContextualTypeFor(pc, arguments[i], parameter.ID) is { } literal) argTypes[position] = literal;
        }
    }

    /// <summary>
    /// <paramref name="fnType"/> with the parameters whose type depends on a type parameter
    /// through <c>keyof</c> or an indexed access, such as <c>callback: Events[K]</c>, filled in
    /// from the arguments, so each argument is checked against the field its key names.
    /// </summary>
    private FunctionType WithDependentParameters(PassContext pc, FunctionType fnType, List<TypID> argTypes)
    {
        if (!fnType.ParamTypes.Any(IsDependent)) return fnType;

        var bindings = new Dictionary<TypID, Type>();
        for (var i = 0; i < argTypes.Count && i < fnType.ParamTypes.Count; i++)
        {
            if (!IsDependent(fnType.ParamTypes[i]) && pc.Pkg!.Types.GetByID(argTypes[i], out var argument))
                Bind(pc, fnType.ParamTypes[i], argument, bindings);
        }

        if (bindings.Count == 0) return fnType;

        var parameters = fnType.ParamTypes
            .Select(parameter => IsDependent(parameter) ? Evaluate(pc, pc.Pkg!.Types.Substitute(parameter, bindings)) : parameter)
            .ToList();
        return (FunctionType)pc.Pkg!.Types.DeclareType(new FunctionType(parameters, fnType.ParamNames, fnType.ReturnType,
            fnType.IsVararg, fnType.VarargType, fnType.DefaultParams, fnType.IsAsync));
    }

    private static bool IsDependent(Type type)
    {
        return type switch
        {
            KeyOfType or IndexedAccessType => ContainsTypeParameter(type),
            UnionType union => union.Types.Any(IsDependent),
            FunctionType function => function.ParamTypes.Any(IsDependent) || IsDependent(function.ReturnType),
            _ => false,
        };
    }

    /// <summary>
    /// The type of <c>value["name"]</c> on a struct, class or interface: the type of the field the
    /// literal key names, as <c>events["user:login"]</c> reads a field whose name is no identifier.
    /// <c>null</c> when <paramref name="baseType"/> has no fields or the key is no literal.
    /// </summary>
    private TypID? FieldByKey(PassContext pc, IndexAccessExpr idx, Type baseType, TypID indexType)
    {
        if (Type.FieldNamesOf(baseType) is not { } names) return null;

        if (pc.Pkg!.Types.GetByID(indexType, out var index) && index is TypeParameterType { ExtendsBound: not null })
            return pc.Pkg.Types.DeclareType(new IndexedAccessType(baseType, index)).ID;

        var keys = idx.Index is StringLiteralExpr literal ? [literal.Value] : LiteralKeys(GetType(pc, Evaluate(pc, indexType)));
        if (keys == null) return null;

        var missing = keys.FirstOrDefault(key => !names.Contains(key));
        if (missing != null)
        {
            pc.Diag.Report(idx.Index.Span, DiagnosticCode.ErrNoSuchKey, TypeName(pc, baseType.ID), LiteralValue.Of(missing).Text,
                string.Join(", ", names.Select(name => LiteralValue.Of(name).Text)));
            return pc.Types.PrimAny.ID;
        }

        var keyType = keys.Count == 1
            ? pc.Types.LiteralOf(LiteralValue.Of(keys[0]))
            : GetType(pc, pc.Types.UnionOf(keys.Select(key => (Type)pc.Types.LiteralOf(LiteralValue.Of(key)))));
        return Evaluate(pc, pc.Pkg.Types.DeclareType(new IndexedAccessType(baseType, keyType)).ID);
    }

    /// <summary>
    /// Assignability involving literal types: a literal fits the same literal and whatever its
    /// base type fits, and only a literal fits a literal type. <c>null</c> where neither side is
    /// a literal, or a union is involved, which is checked member by member.
    /// </summary>
    private bool? LiteralAssignable(PassContext pc, Type destination, Type source)
    {
        if (destination is UnionType || source is UnionType) return null;
        if (source is LiteralType sourceLiteral)
            return destination is LiteralType ? destination.ID == source.ID : IsTypeAssignable(pc, destination.ID, sourceLiteral.BaseType.ID);
        if (destination is LiteralType) return false;
        return null;
    }

    /// <summary>
    /// The types of the values of a <c>return</c>, where a literal keeps the literal type the
    /// declared return type asks for.
    /// </summary>
    private void AdoptReturnedLiterals(PassContext pc, List<Expr> values)
    {
        if (_declaredReturn == TypID.Invalid || !pc.Pkg!.Types.GetByID(_declaredReturn, out var declared)) return;

        for (var i = 0; i < values.Count; i++)
        {
            var expected = declared is TupleType tuple
                ? i < tuple.Fields.Count ? tuple.Fields[i].Type.ID : TypID.Invalid
                : i == 0 ? declared.ID : TypID.Invalid;
            if (ContextualTypeFor(pc, values[i], expected) is { } literal) values[i].Type = literal;
        }
    }
}
