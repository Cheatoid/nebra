using System.Text.RegularExpressions;
using Nebra.IR;
using Nebra.LPS.Handlers;
using NebraType = Nebra.IR.Type;

namespace Nebra.LPS;

/// <summary>
/// The type a value written at the cursor is expected to have, worked out the way the compiler
/// takes it, while the code around the cursor is still being typed: the annotation of a local,
/// the target of an assignment, the declared return type of the function, the other operand of
/// <c>==</c>, the value a <c>match</c> tests, or the parameter of a call.
/// </summary>
public static partial class ExpectedTypeAtCursor
{
    [GeneratedRegex(@"\blocal\s+(?:mut\s+)?[A-Za-z_][A-Za-z0-9_]*\s*:\s*(?<type>[^=]+?)\s*=$")]
    private static partial Regex AnnotatedLocal();

    [GeneratedRegex(@"(?<target>[A-Za-z_][A-Za-z0-9_]*(?:\.[A-Za-z_][A-Za-z0-9_]*)*)\s*=$")]
    private static partial Regex AssignmentTarget();

    [GeneratedRegex(@"(?<operand>[A-Za-z_][A-Za-z0-9_]*(?:\.[A-Za-z_][A-Za-z0-9_]*)*)\s*(?:==|~=)$")]
    private static partial Regex ComparedOperand();

    [GeneratedRegex(@"\bmatch\s+(?<operand>[A-Za-z_][A-Za-z0-9_]*(?:\.[A-Za-z_][A-Za-z0-9_]*)*)\s*$")]
    private static partial Regex MatchedOperand();

    /// <summary>
    /// The expected type at the 1-based <paramref name="line"/> and <paramref name="col"/>, or
    /// <c>null</c> where no type is expected. <paramref name="visible"/> are the symbols in scope,
    /// used for names the analysed program does not hold yet.
    /// </summary>
    public static NebraType? Find(AnalysisResult result, int line, int col, IReadOnlyList<Symbol> visible)
    {
        var prefix = LinePrefix(result.SourceText, line, col);
        if (TableEntryAt(result.SourceText, line, col) is { } entry) return EntryType(result, entry, visible);

        if (Regex.IsMatch(prefix, @"\bcase$")) return MatchedType(result, line, col) ?? MatchedTypeFromSource(result, line, visible);
        if (Regex.IsMatch(prefix, @"\breturn$")) return DeclaredReturnType(result, line, col);

        if (ComparedOperand().Match(prefix) is { Success: true } compared)
            return TypeOfOperand(result, line, compared.Groups["operand"], visible);

        if (prefix.EndsWith('=') && !Regex.IsMatch(prefix, @"(?:==|~=|<=|>=)$"))
        {
            if (AnnotatedLocal().Match(prefix) is { Success: true } local)
                return AnnotatedType(result, line, local.Groups["type"].Value.Trim(), visible);
            if (AssignmentTarget().Match(prefix) is { Success: true } assignment)
                return TypeOfOperand(result, line, assignment.Groups["target"], visible);
            return null;
        }

        return ParameterType(result, line, col) ?? ParameterTypeFromSource(result, line, col, visible);
    }

    /// <summary>
    /// Whether <paramref name="candidate"/>, the type of a value or the return type of a function,
    /// fits where <paramref name="expected"/> is wanted. Ranks completions only, so it follows the
    /// compiler's rules for the common cases and errs towards not fitting.
    /// </summary>
    public static bool Fits(NebraType candidate, NebraType expected)
    {
        if (candidate.ID == expected.ID) return true;
        if (expected.Kind == TypeKind.PrimitiveAny) return true;

        if (expected is UnionType expectedUnion) return expectedUnion.Types.Any(member => Fits(candidate, member));
        if (candidate is UnionType candidateUnion)
            return candidateUnion.Types.Where(member => member.Kind != TypeKind.PrimitiveNil).All(member => Fits(member, expected));
        if (candidate is LiteralType literal) return Fits(literal.BaseType, expected);

        return (candidate, expected) switch
        {
            (ClassType candidateClass, ClassType expectedClass) => ClassChain(candidateClass).Any(current => current.Definition == expectedClass.Definition),
            (ClassType candidateClass, InterfaceType expectedInterface) =>
                NebraType.ImplementedInterfaces(candidateClass).Any(iface => iface.Definition == expectedInterface.Definition),
            (InterfaceType candidateInterface, InterfaceType expectedInterface) =>
                NebraType.BaseInterfacesOf(candidateInterface).Prepend(candidateInterface).Any(iface => iface.Definition == expectedInterface.Definition),
            (EnumType candidateEnum, _) => expected.ID == candidateEnum.BaseType.ID,
            _ => false,
        };
    }

