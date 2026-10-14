using Nebra.Compiler.Annotations;
using Nebra.Configuration;
using Nebra.IR;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;

using Type = Nebra.IR.Type;

namespace Nebra.LPS;

/// <summary>Where a marker sits, which side it shows and whether it marks a declaration or a use.</summary>
public sealed record SideMarker(Position Position, string Side, string Kind);

/// <summary>The parameters of the <c>nebra/sideMarkers</c> request: the document to mark.</summary>
public sealed class SideMarkersParams
{
    public TextDocumentIdentifier TextDocument { get; set; } = null!;
}

/// <summary>
/// Finds where code runs: a marker on every declaration of a file - functions, classes, members,
/// enums, declared APIs - with the side it runs on, and on every use of something restricted to
/// a side. A declaration takes the side of its own <c>@side</c>, else that of the declaration
/// around it, else the file's side from <c>[sides]</c>; a use shows only an explicit restriction.
/// </summary>
public static class SideMarkers
{
    public const string Client = "client";
    public const string Server = "server";
    public const string Shared = "shared";

    public const string Method = "nebra/sideMarkers";

    public static async Task<List<SideMarker>> HandleAsync(NebraWorkspace workspace, SideMarkersParams request, CancellationToken ct)
    {
        var result = await workspace.GetResultAsync(request.TextDocument.Uri.ToString(), ct);
        return result == null ? [] : Compute(result);
    }

    public static List<SideMarker> Compute(AnalysisResult result)
    {
        var config = result.Snapshot.Config;
        var fileSide = SidesResolver.ResolveFileSide(config.Sides, result.FilePath, config.ProjectRoot);
        var markers = new List<SideMarker>();

        foreach (var statement in result.Hir.Body)
        {
            AddDeclaration(markers, statement, fileSide);
        }

        foreach (var node in NodeFinder.BuildNodeRegistry(result.Hir).Values)
        {
            AddUse(result, markers, node);
        }

        return markers
            .GroupBy(marker => (marker.Position.Line, marker.Position.Character))
            .Select(group => group.First())
            .OrderBy(marker => marker.Position.Line)
            .ThenBy(marker => marker.Position.Character)
            .ToList();
    }

    /// <summary>The marker name of <paramref name="side"/>, or null when it runs everywhere.</summary>
    public static string? NameOf(Side side)
    {
        if (side == Side.All || side == Side.None) return null;

        var client = side.HasFlag(Side.Client);
        var server = side.HasFlag(Side.Server);
        if (client && !server) return Client;
        if (server && !client) return Server;
        return Shared;
    }

    private static void AddDeclaration(List<SideMarker> markers, Stmt statement, Side enclosing)
    {
        switch (statement)
        {
            case ExportStmt export:
                AddDeclaration(markers, export.Declaration, enclosing);
                break;
            case FunctionDecl function:
                Add(markers, function.MethodName ?? function.NamePath[^1], Resolve(function.Annotations, enclosing));
                break;
            case LocalFunctionDecl function:
                Add(markers, function.Name, Resolve(function.Annotations, enclosing));
                break;
            case DeclareFunctionDecl function:
                Add(markers, function.MethodName ?? function.NamePath[^1], Resolve(function.Annotations, enclosing));
                break;
            case DeclareVariableDecl variable:
                Add(markers, variable.Name, Resolve(variable.Annotations, enclosing));
                break;
            case EnumDecl enumDecl:
                Add(markers, enumDecl.Name, Resolve(enumDecl.Annotations, enclosing));
                break;
            case DeclareModuleDecl module:
                AddModule(markers, module, Resolve(module.Annotations, enclosing));
                break;
            case ClassDecl classDecl:
                AddClass(markers, classDecl, Resolve(classDecl.Annotations, enclosing));
                break;
            case InterfaceDecl interfaceDecl:
                AddInterface(markers, interfaceDecl, Resolve(interfaceDecl.Annotations, enclosing));
                break;
            case ExtendDecl extendDecl:
                AddExtension(markers, extendDecl, Resolve(extendDecl.Annotations, enclosing));
                break;
        }
    }

    private static void AddModule(List<SideMarker> markers, DeclareModuleDecl module, Side side)
    {
        foreach (var member in module.Members)
        {
            AddDeclaration(markers, member, side);
        }
    }

