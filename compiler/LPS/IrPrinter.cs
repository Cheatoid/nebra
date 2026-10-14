using System.Text;
using Nebra.IR;

namespace Nebra.LPS;

/// <summary>
/// Writes program nodes back as Nebra code, for showing what an annotation expanded to. Covers
/// what annotations build - literals, names, calls, member access, functions, locals, returns -
/// and shortens anything else, including the bodies of declarations, which annotations keep as
/// they were.
/// </summary>
public static class IrPrinter
{
    private const string Indent = "    ";

    public static string Print(IEnumerable<Stmt> statements)
    {
        var text = new StringBuilder();
        foreach (var statement in statements)
        {
            Statement(text, statement, "");
        }

        return text.ToString().TrimEnd();
    }

    private static void Statement(StringBuilder text, Stmt statement, string indent)
    {
        switch (statement)
        {
            case ExportStmt export:
                text.Append(indent).Append("export ");
                Statement(text, export.Declaration, indent, skipIndent: true);
                break;
            default:
                Statement(text, statement, indent, skipIndent: false);
                break;
        }
    }

    private static void Statement(StringBuilder text, Stmt statement, string indent, bool skipIndent)
    {
        if (!skipIndent) text.Append(indent);

        switch (statement)
        {
            case ExprStmt expression:
                text.Append(Expression(expression.Expression, indent)).Append('\n');
                break;
            case ReturnStmt returned:
                text.Append("return ").Append(List(returned.Values, indent)).Append('\n');
                break;
            case LocalDecl local:
                text.Append(local.IsMutable ? "local mut " : "local ").Append(string.Join(", ", local.Variables.Select(variable => variable.Name.Name)));
                if (local.Values.Count > 0) text.Append(" = ").Append(List(local.Values, indent));
                text.Append('\n');
                break;
            case AssignStmt assignment:
                text.Append(List(assignment.Targets, indent)).Append(" = ").Append(List(assignment.Values, indent)).Append('\n');
                break;
            case FunctionDecl function:
                Function(text, "function " + FunctionName(function), function.Parameters, indent);
                break;
            case LocalFunctionDecl function:
                Function(text, "local function " + function.Name.Name, function.Parameters, indent);
                break;
            case ClassDecl classDecl:
                text.Append("class ").Append(classDecl.Name.Name).Append('\n').Append(indent).Append(Indent).Append("...\n").Append(indent).Append("end\n");
                break;
            default:
                text.Append("...\n");
                break;
        }
    }

    private static void Function(StringBuilder text, string head, List<Parameter> parameters, string indent)
    {
        text.Append(head).Append('(').Append(Parameters(parameters)).Append(")\n")
            .Append(indent).Append(Indent).Append("...\n")
            .Append(indent).Append("end\n");
    }

    private static string FunctionName(FunctionDecl function)
    {
        var path = string.Join(".", function.NamePath.Select(name => name.Name));
        return function.MethodName == null ? path : $"{path}:{function.MethodName.Name}";
    }

    private static string Parameters(List<Parameter> parameters)
    {
        return string.Join(", ", parameters.Select(parameter => parameter.IsVararg ? "..." : parameter.Name.Name));
    }

    private static string List(IEnumerable<Expr> expressions, string indent)
    {
        return string.Join(", ", expressions.Select(expression => Expression(expression, indent)));
    }

    private static string Expression(Expr expression, string indent)
    {
        return expression switch
        {
            StringLiteralExpr text => $"\"{text.Value}\"",
            NumberLiteralExpr number => number.Raw,
            BoolLiteralExpr flag => flag.Value ? "true" : "false",
            NilLiteralExpr => "nil",
            VarargExpr => "...",
            NameExpr name => name.Name.Name,
            ParenExpr paren => $"({Expression(paren.Inner, indent)})",
            DotAccessExpr access => $"{Expression(access.Object, indent)}.{access.FieldName.Name}",
            IndexAccessExpr index => $"{Expression(index.Object, indent)}[{Expression(index.Index, indent)}]",
            FunctionCallExpr call => $"{Expression(call.Callee, indent)}({List(call.Arguments, indent)})",
            MethodCallExpr call => $"{Expression(call.Object, indent)}:{call.MethodName.Name}({List(call.Arguments, indent)})",
            NewExpr creation => $"new {creation.ClassName.Name}({List(creation.Arguments, indent)})",
            TableConstructorExpr table => Table(table, indent),
            FunctionDefExpr function => FunctionLiteral(function, indent),
            _ => "..."
        };
    }

    private static string Table(TableConstructorExpr table, string indent)
    {
        var fields = table.Fields.Select(field => field switch
        {
            { Name: not null } => $"{field.Name.Name} = {Expression(field.Value, indent)}",
            { Key: not null } => $"[{Expression(field.Key, indent)}] = {Expression(field.Value, indent)}",
            _ => Expression(field.Value, indent)
        });

        return $"{{ {string.Join(", ", fields)} }}";
    }

    private static string FunctionLiteral(FunctionDefExpr function, string indent)
    {
        var body = new StringBuilder();
        foreach (var statement in function.Body)
        {
            Statement(body, statement, indent + Indent);
        }

        if (function.ReturnStmt != null)
        {
            Statement(body, function.ReturnStmt, indent + Indent);
        }

        return $"function({Parameters(function.Parameters)})\n{body}{indent}end";
    }
}
