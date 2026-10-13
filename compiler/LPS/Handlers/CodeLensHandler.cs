using Nebra.IR;
using Newtonsoft.Json.Linq;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;

namespace Nebra.LPS.Handlers;

/// <summary>
/// Puts a "Run test" lens on every <c>test</c> and <c>it</c> call of the <c>nebra:test</c>
/// framework, a "Run suite" lens on every <c>describe</c>, and a lens running the whole file on
/// its import of the framework. Each lens carries the file, the filter selecting its tests - the
/// names of the enclosing suites and the test, joined the way the test runner reports them - and
/// the project root <c>nebra test</c> has to run in.
/// </summary>
public sealed class CodeLensHandler(NebraWorkspace workspace) : CodeLensHandlerBase
{
    public const string RunTestCommand = "nebra.runTest";

    private const string TestModule = "nebra:test";
    private static readonly string[] TestFunctions = ["test", "it", "describe"];

    public override async Task<CodeLensContainer?> Handle(CodeLensParams request, CancellationToken ct)
    {
        var uri = request.TextDocument.Uri.ToString();
        var result = await workspace.GetResultAsync(uri, ct);
        if (result == null) return null;

        var import = result.Hir.Body.OfType<ImportStmt>().FirstOrDefault(statement => statement.Module.Name == TestModule);
        if (import == null) return new CodeLensContainer();

        var lenses = new List<CodeLens>();
        var finder = new TestFinder(import);
        var root = workspace.ProjectFor(uri).RootPath ?? Path.GetDirectoryName(result.FilePath)!;

        lenses.Add(Lens(import.Span, "Run tests in file", uri, null, root));
        foreach (var (span, isSuite, filter) in finder.Find(result.Hir.Body, []))
        {
            lenses.Add(Lens(span, isSuite ? "Run suite" : "Run test", uri, filter, root));
        }

        return new CodeLensContainer(lenses);
    }

    public override Task<CodeLens> Handle(CodeLens request, CancellationToken ct) => Task.FromResult(request);

    protected override CodeLensRegistrationOptions CreateRegistrationOptions(
        CodeLensCapability capability, ClientCapabilities clientCapabilities)
    {
        return new CodeLensRegistrationOptions
        {
            DocumentSelector = TextDocumentSelector.ForLanguage("nebra"),
            ResolveProvider = false
        };
    }

    private static CodeLens Lens(Diagnostics.TextSpan span, string title, string uri, string? filter, string root)
    {
        return new CodeLens
        {
            Range = new OmniSharp.Extensions.LanguageServer.Protocol.Models.Range(span.StartLn - 1, span.StartCol - 1, span.StartLn - 1, span.StartCol - 1),
            Command = new Command
            {
                Name = RunTestCommand,
                Title = title,
                Arguments = new JArray(uri, filter, root)
            }
        };
    }

    /// <summary>Finds the calls of the framework's functions under the names one import gives them.</summary>
    private sealed class TestFinder
    {
        private readonly Dictionary<SymID, string> _functions = [];
        private readonly SymID _namespace = SymID.Invalid;

        public TestFinder(ImportStmt import)
        {
            if (import is { Kind: ImportKind.Namespace or ImportKind.Default, Alias: { } alias })
            {
                _namespace = alias.Sym;
            }

            foreach (var specifier in import.Specifiers.Where(specifier => TestFunctions.Contains(specifier.Name.Name)))
            {
                _functions[(specifier.Alias ?? specifier.Name).Sym] = specifier.Name.Name;
            }
        }

        /// <summary>The tests and suites among <paramref name="statements"/>, descending into every suite.</summary>
        public IEnumerable<(Diagnostics.TextSpan Span, bool IsSuite, string Filter)> Find(List<Stmt> statements, IReadOnlyList<string> suites)
        {
            foreach (var statement in statements)
            {
                if (statement is not ExprStmt { Expression: FunctionCallExpr call }) continue;
                if (call.Arguments is not [StringLiteralExpr name, ..]) continue;

                var function = FunctionOf(call.Callee);
                if (function == null) continue;

                var path = suites.Append(name.Value).ToList();
                var isSuite = function == "describe";
                yield return (call.Span, isSuite, string.Join(" > ", path));

                if (!isSuite || call.Arguments is not [_, FunctionDefExpr body, ..]) continue;

                foreach (var nested in Find(body.Body, path))
                {
                    yield return nested;
                }
            }
        }

        private string? FunctionOf(Expr callee)
        {
            return callee switch
            {
                NameExpr name => _functions.GetValueOrDefault(name.Name.Sym),
                DotAccessExpr { Object: NameExpr owner } access when owner.Name.Sym == _namespace && _namespace != SymID.Invalid
                    => TestFunctions.FirstOrDefault(function => function == access.FieldName.Name),
                _ => null
            };
        }
    }
}
