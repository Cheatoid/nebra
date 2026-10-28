using Nebra.IR;

namespace Nebra.LPS;

/// <summary>Every block of statements in a file: the top level, and each body nested inside it.</summary>
public static class StatementLists
{
    public static IEnumerable<List<Stmt>> All(IRScript script)
    {
        yield return script.Body;

        foreach (var node in NodeFinder.BuildNodeRegistry(script).Values)
        {
            foreach (var body in BodiesOf(node))
            {
                yield return body;
            }
        }
    }

    private static IEnumerable<List<Stmt>> BodiesOf(Node node)
    {
        switch (node)
        {
            case FunctionDecl function: yield return function.Body; break;
            case LocalFunctionDecl function: yield return function.Body; break;
            case FunctionDefExpr function: yield return function.Body; break;
            case DoBlockStmt block: yield return block.Body; break;
            case TryStmt protectedBlock:
                foreach (var block in protectedBlock.Blocks) yield return block;
                break;
            case WhileStmt loop: yield return loop.Body; break;
            case RepeatStmt loop: yield return loop.Body; break;
            case NumericForStmt loop: yield return loop.Body; break;
            case GenericForStmt loop: yield return loop.Body; break;
            case DeferStmt { Block: not null } defer: yield return defer.Block; break;
            case IfStmt branch:
                yield return branch.Body;
                foreach (var clause in branch.ElseIfs) yield return clause.Body;
                if (branch.ElseBody != null) yield return branch.ElseBody;
                break;
            case MatchStmt match:
                foreach (var arm in match.Arms) yield return arm.Body;
                break;
            case ClassDecl classDecl:
                foreach (var classConstructor in classDecl.Constructors) yield return classConstructor.Body;
                foreach (var method in classDecl.Methods) yield return method.Body;
                foreach (var accessor in classDecl.Accessors) yield return accessor.Body;
                break;
            case InterfaceDecl interfaceDecl:
                foreach (var method in interfaceDecl.Methods.Where(method => method.Body != null)) yield return method.Body!;
                break;
            case ExtendDecl extendDecl:
                foreach (var method in extendDecl.Methods) yield return method.Body;
                break;
        }
    }
}
