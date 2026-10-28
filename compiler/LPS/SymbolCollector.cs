using Nebra.Diagnostics;
using Nebra.IR;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;

using LspSymbolKind = OmniSharp.Extensions.LanguageServer.Protocol.Models.SymbolKind;

namespace Nebra.LPS;

/// <summary>
/// Builds the outline of a file - every declaration with its members nested under it - in the
/// shape of LSP document symbols. Shared by the document outline and the workspace symbol
/// search, so both list the same declarations.
/// </summary>
public static class SymbolCollector
{
    public static List<DocumentSymbol> Collect(AnalysisResult result)
    {
        var symbols = new List<DocumentSymbol>();
        CollectStatements(result.Hir.Body, result.SourceText, symbols);
        return symbols;
    }

    private static void CollectStatements(IEnumerable<Stmt> statements, string source, List<DocumentSymbol> symbols)
    {
        foreach (var statement in statements)
        {
            CollectStatement(statement, source, symbols);
        }
    }

    private static void CollectStatement(Stmt statement, string source, List<DocumentSymbol> symbols)
    {
        switch (statement)
        {
            case ExportStmt export:
                CollectStatement(export.Declaration, source, symbols);
                break;
            case FunctionDecl function when function.NamePath.Count > 0:
                symbols.Add(Symbol(FunctionName(function), LspSymbolKind.Function, function.Span, function.NamePath[0].Span));
                break;
            case LocalFunctionDecl function:
                symbols.Add(Symbol(function.Name.Name, LspSymbolKind.Function, function.Span, function.Name.Span));
                break;
            case LocalDecl local:
                symbols.AddRange(local.Variables.Select(variable =>
                    Symbol(variable.Name.Name, LspSymbolKind.Variable, variable.Span, variable.Name.Span)));
                break;
            case ClassDecl classDecl:
                symbols.Add(Symbol(classDecl.Name.Name, LspSymbolKind.Class, classDecl.Span, classDecl.Name.Span, ClassMembers(classDecl)));
                break;
            case InterfaceDecl interfaceDecl:
                symbols.Add(Symbol(interfaceDecl.Name.Name, LspSymbolKind.Interface, interfaceDecl.Span, interfaceDecl.Name.Span,
                    InterfaceMembers(interfaceDecl)));
                break;
            case EnumDecl enumDecl:
                symbols.Add(Symbol(enumDecl.Name.Name, LspSymbolKind.Enum, enumDecl.Span, enumDecl.Name.Span,
                    enumDecl.Members.Select(member => Symbol(member.Name.Name, LspSymbolKind.EnumMember, member.Span, member.Name.Span)).ToList()));
                break;
            case ExtendDecl extendDecl when extendDecl.TargetType != null:
                symbols.Add(Symbol("extend " + SourceOf(extendDecl.TargetType.Span, source), LspSymbolKind.Namespace,
                    extendDecl.Span, extendDecl.TargetType.Span,
                    extendDecl.Methods.Select(method => Symbol(method.Name.Name, LspSymbolKind.Method, method.Span, method.Name.Span)).ToList()));
                break;
            case DeclareFunctionDecl function when function.NamePath.Count > 0:
                symbols.Add(Symbol(string.Join(".", function.NamePath.Select(part => part.Name)), LspSymbolKind.Function,
                    function.Span, function.NamePath[^1].Span));
                break;
            case DeclareVariableDecl variable:
                symbols.Add(Symbol(variable.Name.Name, LspSymbolKind.Variable, variable.Span, variable.Name.Span));
                break;
            case DeclareModuleDecl module:
            {
                var members = new List<DocumentSymbol>();
                CollectStatements(module.Members, source, members);
                symbols.Add(Symbol(module.ModuleName.Name, LspSymbolKind.Module, module.Span, module.ModuleName.Span, members));
                break;
            }
        }
    }

    private static List<DocumentSymbol> ClassMembers(ClassDecl classDecl)
    {
        var members = new List<DocumentSymbol>();

        foreach (var classConstructor in classDecl.Constructors)
        {
            members.Add(Symbol("constructor", LspSymbolKind.Constructor, classConstructor.Span, classConstructor.Span));
        }

        foreach (var field in classDecl.Fields)
        {
            members.Add(Symbol(field.Name.Name, LspSymbolKind.Field, field.Span, field.Name.Span));
        }

        foreach (var method in classDecl.Methods)
        {
            var isOperator = method.IsOperator && method.OperatorSymbol != null;
            members.Add(Symbol(isOperator ? "operator " + method.OperatorSymbol : method.Name.Name,
                isOperator ? LspSymbolKind.Operator : LspSymbolKind.Method, method.Span, method.Name.Span));
        }

        foreach (var accessor in classDecl.Accessors)
        {
            var prefix = accessor.Kind == AccessorKind.Getter ? "get " : "set ";
            members.Add(Symbol(prefix + accessor.Name.Name, LspSymbolKind.Property, accessor.Span, accessor.Name.Span));
        }

        return members;
    }

    private static List<DocumentSymbol> InterfaceMembers(InterfaceDecl interfaceDecl)
    {
        var fields = interfaceDecl.Fields.Select(field => Symbol(field.Name.Name, LspSymbolKind.Field, field.Span, field.Name.Span));
        var methods = interfaceDecl.Methods.Select(method => Symbol(method.Name.Name, LspSymbolKind.Method, method.Span, method.Name.Span));
        return fields.Concat(methods).ToList();
    }

    private static string FunctionName(FunctionDecl function)
    {
        var name = string.Join(".", function.NamePath.Select(part => part.Name));
        return function.MethodName == null ? name : name + ":" + function.MethodName.Name;
    }

    private static string SourceOf(TextSpan span, string source)
    {
        var lines = source.Split('\n');
        if (span.StartLn != span.EndLn || span.StartLn < 1 || span.StartLn > lines.Length)
        {
            return "...";
        }

        var line = lines[span.StartLn - 1].TrimEnd('\r');
        var start = Math.Clamp(span.StartCol - 1, 0, line.Length);
        var end = Math.Clamp(span.EndCol - 1, start, line.Length);
        return line[start..end].Trim();
    }

    private static DocumentSymbol Symbol(string name, LspSymbolKind kind, TextSpan range, TextSpan selection,
        List<DocumentSymbol>? children = null)
    {
        return new DocumentSymbol
        {
            Name = name,
            Kind = kind,
            Range = NebraWorkspace.SpanToRange(range),
            SelectionRange = NebraWorkspace.SpanToRange(selection),
            Children = children is { Count: > 0 } ? new Container<DocumentSymbol>(children) : null
        };
    }
}
