using Nebra.IR;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Type = Nebra.IR.Type;

namespace Nebra.LPS.Handlers;

/// <summary>
/// Shows what the source leaves implicit: the inferred type of a local declared without an
/// annotation, and the parameter name each call argument binds to - left out where the argument
/// is a variable of that same name.
/// </summary>
public sealed class InlayHintHandler(NebraWorkspace workspace) : InlayHintsHandlerBase
{
    public override async Task<InlayHintContainer?> Handle(InlayHintParams request, CancellationToken ct)
    {
        var result = await workspace.GetResultAsync(request.TextDocument.Uri.ToString(), ct);
        if (result == null) return null;

        var settings = workspace.Settings;
        var hints = new List<InlayHint>();
        foreach (var node in NodeFinder.BuildNodeRegistry(result.Hir).Values)
        {
            if (!Overlaps(node, request.Range)) continue;

            switch (node)
            {
                case LocalDecl local when settings.InlayTypeHints:
                    hints.AddRange(TypeHints(result, local));
                    break;
                case FunctionCallExpr call when settings.InlayParameterHints:
                    hints.AddRange(ParameterHints(Signature(result, call.Callee), call.Arguments));
                    break;
                case MethodCallExpr call when settings.InlayParameterHints:
                    hints.AddRange(ParameterHints(MethodSignature(result, call), call.Arguments));
                    break;
                case NewExpr creation when settings.InlayParameterHints:
                    hints.AddRange(ParameterHints(ConstructorSignature(result, creation), creation.Arguments));
                    break;
            }
        }

        return new InlayHintContainer(hints.OrderBy(hint => hint.Position.Line).ThenBy(hint => hint.Position.Character));
    }

    private IEnumerable<InlayHint> TypeHints(AnalysisResult result, LocalDecl local)
    {
        foreach (var variable in local.Variables)
        {
            if (variable.TypeAnnotation != null || variable.Name.Sym == SymID.Invalid) continue;
            if (!result.Snapshot.TryGetSymbol(variable.Name.Sym, out var symbol)) continue;
            if (!result.Types.GetByID(symbol.Type, out var type) || IsUninformative(type)) continue;

            yield return new InlayHint
            {
                Position = NebraWorkspace.SpanToRange(variable.Name.Span).End,
                Label = ": " + workspace.FormatType(result.Types, type),
                Kind = InlayHintKind.Type
            };
        }
    }

    private static IEnumerable<InlayHint> ParameterHints(FunctionType? signature, List<Expr> arguments)
    {
        if (signature == null) yield break;

        var offset = signature.ParamNames.Count > 0 && signature.ParamNames[0] == "self" ? 1 : 0;
        for (var index = 0; index < arguments.Count; index++)
        {
            var parameterIndex = index + offset;
            if (parameterIndex >= signature.ParamNames.Count) yield break;

            var name = signature.ParamNames[parameterIndex];
            if (string.IsNullOrEmpty(name) || name == "...") continue;
            if (arguments[index] is NameExpr argument && argument.Name.Name == name) continue;

            yield return new InlayHint
            {
                Position = NebraWorkspace.SpanToRange(arguments[index].Span).Start,
                Label = name + ":",
                Kind = InlayHintKind.Parameter,
                PaddingRight = true
            };
        }
    }

    private static FunctionType? Signature(AnalysisResult result, Expr callee)
    {
        return result.Types.GetByID(callee.Type, out var type) ? Unwrap(type) as FunctionType : null;
    }

    private static FunctionType? MethodSignature(AnalysisResult result, MethodCallExpr call)
    {
        return result.Types.GetByID(call.Object.Type, out var receiver)
            ? MemberResolver.FindMethod(receiver, call.MethodName.Name)
            : null;
    }

    private static FunctionType? ConstructorSignature(AnalysisResult result, NewExpr creation)
    {
        if (!result.Snapshot.TryGetSymbol(creation.ClassName.Sym, out var symbol)) return null;
        return result.Types.GetByID(symbol.Type, out var type) && type is ClassType classType
            ? classType.ConstructorType
            : null;
    }

    private static Type Unwrap(Type type)
    {
        if (type is not UnionType union) return type;
        var present = union.Types.Where(member => member.Kind != TypeKind.PrimitiveNil).ToList();
        return present.Count == 1 ? present[0] : type;
    }

    private static bool IsUninformative(Type type)
    {
        return type.Kind is TypeKind.PrimitiveAny or TypeKind.PrimitiveNil;
    }

    private static bool Overlaps(Node node, OmniSharp.Extensions.LanguageServer.Protocol.Models.Range range)
    {
        var startLine = node.Span.StartLn - 1;
        var endLine = node.Span.EndLn - 1;
        return endLine >= range.Start.Line && startLine <= range.End.Line;
    }

    public override Task<InlayHint> Handle(InlayHint request, CancellationToken ct)
    {
        return Task.FromResult(request);
    }

    protected override InlayHintRegistrationOptions CreateRegistrationOptions(
        InlayHintClientCapabilities capability, ClientCapabilities clientCapabilities)
    {
        return new InlayHintRegistrationOptions
        {
            DocumentSelector = TextDocumentSelector.ForLanguage("nebra"),
            ResolveProvider = false
        };
    }
}
