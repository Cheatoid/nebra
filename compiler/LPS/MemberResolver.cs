using Nebra.IR;
using Type = Nebra.IR.Type;

namespace Nebra.LPS;

/// <summary>A member access the cursor sits on: the receiver expression and the member name.</summary>
public sealed record MemberAccess(Expr Receiver, NameRef Member);

/// <summary>
/// Resolves member accesses (<c>obj.field</c>, <c>obj:method()</c>) to the declaration of the
/// member, following base classes, implemented and extended interfaces, and extension blocks.
/// </summary>
public static class MemberResolver
{
    /// <summary>
    /// Returns the member access <paramref name="nameRef"/> names inside <paramref name="node"/>,
    /// or null when the name is not the member part of an access.
    /// </summary>
    public static MemberAccess? FindAccess(Node? node, NameRef nameRef)
    {
        if (node is ExprStmt statement)
        {
            node = statement.Expression;
        }

        return node switch
        {
            MethodCallExpr call when call.MethodName == nameRef => new MemberAccess(call.Object, call.MethodName),
            DotAccessExpr dot when dot.FieldName == nameRef => new MemberAccess(dot.Object, dot.FieldName),
            FunctionCallExpr { Callee: DotAccessExpr callee } when callee.FieldName == nameRef =>
                new MemberAccess(callee.Object, callee.FieldName),
            _ => null
        };
    }

    /// <summary>
    /// Returns the name the member <paramref name="memberName"/> of <paramref name="receiverType"/>
    /// was declared under, or null when the type has no such member or it has no source.
    /// </summary>
    public static NameRef? FindDeclaration(Type receiverType, string memberName)
    {
        foreach (var owner in MemberOwners(receiverType))
        {
            if (owner is ClassType classType && classType.MemberDeclarations.TryGetValue(memberName, out var classMember))
            {
                return classMember;
            }

            if (owner is InterfaceType interfaceType && interfaceType.MemberDeclarations.TryGetValue(memberName, out var interfaceMember))
            {
                return interfaceMember;
            }

            if (owner.ExtensionMethodNodes.TryGetValue(memberName, out var extension))
            {
                return extension.Name;
            }
        }

        return null;
    }

    /// <summary>
    /// Returns the type a use of the member <paramref name="memberName"/> of
    /// <paramref name="receiverType"/> produces: a field's type, a getter's result, a method's
    /// return type. Null when the type has no such member.
    /// </summary>
    public static Type? FindType(Type receiverType, string memberName)
    {
        foreach (var owner in MemberOwners(receiverType))
        {
            switch (owner)
            {
                case ClassType classType when classType.InstanceFields.TryGetValue(memberName, out var field):
                    return field.Type;
                case ClassType classType when classType.Getters.TryGetValue(memberName, out var getter):
                    return getter.ReturnType;
                case ClassType classType when classType.Methods.TryGetValue(memberName, out var method):
                    return method.ReturnType;
                case ClassType classType when classType.StaticMethods.TryGetValue(memberName, out var staticMethod):
                    return staticMethod.ReturnType;
                case InterfaceType interfaceType when interfaceType.Fields.TryGetValue(memberName, out var interfaceField):
                    return interfaceField.Type;
                case InterfaceType interfaceType when interfaceType.Methods.TryGetValue(memberName, out var interfaceMethod):
                    return interfaceMethod.ReturnType;
            }

            if (owner.ExtensionMethods.TryGetValue(memberName, out var extension))
            {
                return extension.ReturnType;
            }
        }

        return null;
    }

    /// <summary>
    /// Returns the signature of the method <paramref name="memberName"/> of
    /// <paramref name="receiverType"/>, or null when it has no such method.
    /// </summary>
    public static FunctionType? FindMethod(Type receiverType, string memberName)
    {
        foreach (var owner in MemberOwners(receiverType))
        {
            switch (owner)
            {
                case ClassType classType when classType.Methods.TryGetValue(memberName, out var method):
                    return method;
                case ClassType classType when classType.StaticMethods.TryGetValue(memberName, out var staticMethod):
                    return staticMethod;
                case InterfaceType interfaceType when interfaceType.Methods.TryGetValue(memberName, out var interfaceMethod):
                    return interfaceMethod;
            }

            if (owner.ExtensionMethods.TryGetValue(memberName, out var extension))
            {
                return extension;
            }
        }

        return null;
    }

    /// <summary>
    /// Enumerates the types that can contribute a member to <paramref name="type"/>, nearest
    /// first: the type itself, its base classes, then every interface it implements or extends.
    /// </summary>
    private static IEnumerable<Type> MemberOwners(Type type)
    {
        var target = WithoutNil(type);

        if (target is ClassType classType)
        {
            for (var current = classType; current != null; current = current.BaseClass)
            {
                yield return current;
            }

            foreach (var implemented in Type.ImplementedInterfaces(classType))
            {
                yield return implemented;
            }

            yield break;
        }

        if (target is InterfaceType interfaceType)
        {
            yield return interfaceType;

            foreach (var extended in Type.BaseInterfacesOf(interfaceType))
            {
                yield return extended;
            }

            yield break;
        }

        yield return target;
    }

    private static Type WithoutNil(Type type)
    {
        if (type is not UnionType union)
        {
            return type;
        }

        var nonNil = union.Types.Where(member => member.Kind != TypeKind.PrimitiveNil).ToList();
        return nonNil.Count == 1 ? WithoutNil(nonNil[0]) : type;
    }
}
