using Nebra.IR;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Type = Nebra.IR.Type;

namespace Nebra.LPS.Handlers;

/// <summary>
/// Lists what implements the type or member under the cursor: the classes implementing an
/// interface or extending a class, or the methods in those classes that implement or override a
/// method. Needs every type of the workspace, so it works on the workspace snapshot.
/// </summary>
public sealed class ImplementationHandler(NebraWorkspace workspace) : ImplementationHandlerBase
{
    public override async Task<LocationOrLocationLinks?> Handle(ImplementationParams request, CancellationToken ct)
    {
        var result = await workspace.GetWorkspaceResultAsync(request.TextDocument.Uri.ToString(), ct);
        if (result == null) return null;

        var target = CursorTarget.At(result, request.Position.Line + 1, request.Position.Character + 1);
        if (target == null) return null;

        var types = result.Types.All.ToList();
        var declarations = target.IsMember
            ? MemberImplementations(types, target.Name.Name, target.MemberDeclaration!)
            : TypeImplementations(types, TargetType(result, target.Symbol));

        var locations = declarations
            .Where(declaration => !string.IsNullOrEmpty(declaration.Span.File) && File.Exists(declaration.Span.File))
            .Select(declaration => new LocationOrLocationLink(new Location
            {
                Uri = DocumentUri.FromFileSystemPath(declaration.Span.File!),
                Range = NebraWorkspace.SpanToRange(declaration.Span)
            }))
            .ToList();

        return new LocationOrLocationLinks(locations);
    }

    private static Type? TargetType(AnalysisResult result, SymID symbol)
    {
        if (!result.Snapshot.TryGetSymbol(symbol, out var declared)) return null;
        if (!result.Types.GetByID(declared.Type, out var type)) return null;

        return type is UnionType union
            ? union.Types.FirstOrDefault(member => member.Kind != TypeKind.PrimitiveNil)
            : type;
    }

    private static IEnumerable<NameRef> TypeImplementations(List<Type> types, Type? type)
    {
        if (type is not (ClassType or InterfaceType)) return [];

        return Implementors(types, type)
            .Select(implementor => implementor.DeclaredAt)
            .OfType<NameRef>();
    }

    private static IEnumerable<NameRef> MemberImplementations(List<Type> types, string memberName, NameRef declaration)
    {
        var owner = types.FirstOrDefault(type => Declares(type, memberName, declaration));
        if (owner == null) return [];

        return Implementors(types, owner)
            .Select(implementor => implementor.MemberDeclarations.GetValueOrDefault(memberName))
            .OfType<NameRef>();
    }

    private static bool Declares(Type type, string memberName, NameRef declaration)
    {
        var members = type switch
        {
            ClassType classType => classType.MemberDeclarations,
            InterfaceType interfaceType => interfaceType.MemberDeclarations,
            _ => null
        };

        return members != null && members.TryGetValue(memberName, out var declared) && ReferenceEquals(declared, declaration);
    }

    /// <summary>The classes implementing <paramref name="type"/> when it is an interface, or extending it when it is a class.</summary>
    private static IEnumerable<ClassType> Implementors(List<Type> types, Type type)
    {
        foreach (var candidate in types.OfType<ClassType>())
        {
            if (ReferenceEquals(candidate, type)) continue;

            var implements = type switch
            {
                InterfaceType interfaceType => Type.ImplementedInterfaces(candidate).Contains(interfaceType),
                ClassType classType => Extends(candidate, classType),
                _ => false
            };

            if (implements) yield return candidate;
        }
    }

    private static bool Extends(ClassType candidate, ClassType baseClass)
    {
        var visited = new HashSet<ClassType>();
        for (var current = candidate.BaseClass; current != null && visited.Add(current); current = current.BaseClass)
        {
            if (ReferenceEquals(current, baseClass)) return true;
        }

        return false;
    }

    protected override ImplementationRegistrationOptions CreateRegistrationOptions(
        ImplementationCapability capability, ClientCapabilities clientCapabilities)
    {
        return new ImplementationRegistrationOptions
        {
            DocumentSelector = TextDocumentSelector.ForLanguage("nebra")
        };
    }
}
