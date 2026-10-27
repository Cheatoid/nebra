using Nebra.Diagnostics;
using Nebra.IR;

namespace Nebra.Compiler.Passes;

/// <summary>
/// Reports a top-level statement that, when the file runs, reaches a class, function or extension
/// defined further down the same file. Top-level definitions are declared before the first
/// statement so functions can reach each other, but each one is only assigned when its
/// definition runs, so a use above it finds <c>nil</c>. Uses inside a function body are fine
/// unless a statement above the definition calls that function.
/// </summary>
public sealed class CheckDefinitionOrderPass() : Pass(PassName, PassScope.PerBuild, noErrors: false, InferTypesPass.PassName)
{
    public const string PassName = "CheckDefinitionOrder";

    private sealed record Reference(object Target, string Name, TextSpan Span);

    public override bool Run(PassContext context)
    {
        foreach (var package in context.Pkgs.Where(package => !package.IsLibrary))
        {
            foreach (var file in package.Files.Where(file => !file.IsDeclarationFile))
                CheckFile(context, file.Hir.Body);
        }

        return true;
    }

    private static void CheckFile(PassContext context, List<Stmt> body)
    {
        var definedAt = new Dictionary<object, int>();
        var functionBodies = new Dictionary<SymID, List<Reference>>();
        var kinds = new Dictionary<object, string>();
        for (var index = 0; index < body.Count; index++)
        {
            switch (body[index] is ExportStmt export ? export.Declaration : body[index])
            {
                case ClassDecl { IsDeclare: false } cd:
                    definedAt.TryAdd(cd.Name.Sym, index);
                    kinds[cd.Name.Sym] = "class";
                    break;
                case LocalFunctionDecl lfd:
                    definedAt.TryAdd(lfd.Name.Sym, index);
                    kinds[lfd.Name.Sym] = "function";
                    functionBodies[lfd.Name.Sym] = ReferencesRunBy(lfd);
                    break;
                case FunctionDecl { NamePath.Count: 1, MethodName: null } fd:
                    definedAt.TryAdd(fd.NamePath[0].Sym, index);
                    kinds[fd.NamePath[0].Sym] = "function";
                    functionBodies[fd.NamePath[0].Sym] = ReferencesRunBy(fd);
                    break;
                case ExtendDecl { IsDeclare: false } ed:
                    foreach (var member in ed.Methods)
                    {
                        definedAt.TryAdd(member, index);
                        kinds[member] = "extend block";
                    }
                    break;
            }
        }

        for (var index = 0; index < body.Count; index++)
        {
            foreach (var reference in ImmediateReferences(body[index]))
            {
                if (definedAt.TryGetValue(reference.Target, out var defined) && defined > index)
                {
                    context.Diag.Report(reference.Span, DiagnosticCode.ErrUsedBeforeDefinition,
                        $"'{reference.Name}' is used before its {kinds[reference.Target]} below has run");
                    continue;
                }

                if (reference.Target is not SymID function || !functionBodies.ContainsKey(function)) continue;
                if (FirstLaterDefinition(function, functionBodies, definedAt, index) is { } late)
                {
                    context.Diag.Report(reference.Span, DiagnosticCode.ErrUsedBeforeDefinition,
                        $"'{reference.Name}' uses '{late.Name}' before its {kinds[late.Target]} below has run");
                }
            }
        }
    }

    /// <summary>
    /// The first reference that calling <paramref name="function"/>, and the functions it calls in
    /// turn, makes to something defined after the statement at <paramref name="index"/>.
    /// </summary>
    private static Reference? FirstLaterDefinition(SymID function, Dictionary<SymID, List<Reference>> functionBodies,
        Dictionary<object, int> definedAt, int index)
    {
        var visited = new HashSet<SymID>();
        var pending = new Stack<SymID>();
        pending.Push(function);
        while (pending.TryPop(out var current))
        {
            if (!visited.Add(current) || !functionBodies.TryGetValue(current, out var references)) continue;

            foreach (var reference in references)
            {
                if (definedAt.TryGetValue(reference.Target, out var defined) && defined > index) return reference;
                if (reference.Target is SymID callee) pending.Push(callee);
            }
        }

        return null;
    }

