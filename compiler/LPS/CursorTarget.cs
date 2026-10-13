using Nebra.IR;

namespace Nebra.LPS;

/// <summary>
/// What the cursor points at: a name bound to a symbol, or a member - a field, method or
/// accessor - identified by the name it was declared under.
/// </summary>
public sealed record CursorTarget(NameRef Name, SymID Symbol, NameRef? MemberDeclaration)
{
    public bool IsMember => MemberDeclaration != null;

    /// <summary>Finds the target at a one-based <paramref name="line"/> and <paramref name="col"/>.</summary>
    public static CursorTarget? At(AnalysisResult result, int line, int col)
    {
        var nameRef = NodeFinder.FindNameRef(result.Hir, line, col);
        if (nameRef == null)
        {
            return null;
        }

        if (nameRef.Sym != SymID.Invalid)
        {
            return new CursorTarget(nameRef, nameRef.Sym, null);
        }

        var declaration = MemberDeclarationOf(result, nameRef);
        return declaration == null ? null : new CursorTarget(nameRef, SymID.Invalid, declaration);
    }

    /// <summary>
    /// The declaration of the member <paramref name="nameRef"/> names: itself when it is the name
    /// in a member declaration, otherwise the member its access resolves to through the
    /// receiver's type.
    /// </summary>
    public static NameRef? MemberDeclarationOf(AnalysisResult result, NameRef nameRef)
    {
        if (IsMemberDeclaration(result.Hir, nameRef))
        {
            return nameRef;
        }

        var access = MemberResolver.FindAccess(NodeFinder.Find(result.Hir, nameRef.Span.StartLn, nameRef.Span.StartCol), nameRef);
        if (access == null || access.Receiver.Type == TypID.Invalid)
        {
            return null;
        }

        return result.Types.GetByID(access.Receiver.Type, out var receiverType)
            ? MemberResolver.FindDeclaration(receiverType, access.Member.Name)
            : null;
    }

    private static bool IsMemberDeclaration(IRScript script, NameRef nameRef)
    {
        return DeclaredMemberNames(script.Body).Any(declared => ReferenceEquals(declared, nameRef));
    }

    private static IEnumerable<NameRef> DeclaredMemberNames(IEnumerable<Stmt> statements)
    {
        foreach (var statement in statements)
        {
            var declaration = statement is ExportStmt export ? export.Declaration : statement;
            switch (declaration)
            {
                case ClassDecl classDecl:
                    foreach (var field in classDecl.Fields) yield return field.Name;
                    foreach (var method in classDecl.Methods) yield return method.Name;
                    foreach (var accessor in classDecl.Accessors) yield return accessor.Name;
                    break;
                case InterfaceDecl interfaceDecl:
                    foreach (var field in interfaceDecl.Fields) yield return field.Name;
                    foreach (var method in interfaceDecl.Methods) yield return method.Name;
                    break;
                case ExtendDecl extendDecl:
                    foreach (var method in extendDecl.Methods) yield return method.Name;
                    break;
                case DeclareModuleDecl module:
                    foreach (var member in DeclaredMemberNames(module.Members)) yield return member;
                    break;
            }
        }
    }
}
