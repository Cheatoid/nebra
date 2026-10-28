using Nebra.Diagnostics;
using Nebra.IR;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;

namespace Nebra.LPS.Handlers;

public sealed class SignatureHelpHandler(NebraWorkspace workspace) : SignatureHelpHandlerBase
{
    public override async Task<SignatureHelp?> Handle(SignatureHelpParams request, CancellationToken ct)
    {
        var result = await workspace.GetResultAsync(request.TextDocument.Uri.ToString(), ct);
        if (result == null) return null;

        var line = request.Position.Line + 1;
        var col = request.Position.Character + 1;

        var annotationHelp = TryAnnotationSignatureHelp(result, request.Position);
        if (annotationHelp != null) return annotationHelp;

        var call = ResolveCall(result, line, col);
        if (call == null) return null;
        var (callNode, ft, sym, calleeLabel) = call.Value;

        var activeParam = CountActiveParam(result.SourceText, request.Position);

        var paramInfos = new List<ParameterInformation>();
        List<Parameter>? declParams = null;
        Doc.DocComment? doc = null;

        if (sym != null && sym.DeclaringNode != NodeID.Invalid && result.NodeRegistry.TryGetValue(sym.DeclaringNode, out var declNode))
        {
            declParams = declNode switch
            {
                FunctionDecl fd => fd.Parameters,
                LocalFunctionDecl lfd => lfd.Parameters,
                _ => null
            };
            if (declNode is Decl declWithDoc) doc = declWithDoc.Doc;
        }

        var regularIdx = 0;
        if (declParams != null)
        {
            foreach (var dp in declParams)
            {
                if (dp.IsVararg)
                {
                    var vaType = ft.VarargType != null
                        ? workspace.FormatType(result.Types, ft.VarargType)
                        : "any";
                    var vaName = dp.Name.Name != "..." ? dp.Name.Name : "...";
                    paramInfos.Add(new ParameterInformation
                    {
                        Label = new ParameterInformationLabel($"...{vaName}: {vaType}")
                    });
                }
                else
                {
                    var pType = regularIdx < ft.ParamTypes.Count
                        ? workspace.FormatType(result.Types, ft.ParamTypes[regularIdx])
                        : "any";
                    var label2 = $"{dp.Name.Name}: {pType}";
                    if (dp.DefaultValue != null)
                        label2 += " = ...";
                    var pi = new ParameterInformation
                    {
                        Label = new ParameterInformationLabel(label2)
                    };
                    var pdesc = Doc.DocMarkdown.ParamDescription(doc, dp.Name.Name);
                    if (pdesc != null)
                        pi = new ParameterInformation
                        {
                            Label = pi.Label,
                            Documentation = new StringOrMarkupContent(new MarkupContent { Kind = MarkupKind.Markdown, Value = pdesc })
                        };
                    paramInfos.Add(pi);
                    regularIdx++;
                }
            }
        }
        else
        {
            var skipFirst = callNode is MethodCallExpr
                && ft.ParamTypes.Count > 0 && ft.ParamNames.Count > 0
                && ft.ParamNames[0] == "self";
            for (var i = skipFirst ? 1 : 0; i < ft.ParamTypes.Count; i++)
            {
                var pType = workspace.FormatType(result.Types, ft.ParamTypes[i]);
                var defaultHint = ft.DefaultParams.Contains(i) ? " = ..." : "";
                var argName = $"arg{i}";
                if (ft.ParamNames != null && i < ft.ParamNames.Count && !string.IsNullOrEmpty(ft.ParamNames[i]))
                    argName = ft.ParamNames[i];
                paramInfos.Add(new ParameterInformation
                {
                    Label = new ParameterInformationLabel($"{argName}: {pType}{defaultHint}")
                });
            }
            if (ft.IsVararg)
            {
                var vaType = ft.VarargType != null
                    ? workspace.FormatType(result.Types, ft.VarargType)
                    : "any";
                paramInfos.Add(new ParameterInformation
                {
                    Label = new ParameterInformationLabel($"...: {vaType}")
                });
            }
        }

        var retType = workspace.FormatType(result.Types, ft.ReturnType);
        var paramStr = string.Join(", ", paramInfos.Select(p => p.Label.Label));
        var label = $"{calleeLabel}({paramStr}) -> {retType}";

        var sigInfo = new SignatureInformation
        {
            Label = label,
            Parameters = new Container<ParameterInformation>(paramInfos),
            Documentation = doc != null && !doc.IsEmpty
                ? new StringOrMarkupContent(new MarkupContent
                {
                    Kind = MarkupKind.Markdown,
                    Value = Doc.DocMarkdown.Render(doc)
                })
                : null
        };

        return new SignatureHelp
        {
            Signatures = new Container<SignatureInformation>(sigInfo),
            ActiveSignature = 0,
            ActiveParameter = Math.Min(activeParam, Math.Max(0, paramInfos.Count - 1))
        };
    }

