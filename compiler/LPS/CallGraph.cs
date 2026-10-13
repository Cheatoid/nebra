using Nebra.Diagnostics;
using Nebra.IR;

using LspSymbolKind = OmniSharp.Extensions.LanguageServer.Protocol.Models.SymbolKind;

namespace Nebra.LPS;

/// <summary>A function or method that can be called, identified by the name it was declared under.</summary>
public sealed record Callable(NameRef Name, TextSpan Span, LspSymbolKind Kind, string? Container, AnalysisResult File);

/// <summary>
/// One call: where it happens, the callable it reaches, and the callable it happens in - null for
/// a call at the top level of a file.
/// </summary>
public sealed record CallSite(TextSpan Span, NameRef Callee, Callable? Caller, AnalysisResult File);

/// <summary>
/// The callables and calls of every source file in a workspace snapshot. Calls are resolved the
/// same way navigation resolves them, so a call through an import, through a method of a base
/// class or through an extension reaches the declaration it would jump to.
/// </summary>
public sealed class CallGraph
{
    public IReadOnlyList<Callable> Callables { get; }
    public IReadOnlyList<CallSite> Calls { get; }

    private CallGraph(IReadOnlyList<Callable> callables, IReadOnlyList<CallSite> calls)
    {
        Callables = callables;
        Calls = calls;
    }

    public static CallGraph Build(WorkspaceSnapshot snapshot)
    {
        var callables = new List<Callable>();
        var calls = new List<CallSite>();

        foreach (var result in snapshot.SourceResults())
        {
            var fileCallables = CollectCallables(result).ToList();
            callables.AddRange(fileCallables);

            foreach (var node in NodeFinder.BuildNodeRegistry(result.Hir).Values)
            {
                var (name, callee) = ResolveCall(result, node);
                if (name == null || callee == null) continue;

                calls.Add(new CallSite(name.Span, callee, Enclosing(fileCallables, name.Span), result));
            }
        }

        return new CallGraph(callables, calls);
    }

    /// <summary>The callable declared under <paramref name="name"/>, if any.</summary>
    public Callable? Find(NameRef name)
    {
        return Callables.FirstOrDefault(callable => ReferenceEquals(callable.Name, name));
    }

    /// <summary>The callable whose name starts at a zero-based position in <paramref name="path"/>.</summary>
    public Callable? FindAt(string path, int line, int character)
    {
        return Callables.FirstOrDefault(callable =>
            string.Equals(callable.File.FilePath, path, StringComparison.OrdinalIgnoreCase)
            && callable.Name.Span.StartLn - 1 == line
            && callable.Name.Span.StartCol - 1 == character);
    }

    private static (NameRef? Name, NameRef? Callee) ResolveCall(AnalysisResult result, Node node)
    {
        return node switch
        {
            MethodCallExpr call => (call.MethodName, CursorTarget.MemberDeclarationOf(result, call.MethodName)),
            FunctionCallExpr { Callee: DotAccessExpr dot } => (dot.FieldName, CursorTarget.MemberDeclarationOf(result, dot.FieldName)),
            FunctionCallExpr { Callee: NameExpr name } => (name.Name, DeclarationOf(result.Snapshot, name.Name.Sym)),
            _ => (null, null)
        };
    }

    private static NameRef? DeclarationOf(WorkspaceSnapshot snapshot, SymID symbol)
    {
        if (symbol == SymID.Invalid || !snapshot.TryGetSymbol(snapshot.Origin(symbol), out var declared)) return null;
        if (!snapshot.TryGetNode(declared.DeclaringNode, out var node, out _)) return null;
        return NodeFinder.DeclaredName(node, declared.Name);
    }

    private static Callable? Enclosing(List<Callable> callables, TextSpan span)
    {
        return callables
            .Where(callable => SourceRegions.Contains(callable.Span, span.StartLn, span.StartCol))
            .OrderBy(callable => callable.Span.EndLn - callable.Span.StartLn)
            .ThenBy(callable => callable.Span.EndCol - callable.Span.StartCol)
            .FirstOrDefault();
    }

    private static IEnumerable<Callable> CollectCallables(AnalysisResult result)
    {
        foreach (var node in NodeFinder.BuildNodeRegistry(result.Hir).Values)
        {
            switch (node)
            {
                case FunctionDecl function when function.NamePath.Count > 0:
                {
                    var name = function.MethodName ?? function.NamePath[^1];
                    var container = function.NamePath.Count > 1 || function.MethodName != null
                        ? string.Join(".", function.NamePath.Select(part => part.Name))
                        : null;
                    yield return new Callable(name, function.Span, LspSymbolKind.Function, container, result);
                    break;
                }
                case LocalFunctionDecl function:
                    yield return new Callable(function.Name, function.Span, LspSymbolKind.Function, null, result);
                    break;
                case ClassDecl classDecl:
                    foreach (var method in classDecl.Methods)
                        yield return new Callable(method.Name, method.Span, LspSymbolKind.Method, classDecl.Name.Name, result);
                    break;
                case InterfaceDecl interfaceDecl:
                    foreach (var method in interfaceDecl.Methods.Where(method => method.IsDefault))
                        yield return new Callable(method.Name, method.Span, LspSymbolKind.Method, interfaceDecl.Name.Name, result);
                    break;
                case ExtendDecl { IsDeclare: false } extendDecl:
                    foreach (var method in extendDecl.Methods)
                        yield return new Callable(method.Name, method.Span, LspSymbolKind.Method, "extend", result);
                    break;
            }
        }
    }
}