    private static IEnumerable<ClassType> ClassChain(ClassType classType)
    {
        for (var current = classType; current != null; current = current.BaseClass)
        {
            yield return current;
        }
    }

    /// <summary>The text of <paramref name="line"/> before the cursor, without the name being typed and trailing blanks.</summary>
    private static string LinePrefix(string source, int line, int col)
    {
        var lines = source.Split('\n');
        if (line < 1 || line > lines.Length) return "";

        var text = lines[line - 1].TrimEnd('\r');
        var end = Math.Clamp(col - 1, 0, text.Length);
        while (end > 0 && (char.IsLetterOrDigit(text[end - 1]) || text[end - 1] == '_')) end--;
        return text[..end].TrimEnd();
    }

    /// <summary>
    /// An entry of a table literal the cursor writes the value of: the 1-based position of the
    /// table's <c>{</c>, and the name of the field for <c>name =</c> or <c>["name"] =</c>, or
    /// <c>null</c> for a positional entry. <c>null</c> when the innermost bracket left open
    /// before the cursor is not a table's.
    /// </summary>
    private sealed record TableEntry(int BraceLine, int BraceCol, string? Field);

    private static TableEntry? TableEntryAt(string source, int line, int col)
    {
        var lineStarts = new List<int> { 0 };
        for (var i = 0; i < source.Length; i++)
        {
            if (source[i] == '\n') lineStarts.Add(i + 1);
        }

        if (line < 1 || line > lineStarts.Count) return null;
        var cursor = Math.Min(lineStarts[line - 1] + col - 1, source.Length);
        while (cursor > 0 && (char.IsLetterOrDigit(source[cursor - 1]) || source[cursor - 1] == '_')) cursor--;

        var brace = OpenBracket(source, cursor);
        if (brace < 0 || source[brace] != '{') return null;

        var entry = LastEntry(source[(brace + 1)..cursor]).Trim();
        string? field = null;
        if (entry.Length > 0)
        {
            var named = Regex.Match(entry, @"^(?:(?<name>[A-Za-z_][A-Za-z0-9_]*)|\[\s*[""'](?<key>[^""']*)[""']\s*\])\s*=$");
            if (!named.Success) return null;
            field = named.Groups["name"].Success ? named.Groups["name"].Value : named.Groups["key"].Value;
        }

        var braceLine = lineStarts.FindLastIndex(start => start <= brace) + 1;
        return new TableEntry(braceLine, brace - lineStarts[braceLine - 1] + 1, field);
    }

    /// <summary>The offset of the innermost bracket left open before <paramref name="end"/>, or -1.</summary>
    private static int OpenBracket(string source, int end)
    {
        var depth = 0;
        for (var i = end - 1; i >= 0; i--)
        {
            switch (source[i])
            {
                case ')' or ']' or '}':
                    depth++;
                    break;
                case '(' or '[' or '{':
                    if (depth == 0) return i;
                    depth--;
                    break;
            }
        }

        return -1;
    }

    /// <summary>The text of the last entry in <paramref name="entries"/>, after the last <c>,</c> or <c>;</c> outside brackets.</summary>
    private static string LastEntry(string entries)
    {
        var depth = 0;
        var start = 0;
        for (var i = 0; i < entries.Length; i++)
        {
            switch (entries[i])
            {
                case '(' or '[' or '{':
                    depth++;
                    break;
                case ')' or ']' or '}':
                    depth--;
                    break;
                case ',' or ';' when depth == 0:
                    start = i + 1;
                    break;
            }
        }

        return entries[start..];
    }