    private static ClassType? ClassOfSymbol(AnalysisResult result, SymID sym)
    {
        if (!result.Syms.GetByID(sym, out var symbol)) return null;
        return result.Types.GetByID(symbol.Type, out var type) ? type as ClassType : null;
    }

    /// <summary>
    /// The class whose declaration encloses the position, the innermost one when classes nest.
    /// </summary>
    private static ClassType? EnclosingClass(AnalysisResult result, int line, int col)
    {
        var enclosing = result.NodeRegistry.Values
            .OfType<ClassDecl>()
            .Where(decl => Contains(decl.Span, line, col))
            .MinBy(decl => (decl.Span.EndLn - decl.Span.StartLn, decl.Span.EndCol - decl.Span.StartCol));
        return enclosing == null ? null : ClassOfSymbol(result, enclosing.Name.Sym);
    }

    private static bool Contains(TextSpan span, int line, int col)
    {
        if (line < span.StartLn || line > span.EndLn) return false;
        if (line == span.StartLn && col < span.StartCol) return false;
        return line != span.EndLn || col <= span.EndCol;
    }

    /// <summary>
    /// The constructor a <c>new</c> of <paramref name="classType"/> runs: its own, or else the
    /// nearest inherited one.
    /// </summary>
    private static FunctionType? ConstructorOf(ClassType? classType)
    {
        for (var current = classType; current != null; current = current.BaseClass)
        {
            if (current.ConstructorType != null) return current.ConstructorType;
        }

        return null;
    }

    /// <summary>
    /// Looks up a method/field-as-function on the receiver expression's type,
    /// walking class inheritance and interface base graphs. Returns the
    /// <see cref="FunctionType"/> when the member exists, null otherwise.
    /// Used so signature help works on calls whose callee has no bound
    /// symbol — e.g. <c>player:GetValue(...)</c> inheriting from
    /// <c>Entity</c>, or <c>Events.CallRemote(...)</c> with side-overloaded
    /// dispatch.
    /// </summary>
    private static FunctionType? ResolveMemberAsFunction(AnalysisResult result, Expr receiver, string name, bool isStatic)
    {
        if (receiver.Type == TypID.Invalid) return null;
        if (!result.Types.GetByID(receiver.Type, out var rt)) return null;

        if (rt is UnionType u)
        {
            foreach (var m in u.Types)
                if (m.Kind != TypeKind.PrimitiveNil) { rt = m; break; }
        }

        if (rt is ClassType ct)
        {
            for (var cur = ct; cur != null; cur = cur.BaseClass)
            {
                if (isStatic && cur.StaticMethods.TryGetValue(name, out var sm)) return sm;
                if (!isStatic && cur.Methods.TryGetValue(name, out var im)) return im;
            }

            if (isStatic) return null;

            foreach (var iface in Nebra.IR.Type.ImplementedInterfaces(ct))
            {
                if (iface.Methods.TryGetValue(name, out var im)) return im;
            }
            return null;
        }
        if (rt is InterfaceType ift)
        {
            var visited = new HashSet<InterfaceType>();
            var queue = new Queue<InterfaceType>();
            queue.Enqueue(ift);
            while (queue.TryDequeue(out var cur))
            {
                if (!visited.Add(cur)) continue;
                if (cur.Methods.TryGetValue(name, out var m)) return m;
                foreach (var b in cur.BaseInterfaces) queue.Enqueue(b);
            }
            return null;
        }
        if (rt is StructType st)
        {
            var field = st.Fields.FirstOrDefault(f => f.Name.Name == name);
            return field?.Type as FunctionType;
        }
        return null;
    }

    /// <summary>
    /// Signature help inside <c>@Name(...)</c>: the annotation's parameters, with the one being
    /// written highlighted. Read from the source, because applied annotations no longer appear
    /// in the analysed program.
    /// </summary>
    private static SignatureHelp? TryAnnotationSignatureHelp(AnalysisResult result, OmniSharp.Extensions.LanguageServer.Protocol.Models.Position pos)
    {
        var call = AnnotationCatalog.CallAt(result.SourceText, pos.Line, pos.Character);
        if (call == null) return null;

        var entry = AnnotationCatalog.Find(result, call.Value.Name);
        if (entry == null) return null;

        var parameters = entry.Parameters
            .Select(parameter => new ParameterInformation { Label = new ParameterInformationLabel(AnnotationCatalog.Parameter(parameter)) })
            .ToList();

        var documentation = $"Applies to {AnnotationCatalog.TargetsText(entry)}";
        if (!string.IsNullOrEmpty(entry.Documentation)) documentation += $"\n\n{entry.Documentation}";

        return new SignatureHelp
        {
            Signatures = new Container<SignatureInformation>(new SignatureInformation
            {
                Label = AnnotationCatalog.Signature(entry),
                Parameters = new Container<ParameterInformation>(parameters),
                Documentation = new StringOrMarkupContent(new MarkupContent { Kind = MarkupKind.Markdown, Value = documentation })
            }),
            ActiveSignature = 0,
            ActiveParameter = ActiveParameter(entry, call.Value.Arguments)
        };
    }