    private static void AddClass(List<SideMarker> markers, ClassDecl classDecl, Side side)
    {
        Add(markers, classDecl.Name, side);

        foreach (var field in classDecl.Fields.Where(field => !field.IsLocal))
        {
            Add(markers, field.Name, Resolve(field.Annotations, side));
        }

        foreach (var method in classDecl.Methods.Where(method => !method.IsLocal))
        {
            Add(markers, method.Name, Resolve(method.Annotations, side));
        }

        foreach (var accessor in classDecl.Accessors)
        {
            Add(markers, accessor.Name, Resolve(accessor.Annotations, side));
        }
    }

    private static void AddInterface(List<SideMarker> markers, InterfaceDecl interfaceDecl, Side side)
    {
        Add(markers, interfaceDecl.Name, side);

        foreach (var field in interfaceDecl.Fields)
        {
            Add(markers, field.Name, Resolve(field.Annotations, side));
        }

        foreach (var method in interfaceDecl.Methods)
        {
            Add(markers, method.Name, Resolve(method.Annotations, side));
        }
    }

    private static void AddExtension(List<SideMarker> markers, ExtendDecl extendDecl, Side side)
    {
        foreach (var method in extendDecl.Methods)
        {
            Add(markers, method.Name, Resolve(method.Annotations, side));
        }
    }

    /// <summary>
    /// Marks a use of a name or member that is restricted to a side: a name bound to a symbol
    /// declared with <c>@side</c>, or a member whose declaration or owning type carries one.
    /// </summary>
    private static void AddUse(AnalysisResult result, List<SideMarker> markers, Node node)
    {
        switch (node)
        {
            case NameExpr name when result.Syms.GetByID(name.Name.Sym, out var symbol):
                Add(markers, name.Name, symbol.Side, "reference");
                break;
            case NewExpr creation when result.Syms.GetByID(creation.ClassName.Sym, out var classSymbol):
                Add(markers, creation.ClassName, classSymbol.Side, "reference");
                break;
            case DotAccessExpr access:
                AddMemberUse(result, markers, access.Object, access.FieldName);
                break;
            case MethodCallExpr call:
                AddMemberUse(result, markers, call.Object, call.MethodName);
                break;
        }
    }

    private static void AddMemberUse(AnalysisResult result, List<SideMarker> markers, Expr receiver, NameRef member)
    {
        if (!result.Types.GetByID(receiver.Type, out var receiverType)) return;

        var owner = MemberResolver.FindOwner(receiverType, member.Name);
        if (owner == null) return;

        Add(markers, member, MemberSide(result, owner, member.Name), "reference");
    }

    /// <summary>The side <paramref name="member"/> of <paramref name="owner"/> is annotated with, or that of the owner itself.</summary>
    public static Side MemberSide(AnalysisResult result, Type owner, string member)
    {
        var side = MemberSide(owner, member);
        return side == Side.All ? TypeSide(result, owner) : side;
    }

    private static Side MemberSide(Type owner, string member)
    {
        var tables = owner switch
        {
            ClassType classType => new[] { classType.MethodSides, classType.StaticMethodSides, classType.FieldSides, classType.GetterSides, classType.SetterSides },
            InterfaceType interfaceType => new[] { interfaceType.MethodSides, interfaceType.FieldSides },
            _ => []
        };

        foreach (var table in tables)
        {
            if (table.TryGetValue(member, out var side)) return side;
        }

        return Side.All;
    }

    /// <summary>The side the declaration of a class or interface is annotated with.</summary>
    private static Side TypeSide(AnalysisResult result, Type type)
    {
        var declaration = type.DeclaredAt;
        if (declaration == null || !result.Syms.GetByID(declaration.Sym, out var symbol)) return Side.All;
        return symbol.Side;
    }

    private static Side Resolve(List<Annotation> annotations, Side enclosing)
    {
        var own = BuiltinAnnotations.ExtractSide(annotations);
        return own == Side.All || own == Side.None ? enclosing : own;
    }

    private static void Add(List<SideMarker> markers, NameRef name, Side side, string kind = "declaration")
    {
        var marker = NameOf(side);
        if (marker == null || name.Span.StartLn <= 0) return;

        markers.Add(new SideMarker(new Position(name.Span.StartLn - 1, name.Span.StartCol - 1), marker, kind));
    }
}