    /// <summary>
    /// The type of a table entry: the field of the struct, class or interface the table is written
    /// for, the value type of a map, or the element type of a list for a positional entry.
    /// </summary>
    private static NebraType? EntryType(AnalysisResult result, TableEntry entry, IReadOnlyList<Symbol> visible)
    {
        var table = Find(result, entry.BraceLine, entry.BraceCol, visible);
        var shapes = table is UnionType union ? union.Types.Where(member => member.Kind != TypeKind.PrimitiveNil).ToList() : table != null ? [table] : [];
        if (shapes.Count != 1) return null;

        return (shapes[0], entry.Field) switch
        {
            (TableArrayType list, null) => list.ElementType,
            (TableMapType map, not null) => map.ValueType,
            (var shape, { } field) => NebraType.FieldTypeOf(shape, field),
            _ => null,
        };
    }

    private static NebraType? MatchedType(AnalysisResult result, int line, int col)
    {
        var matches = new List<(Diagnostics.TextSpan Span, Expr Scrutinee)>();
        foreach (var node in result.NodeRegistry.Values)
        {
            if (node is MatchStmt statement && Contains(statement.Span, line, col)) matches.Add((statement.Span, statement.Scrutinee));
            if (node is MatchExpr expression && Contains(expression.Span, line, col)) matches.Add((expression.Span, expression.Scrutinee));
        }

        if (matches.Count == 0) return null;
        return TypeOf(result, matches.MinBy(candidate => Size(candidate.Span)).Scrutinee.Type);
    }

    /// <summary>
    /// The type a <c>match</c> tests, read from the source above the cursor when the match being
    /// written is still too unfinished to appear in the analysed program.
    /// </summary>
    private static NebraType? MatchedTypeFromSource(AnalysisResult result, int line, IReadOnlyList<Symbol> visible)
    {
        var lines = result.SourceText.Split('\n');
        for (var current = Math.Min(line, lines.Length); current >= 1 && current > line - 200; current--)
        {
            var match = MatchedOperand().Match(lines[current - 1].TrimEnd('\r'));
            if (match.Success) return TypeOfOperand(result, current, match.Groups["operand"], visible);
        }

        return null;
    }

    private static NebraType? DeclaredReturnType(AnalysisResult result, int line, int col)
    {
        var functions = new List<(Diagnostics.TextSpan Span, TypeRef? ReturnType)>();
        foreach (var node in result.NodeRegistry.Values)
        {
            switch (node)
            {
                case FunctionDecl function:
                    functions.Add((function.Span, function.ReturnType));
                    break;
                case LocalFunctionDecl local:
                    functions.Add((local.Span, local.ReturnType));
                    break;
                case FunctionDefExpr literal:
                    functions.Add((literal.Span, literal.ReturnType));
                    break;
                case ClassDecl classDecl:
                    functions.AddRange(classDecl.Methods.Select(method => (method.Span, method.ReturnType)));
                    break;
            }
        }

        var enclosing = functions.Where(function => Contains(function.Span, line, col)).OrderBy(function => Size(function.Span)).FirstOrDefault();
        return enclosing.ReturnType is { } returnType ? TypeOf(result, returnType.ResolvedType) : null;
    }

    private static NebraType? AnnotatedType(AnalysisResult result, int line, string typeText, IReadOnlyList<Symbol> visible)
    {
        var declared = result.NodeRegistry.Values
            .OfType<LocalDecl>()
            .Where(local => local.Span.StartLn == line && local.Variables.Count > 0)
            .Select(local => local.Variables[0].TypeAnnotation?.ResolvedType ?? TypID.Invalid)
            .FirstOrDefault(type => type != TypID.Invalid);
        if (declared != TypID.Invalid && declared != default) return TypeOf(result, declared);

        var name = typeText.TrimEnd('?').Trim();
        var symbol = visible.FirstOrDefault(candidate => candidate.Name == name
            && candidate.Kind is SymbolKind.Enum or SymbolKind.Class or SymbolKind.Interface);
        return symbol != null ? TypeOf(result, symbol.Type) : null;
    }

    /// <summary>
    /// The type of the expression <paramref name="operand"/> matched on <paramref name="line"/>:
    /// as the analysed program has it there, or for a plain name the type of the symbol in scope.
    /// </summary>
    private static NebraType? TypeOfOperand(AnalysisResult result, int line, Group operand, IReadOnlyList<Symbol> visible)
    {
        var lastColumn = operand.Index + operand.Length;
        if (NodeFinder.Find(result.Hir, line, lastColumn) is Expr expression
            && expression.Span.EndLn == line && expression.Span.EndCol == lastColumn
            && TypeOf(result, expression.Type) is { } found)
            return found;

        if (operand.Value.Contains('.')) return null;
        var symbol = visible.FirstOrDefault(candidate => candidate.Name == operand.Value);
        return symbol != null ? TypeOf(result, symbol.Type) : null;
    }

