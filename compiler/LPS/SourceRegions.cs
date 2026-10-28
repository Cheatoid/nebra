using Nebra.Diagnostics;
using Nebra.IR;

namespace Nebra.LPS;

/// <summary>
/// The nested regions of a file as spans: every node of the tree plus the class, interface and
/// extension members, which the tree keeps outside its node registry. Folding and selection
/// ranges both derive from these.
/// </summary>
public static class SourceRegions
{
    public static IEnumerable<(Node? Node, TextSpan Span)> Collect(IRScript script)
    {
        foreach (var node in NodeFinder.BuildNodeRegistry(script).Values)
        {
            yield return (node, node.Span);

            foreach (var member in MemberSpans(node))
            {
                yield return (null, member);
            }
        }
    }

    public static bool Contains(TextSpan span, int line, int col)
    {
        if (line < span.StartLn || line > span.EndLn) return false;
        if (line == span.StartLn && col < span.StartCol) return false;
        return line != span.EndLn || col <= span.EndCol;
    }

    private static IEnumerable<TextSpan> MemberSpans(Node node)
    {
        switch (node)
        {
            case ClassDecl classDecl:
                foreach (var classConstructor in classDecl.Constructors) yield return classConstructor.Span;
                foreach (var method in classDecl.Methods) yield return method.Span;
                foreach (var field in classDecl.Fields) yield return field.Span;
                foreach (var accessor in classDecl.Accessors) yield return accessor.Span;
                break;
            case InterfaceDecl interfaceDecl:
                foreach (var method in interfaceDecl.Methods) yield return method.Span;
                foreach (var field in interfaceDecl.Fields) yield return field.Span;
                break;
            case ExtendDecl extendDecl:
                foreach (var method in extendDecl.Methods) yield return method.Span;
                break;
        }
    }
}
