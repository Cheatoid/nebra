using Nebra.IR;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Type = Nebra.IR.Type;

namespace Nebra.LPS.Handlers;

/// <summary>
/// Jumps from a value to the declaration of its type: from a variable to its class, from a call
/// to the type the function returns, from a field to the type it holds.
/// </summary>
public sealed class TypeDefinitionHandler(NebraWorkspace workspace) : TypeDefinitionHandlerBase
{
    public override async Task<LocationOrLocationLinks?> Handle(TypeDefinitionParams request, CancellationToken ct)
    {
        var result = await workspace.GetResultAsync(request.TextDocument.Uri.ToString(), ct);
        if (result == null) return null;

        var target = CursorTarget.At(result, request.Position.Line + 1, request.Position.Character + 1);
        if (target == null) return null;

        var type = target.IsMember ? MemberType(result, target) : SymbolType(result, target.Symbol);
        var declaration = type == null ? null : NamedType(type)?.DeclaredAt;
        if (declaration == null || string.IsNullOrEmpty(declaration.Span.File) || !File.Exists(declaration.Span.File))
            return null;

        return new LocationOrLocationLinks(new Location
        {
            Uri = DocumentUri.FromFileSystemPath(declaration.Span.File),
            Range = NebraWorkspace.SpanToRange(declaration.Span)
        });
    }

    private static Type? SymbolType(AnalysisResult result, SymID symbol)
    {
        if (!result.Snapshot.TryGetSymbol(symbol, out var declared)) return null;
        return result.Types.GetByID(declared.Type, out var type) ? type : null;
    }

    private static Type? MemberType(AnalysisResult result, CursorTarget target)
    {
        var access = MemberResolver.FindAccess(
            NodeFinder.Find(result.Hir, target.Name.Span.StartLn, target.Name.Span.StartCol), target.Name);
        if (access == null || !result.Types.GetByID(access.Receiver.Type, out var receiver)) return null;
        return MemberResolver.FindType(receiver, target.Name.Name);
    }

    /// <summary>
    /// The class, interface or enum <paramref name="type"/> refers to, looking through optional
    /// types, arrays and - for a function - its return type.
    /// </summary>
    private static Type? NamedType(Type type)
    {
        return type switch
        {
            { DeclaredAt: not null } => type,
            UnionType union => union.Types.Where(member => member.Kind != TypeKind.PrimitiveNil).Select(NamedType).FirstOrDefault(found => found != null),
            TableArrayType array => NamedType(array.ElementType),
            FunctionType function => NamedType(function.ReturnType),
            _ => null
        };
    }

    protected override TypeDefinitionRegistrationOptions CreateRegistrationOptions(
        TypeDefinitionCapability capability, ClientCapabilities clientCapabilities)
    {
        return new TypeDefinitionRegistrationOptions
        {
            DocumentSelector = TextDocumentSelector.ForLanguage("nebra")
        };
    }
}