    private static NebraType? ParameterType(AnalysisResult result, int line, int col)
    {
        var call = SignatureHelpHandler.ResolveCall(result, line, col);
        if (call == null) return null;

        var (node, function, _, _) = call.Value;
        var position = new OmniSharp.Extensions.LanguageServer.Protocol.Models.Position(line - 1, col - 1);
        var index = SignatureHelpHandler.CountActiveParam(result.SourceText, position);
        if (node is MethodCallExpr && function.ParamNames.Count > 0 && function.ParamNames[0] == "self") index++;

        if (index < function.ParamTypes.Count) return function.ParamTypes[index];
        return function.IsVararg ? function.VarargType : null;
    }

    /// <summary>
    /// The parameter type of a call read from the source, for a call too unfinished to appear in
    /// the analysed program: the name before the open <c>(</c>, a function in scope or a method
    /// or function of a value in scope.
    /// </summary>
    private static NebraType? ParameterTypeFromSource(AnalysisResult result, int line, int col, IReadOnlyList<Symbol> visible)
    {
        var prefix = LinePrefix(result.SourceText, line, col);
        var open = OpenBracket(prefix, prefix.Length);
        if (open < 0 || prefix[open] != '(') return null;

        var callee = Regex.Match(prefix[..open], @"(?<receiver>[A-Za-z_][A-Za-z0-9_]*)\s*(?:(?<separator>[.:])\s*(?<member>[A-Za-z_][A-Za-z0-9_]*))?\s*$");
        if (!callee.Success) return null;

        var receiver = visible.FirstOrDefault(symbol => symbol.Name == callee.Groups["receiver"].Value);
        if (receiver == null || !result.Types.GetByID(receiver.Type, out var receiverType)) return null;

        var isMethod = callee.Groups["separator"].Value == ":";
        var function = callee.Groups["member"].Success
            ? MemberFunction(receiverType, callee.Groups["member"].Value, isMethod)
            : receiverType as FunctionType;
        if (function == null) return null;

        var position = new OmniSharp.Extensions.LanguageServer.Protocol.Models.Position(line - 1, col - 1);
        var index = SignatureHelpHandler.CountActiveParam(result.SourceText, position);
        if (isMethod && function.ParamNames.Count > 0 && function.ParamNames[0] == "self") index++;

        if (index < function.ParamTypes.Count) return function.ParamTypes[index];
        return function.IsVararg ? function.VarargType : null;
    }

    private static FunctionType? MemberFunction(NebraType receiver, string name, bool isMethod)
    {
        if (receiver is ClassType classType)
        {
            foreach (var current in ClassChain(classType))
            {
                var methods = isMethod ? current.Methods : current.StaticMethods;
                if (methods.TryGetValue(name, out var method)) return method;
            }
        }

        if (receiver is InterfaceType interfaceType)
        {
            var method = NebraType.BaseInterfacesOf(interfaceType).Prepend(interfaceType)
                .Select(candidate => candidate.Methods.GetValueOrDefault(name))
                .FirstOrDefault(found => found != null);
            if (method != null) return method;
        }

        return NebraType.FieldTypeOf(receiver, name) as FunctionType;
    }

    private static NebraType? TypeOf(AnalysisResult result, TypID type)
    {
        if (type == TypID.Invalid || !result.Types.GetByID(type, out var resolved)) return null;
        return resolved.Kind == TypeKind.PrimitiveAny ? null : resolved;
    }

    private static bool Contains(Diagnostics.TextSpan span, int line, int col)
    {
        if (line < span.StartLn || line > span.EndLn) return false;
        if (line == span.StartLn && col < span.StartCol) return false;
        return line != span.EndLn || col <= span.EndCol;
    }

    private static long Size(Diagnostics.TextSpan span) => (long)(span.EndLn - span.StartLn) * 100_000 + (span.EndCol - span.StartCol);
}
