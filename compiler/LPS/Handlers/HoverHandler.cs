using Nebra.IR;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;

using Type = Nebra.IR.Type;

namespace Nebra.LPS.Handlers;

/// <summary>
/// Shows what the name under the cursor is: a member where it is declared or used, a symbol, an
/// annotation, or failing those the type of the expression. <see cref="HoverRenderer"/> writes
/// the content.
/// </summary>
public sealed class HoverHandler(NebraWorkspace workspace) : HoverHandlerBase
{
    public override async Task<Hover?> Handle(HoverParams request, CancellationToken ct)
    {
        var uri = request.TextDocument.Uri.ToString();
        var result = await workspace.GetResultAsync(uri, ct);
        if (result == null) return null;

        var line = request.Position.Line + 1;
        var col = request.Position.Character + 1;
        var renderer = new HoverRenderer(result, workspace);

        var declaredMember = MemberDeclarationAt(result, line, col);
        if (declaredMember != null)
        {
            return Create(renderer.ForMemberDeclaration(declaredMember.Value.Owner, declaredMember.Value.Name.Name), declaredMember.Value.Name.Span);
        }

        var hoveredNode = NodeFinder.Find(result.Hir, line, col);
        var nameRef = NodeFinder.FindNameRef(result.Hir, line, col);
        if (nameRef != null && result.Syms.GetByID(nameRef.Sym, out var symbol))
        {
            var effectiveType = hoveredNode is NameExpr name && name.Name == nameRef && name.Type != TypID.Invalid ? name.Type : symbol.Type;
            return Create(renderer.ForSymbol(symbol, effectiveType), nameRef.Span);
        }

        var memberHover = nameRef == null ? null : MemberHover(result, renderer, hoveredNode, nameRef);
        if (memberHover != null) return memberHover;

        var annotation = FindAnnotationAt(result.Hir, line, col);
        if (annotation != null)
        {
            var info = workspace.GetAnnotationInfo(uri, annotation.Name.Name) ?? $"@{annotation.Name.Name}";
            return Create($"```nebra\n{info}\n```", annotation.Span);
        }

        if (hoveredNode is Expr expr && expr.Type != TypID.Invalid)
        {
            return Create(renderer.ForExpression(expr.Type), expr.Span);
        }

        return null;
    }

    private static Hover? MemberHover(AnalysisResult result, HoverRenderer renderer, Node? hoveredNode, NameRef nameRef)
    {
        var access = MemberResolver.FindAccess(hoveredNode, nameRef);
        if (access == null || !result.Types.GetByID(access.Receiver.Type, out var receiverType)) return null;

        var content = renderer.ForMember(receiverType, access.Member.Name);
        return content == null ? null : Create(content, nameRef.Span);
    }

    /// <summary>
    /// The member whose name is declared at the position, in a class, interface or enum
    /// declaration, together with the type that declares it.
    /// </summary>
    private static (Type Owner, NameRef Name)? MemberDeclarationAt(AnalysisResult result, int line, int col)
    {
        foreach (var declaration in Declarations(result.Hir.Body))
        {
            var declared = MembersOf(declaration);
            var member = declared?.Members.FirstOrDefault(candidate => Contains(candidate.Span, line, col));
            if (declared == null || member == null) continue;
            if (!result.Syms.GetByID(declared.Owner.Sym, out var owner) || !result.Types.GetByID(owner.Type, out var ownerType)) continue;

            return (ownerType, member);
        }

        return null;
    }

    /// <summary>The name of a type declaration and the names of the members it declares.</summary>
    private sealed record DeclaredMembers(NameRef Owner, IEnumerable<NameRef> Members);

    private static DeclaredMembers? MembersOf(Decl declaration)
    {
        return declaration switch
        {
            ClassDecl classDecl => new DeclaredMembers(classDecl.Name, classDecl.Fields.Select(field => field.Name)
                .Concat(classDecl.Methods.Select(method => method.Name))
                .Concat(classDecl.Accessors.Select(accessor => accessor.Name))),
            InterfaceDecl interfaceDecl => new DeclaredMembers(interfaceDecl.Name, interfaceDecl.Fields.Select(field => field.Name)
                .Concat(interfaceDecl.Methods.Select(method => method.Name))),
            EnumDecl enumDecl => new DeclaredMembers(enumDecl.Name, enumDecl.Members.Select(member => member.Name)),
            _ => null
        };
    }

    private static IEnumerable<Decl> Declarations(IEnumerable<Stmt> statements)
    {
        foreach (var statement in statements)
        {
            switch (statement)
            {
                case ExportStmt export:
                    yield return export.Declaration;
                    break;
                case DeclareModuleDecl module:
                    foreach (var member in Declarations(module.Members))
                    {
                        yield return member;
                    }
                    break;
                case Decl declaration:
                    yield return declaration;
                    break;
            }
        }
    }

    private static bool Contains(Diagnostics.TextSpan span, int line, int col)
    {
        return span.StartLn == line && span.StartCol <= col && col < span.EndCol;
    }

    private static Hover Create(string markdown, Diagnostics.TextSpan span)
    {
        return new Hover
        {
            Contents = new MarkedStringsOrMarkupContent(new MarkupContent { Kind = MarkupKind.Markdown, Value = markdown }),
            Range = NebraWorkspace.SpanToRange(span)
        };
    }

    private static Annotation? FindAnnotationAt(IRScript hir, int line, int col)
    {
        Annotation? Check(List<Annotation> anns)
        {
            foreach (var a in anns)
            {
                if (a.Span.StartLn <= line && a.Span.EndLn >= line
                    && a.Span.StartCol <= col && a.Span.EndCol >= col)
                    return a;
            }
            return null;
        }

        foreach (var stmt in hir.Body)
        {
            var decl = stmt switch
            {
                ExportStmt ex => ex.Declaration,
                Decl d => d,
                _ => null,
            };
            if (decl == null) continue;

            Annotation? found = decl switch
            {
                FunctionDecl fd => Check(fd.Annotations),
                LocalFunctionDecl lfd => Check(lfd.Annotations),
                LocalDecl ld => Check(ld.Annotations),
                ClassDecl cd => Check(cd.Annotations),
                EnumDecl ed => Check(ed.Annotations),
                InterfaceDecl id => Check(id.Annotations),
                _ => null,
            };
            if (found != null) return found;

            if (decl is ClassDecl cls)
            {
                foreach (var f in cls.Fields) { found = Check(f.Annotations); if (found != null) return found; }
                foreach (var m in cls.Methods) { found = Check(m.Annotations); if (found != null) return found; }
            }
            if (decl is EnumDecl en)
            {
                foreach (var m in en.Members) { found = Check(m.Annotations); if (found != null) return found; }
            }
            if (decl is InterfaceDecl iface)
            {
                foreach (var f in iface.Fields) { found = Check(f.Annotations); if (found != null) return found; }
                foreach (var m in iface.Methods) { found = Check(m.Annotations); if (found != null) return found; }
            }
        }
        return null;
    }

    protected override HoverRegistrationOptions CreateRegistrationOptions(
        HoverCapability capability, ClientCapabilities clientCapabilities)
    {
        return new HoverRegistrationOptions
        {
            DocumentSelector = TextDocumentSelector.ForLanguage("nebra")
        };
    }
}