    /// <summary>The parameter the argument being written fills: named by it, or by its position.</summary>
    private static int ActiveParameter(AnnotationEntry entry, List<string> arguments)
    {
        if (entry.TakesNames || entry.Parameters.Count == 0) return 0;

        var current = arguments[^1].Split('=');
        if (current.Length > 1)
        {
            var named = entry.Parameters.ToList().FindIndex(parameter => parameter.Name == current[0].Trim());
            if (named >= 0) return named;
        }

        return Math.Min(arguments.Count - 1, entry.Parameters.Count - 1);
    }

    internal static int CountActiveParam(string sourceText, OmniSharp.Extensions.LanguageServer.Protocol.Models.Position pos)
    {
        var lines = sourceText.Split('\n');
        if (pos.Line < 0 || pos.Line >= lines.Length) return 0;
        var lineText = lines[pos.Line].TrimEnd('\r');
        var cursor = Math.Min(pos.Character, lineText.Length);

        var depth = 0;
        var commas = 0;

        for (var i = cursor - 1; i >= 0; i--)
        {
            var ch = lineText[i];
            switch (ch)
            {
                case ')' or ']' or '}':
                    depth++;
                    break;
                case '(' or '[' or '{':
                    if (depth == 0) return commas;
                    depth--;
                    break;
                case ',' when depth == 0:
                    commas++;
                    break;
            }
        }

        return commas;
    }

    protected override SignatureHelpRegistrationOptions CreateRegistrationOptions(
        SignatureHelpCapability capability, ClientCapabilities clientCapabilities)
    {
        return new SignatureHelpRegistrationOptions
        {
            DocumentSelector = TextDocumentSelector.ForLanguage("nebra"),
            TriggerCharacters = new Container<string>("(", ",", "@", "=", " ")
        };
    }

    /// <summary>
    /// The call the cursor at <paramref name="line"/>, <paramref name="col"/> writes an argument
    /// of: its node, the signature it calls, the symbol of the callee when it has one and the
    /// name to show for it. Shared with completion, which offers what fits the parameter.
    /// </summary>
    internal static (Node Call, FunctionType Function, Symbol? Symbol, string Label)? ResolveCall(AnalysisResult result, int line, int col)
    {
        var callNode = NodeFinder.FindEnclosingCall(result.Hir, line, col);
        if (callNode == null) return null;

        var calleeSym = SymID.Invalid;
        FunctionType? ft = null;
        string calleeLabel = "fn";

        switch (callNode)
        {
            case FunctionCallExpr fce:
                if (fce.Callee is NameExpr ne) calleeSym = ne.Name.Sym;
                if (calleeSym == SymID.Invalid && fce.Callee is DotAccessExpr fcDot)
                {
                    ft = ResolveMemberAsFunction(result, fcDot.Object, fcDot.FieldName.Name, isStatic: true);
                    if (ft != null) calleeLabel = fcDot.FieldName.Name;
                }
                break;
            case MethodCallExpr mce:
                calleeSym = mce.MethodName.Sym;
                if (calleeSym == SymID.Invalid)
                {
                    ft = ResolveMemberAsFunction(result, mce.Object, mce.MethodName.Name, isStatic: false);
                    if (ft != null) calleeLabel = mce.MethodName.Name;
                }
                break;
            case NewExpr creation:
                ft = ConstructorOf(ClassOfSymbol(result, creation.ClassName.Sym));
                calleeLabel = creation.ClassName.Name;
                break;
            case SuperCallExpr:
                ft = ConstructorOf(EnclosingClass(result, line, col)?.BaseClass);
                calleeLabel = "super";
                break;
            default:
                return null;
        }

        if (ft == null && callNode is NewExpr or SuperCallExpr) return null;

        Symbol? sym = null;
        if (ft == null)
        {
            if (calleeSym == SymID.Invalid || !result.Syms.GetByID(calleeSym, out var s))
                return null;
            sym = s;
            if (!result.Types.GetByID(sym.Type, out var typ) || typ is not FunctionType bound)
                return null;
            ft = bound;
            calleeLabel = sym.Name;
        }

        return (callNode, ft, sym, calleeLabel);
    }
}