    /// <summary>
    /// The references a top-level statement makes while it runs. Function bodies run later and are
    /// left out; a class runs its base class lookup and its static field defaults right away.
    /// </summary>
    private static IEnumerable<Reference> ImmediateReferences(Stmt stmt)
    {
        switch (stmt is ExportStmt export ? export.Declaration : stmt)
        {
            case LocalFunctionDecl or FunctionDecl or ExtendDecl or InterfaceDecl:
                return [];
            case ClassDecl cd:
            {
                var references = new List<Reference>();
                if (cd.BaseClass != null) references.Add(new Reference(cd.BaseClass.Sym, cd.BaseClass.Name, cd.BaseClass.Span));
                foreach (var field in cd.Fields.Where(field => field.IsStatic && field.DefaultValue != null))
                    references.AddRange(ReferencesIn(RunningNodes(NodeTree.Index(field.DefaultValue!))));
                return references;
            }
            default:
                return ReferencesIn(RunningNodes(NodeTree.Index(stmt)));
        }
    }

    /// <summary>What calling a top-level function reaches: the references its own body makes while it runs.</summary>
    private static List<Reference> ReferencesRunBy(Stmt function)
    {
        var nodes = NodeTree.Index(function);
        nodes.Remove(function.ID);
        return ReferencesIn(RunningNodes(nodes)).ToList();
    }

    /// <summary><paramref name="nodes"/> without the bodies of the functions defined among them, which run only when called.</summary>
    private static IEnumerable<Node> RunningNodes(Dictionary<NodeID, Node> nodes)
    {
        var deferred = new HashSet<NodeID>();
        foreach (var node in nodes.Values)
        {
            var nested = node switch
            {
                FunctionDefExpr expression => NodeTree.Index(expression),
                FunctionDecl or LocalFunctionDecl => NodeTree.Index((Stmt)node),
                _ => null,
            };
            if (nested == null) continue;

            foreach (var id in nested.Keys) deferred.Add(id);
        }

        return nodes.Values.Where(node => !deferred.Contains(node.ID));
    }

    private static IEnumerable<Reference> ReferencesIn(IEnumerable<Node> nodes)
    {
        foreach (var node in nodes)
        {
            switch (node)
            {
                case NameExpr name when name.Name.Sym != SymID.Invalid:
                    yield return new Reference(name.Name.Sym, name.Name.Name, name.Span);
                    break;
                case NewExpr creation when creation.ClassName.Sym != SymID.Invalid:
                    yield return new Reference(creation.ClassName.Sym, creation.ClassName.Name, creation.Span);
                    break;
                case MethodCallExpr { Extension: { } extension } call:
                    yield return new Reference(extension, call.MethodName.Name, call.MethodName.Span);
                    break;
                case DotAccessExpr { Extension: { } extension } access:
                    yield return new Reference(extension, access.FieldName.Name, access.FieldName.Span);
                    break;
                case BinaryExpr { Extension: { } extension } binary:
                    yield return new Reference(extension, "operator " + InferTypesPass.OperatorSymbol(extension.Name.Name), binary.Span);
                    break;
                case UnaryExpr { Extension: { } extension } unary:
                    yield return new Reference(extension, "operator " + InferTypesPass.OperatorSymbol(extension.Name.Name), unary.Span);
                    break;
                case IndexAccessExpr { Extension: { } extension } index:
                    yield return new Reference(extension, "operator []", index.Span);
                    break;
                case FunctionCallExpr { Extension: { } extension } call:
                    yield return new Reference(extension, "operator ()", call.Span);
                    break;
            }
        }
    }
}
