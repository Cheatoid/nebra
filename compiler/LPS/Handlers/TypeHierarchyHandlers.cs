using Nebra.IR;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Type = Nebra.IR.Type;

using LspSymbolKind = OmniSharp.Extensions.LanguageServer.Protocol.Models.SymbolKind;

namespace Nebra.LPS.Handlers;

/// <summary>
/// Turns classes and interfaces into type hierarchy items, finds them again from an item, and
/// relates them: a class's supertypes are its base class and the interfaces it lists, an
/// interface's are the interfaces it extends; subtypes are the reverse.
/// </summary>
internal static class TypeHierarchyItems
{
    public static TypeHierarchyItem? From(Type type)
    {
        var declaration = type.DeclaredAt;
        if (declaration?.Span.File is not { } file || !File.Exists(file)) return null;

        var range = NebraWorkspace.SpanToRange(declaration.Span);
        return new TypeHierarchyItem
        {
            Name = declaration.Name,
            Kind = type is InterfaceType ? LspSymbolKind.Interface : LspSymbolKind.Class,
            Uri = DocumentUri.FromFileSystemPath(file),
            Range = range,
            SelectionRange = range
        };
    }

    public static Type? Find(IEnumerable<Type> types, TypeHierarchyItem item)
    {
        var path = item.Uri.GetFileSystemPath();
        if (path == null) return null;

        var full = Path.GetFullPath(path);
        return types.FirstOrDefault(type => type is ClassType or InterfaceType
            && type.DeclaredAt?.Span.File is { } file
            && string.Equals(Path.GetFullPath(file), full, StringComparison.OrdinalIgnoreCase)
            && type.DeclaredAt.Span.StartLn - 1 == item.SelectionRange.Start.Line
            && type.DeclaredAt.Span.StartCol - 1 == item.SelectionRange.Start.Character);
    }

    public static IEnumerable<Type> Supertypes(Type type)
    {
        return type switch
        {
            ClassType classType => (classType.BaseClass == null ? [] : new Type[] { classType.BaseClass }).Concat(classType.Interfaces),
            InterfaceType interfaceType => interfaceType.BaseInterfaces,
            _ => []
        };
    }

    public static IEnumerable<Type> Subtypes(IEnumerable<Type> types, Type type)
    {
        return types.Where(candidate => !ReferenceEquals(candidate, type) && Supertypes(candidate).Contains(type));
    }

    public static Container<TypeHierarchyItem> Items(IEnumerable<Type> types)
    {
        return new Container<TypeHierarchyItem>(types.Distinct().Select(From).OfType<TypeHierarchyItem>());
    }
}

public sealed class TypeHierarchyPrepareHandler(NebraWorkspace workspace) : TypeHierarchyPrepareHandlerBase
{
    public override async Task<Container<TypeHierarchyItem>?> Handle(TypeHierarchyPrepareParams request, CancellationToken ct)
    {
        var result = await workspace.GetResultAsync(request.TextDocument.Uri.ToString(), ct);
        if (result == null) return null;

        var target = CursorTarget.At(result, request.Position.Line + 1, request.Position.Character + 1);
        if (target == null || target.IsMember || !result.Snapshot.TryGetSymbol(target.Symbol, out var symbol)) return null;
        if (!result.Types.GetByID(symbol.Type, out var type) || type is not (ClassType or InterfaceType)) return null;

        var item = TypeHierarchyItems.From(type);
        return item == null ? null : new Container<TypeHierarchyItem>(item);
    }

    protected override TypeHierarchyRegistrationOptions CreateRegistrationOptions(
        TypeHierarchyCapability capability, ClientCapabilities clientCapabilities)
    {
        return new TypeHierarchyRegistrationOptions
        {
            DocumentSelector = TextDocumentSelector.ForLanguage("nebra")
        };
    }
}

public sealed class TypeHierarchySupertypesHandler(NebraWorkspace workspace) : TypeHierarchySupertypesHandlerBase
{
    public override async Task<Container<TypeHierarchyItem>?> Handle(TypeHierarchySupertypesParams request, CancellationToken ct)
    {
        var snapshot = await workspace.GetSnapshotAsync(request.Item.Uri.ToString(), ct);
        var types = snapshot.Types.All.ToList();
        var type = TypeHierarchyItems.Find(types, request.Item);
        return type == null ? null : TypeHierarchyItems.Items(TypeHierarchyItems.Supertypes(type));
    }
}

public sealed class TypeHierarchySubtypesHandler(NebraWorkspace workspace) : TypeHierarchySubtypesHandlerBase
{
    public override async Task<Container<TypeHierarchyItem>?> Handle(TypeHierarchySubtypesParams request, CancellationToken ct)
    {
        var snapshot = await workspace.GetSnapshotAsync(request.Item.Uri.ToString(), ct);
        var types = snapshot.Types.All.ToList();
        var type = TypeHierarchyItems.Find(types, request.Item);
        return type == null ? null : TypeHierarchyItems.Items(TypeHierarchyItems.Subtypes(types, type));
    }
}
