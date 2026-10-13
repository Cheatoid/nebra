using Nebra.LPS.Tests.Harness;

namespace Nebra.LPS.Tests.Features;

public sealed class QuickFixTests
{
    private const string StrictConfig =
        "name = \"lps-test\"\nversion = \"0.1.0\"\ntarget = \"5.4\"\nsource = \"src\"\noutput = \"out\"\n\n[rules]\nimmutable_default = true\nstrict_nil = true\n";

    /// <summary>
    /// Opens <paramref name="path"/>, applies the quick fix titled <paramref name="title"/>, sends
    /// the fixed text back and checks that the diagnostic it fixed is gone. Returns the changed files.
    /// </summary>
    private static async Task<IReadOnlyDictionary<string, string>> ApplyFix(LspSession session, string path, string code, string title)
    {
        var diagnostics = await session.OpenAsync(path);
        var diagnostic = Assert.Single(diagnostics, candidate => candidate.Code?.String == code);
        var actions = await session.CodeActionsAsync(path, [diagnostic]);
        var fix = Assert.Single(actions, action => action.Title == title);
        var changed = session.Apply(fix.Edit);

        var recheck = changed.ContainsKey(path) ? null : session.NextDiagnostics(path);
        var version = 2;
        IReadOnlyList<OmniSharp.Extensions.LanguageServer.Protocol.Models.Diagnostic> after = [];
        foreach (var (file, text) in changed)
        {
            var published = await session.ChangeAsync(file, text, version++);
            if (file == path) after = published;
        }

        if (recheck != null) after = await recheck;
        Assert.DoesNotContain(after, candidate => candidate.Code?.String == code);
        Assert.DoesNotContain(after, candidate => candidate.Severity == OmniSharp.Extensions.LanguageServer.Protocol.Models.DiagnosticSeverity.Error);
        return changed;
    }

    [Fact]
    public async Task AnAssignmentToAnImmutableLocalMakesItMutable()
    {
        await using var session = await LspSession.StartAsync(
            ("nebra.toml", StrictConfig),
            ("src/main.neb", "local total: number = 0\ntotal = 1\nprint(total)\n"));

        var changed = await ApplyFix(session, "src/main.neb", "ErrAssignToImmutable", "Make 'total' mutable");

        Assert.Equal("local mut total: number = 0\ntotal = 1\nprint(total)\n", changed["src/main.neb"]);
    }

    [Fact]
    public async Task AShadowingMethodGetsOverride()
    {
        await using var session = await LspSession.StartAsync(
            ("src/main.neb",
                "class Animal\n    function speak(): string\n        return \"...\"\n    end\nend\n\nclass Cat extends Animal\n    function speak(): string\n        return \"meow\"\n    end\nend\n"));

        var changed = await ApplyFix(session, "src/main.neb", "WarnMissingShadowOverride", "Mark the method 'override'");

        Assert.Contains("class Cat extends Animal\n    override function speak(): string", changed["src/main.neb"]);
    }

    [Fact]
    public async Task AnOverrideWithoutParentLosesTheKeyword()
    {
        await using var session = await LspSession.StartAsync(
            ("src/main.neb", "class Cat\n    override function speak(): string\n        return \"meow\"\n    end\nend\n"));

        var changed = await ApplyFix(session, "src/main.neb", "ErrOverrideNoParent", "Remove 'override'");

        Assert.Equal("class Cat\n    function speak(): string\n        return \"meow\"\n    end\nend\n", changed["src/main.neb"]);
    }

    [Fact]
    public async Task AnAbstractMethodMakesItsClassAbstract()
    {
        await using var session = await LspSession.StartAsync(
            ("src/main.neb", "class Shape\n    abstract function area(): number\nend\n"));

        var changed = await ApplyFix(session, "src/main.neb", "ErrAbstractInNonAbstractClass", "Make 'Shape' abstract");

        Assert.Equal("abstract class Shape\n    abstract function area(): number\nend\n", changed["src/main.neb"]);
    }

    [Fact]
    public async Task AnInstanceMethodCalledWithADotGetsAColon()
    {
        await using var session = await LspSession.StartAsync(
            ("src/main.neb",
                "class Counter\n    value: number = 0\n\n    function bump(): number\n        return self.value + 1\n    end\nend\n\nlocal counter = new Counter()\nprint(counter.bump())\n"));

        var changed = await ApplyFix(session, "src/main.neb", "ErrInstanceMethodNeedsColon", "Call the method with ':'");

        Assert.EndsWith("print(counter:bump())\n", changed["src/main.neb"]);
    }

    [Fact]
    public async Task AnInterfaceMethodCalledWithADotIsCalledWithAColon()
    {
        await using var session = await LspSession.StartAsync(
            ("src/main.neb",
                "interface Named\n    method name(): string\nend\n\nlocal function show(named: Named): nil\n    print(named.name())\nend\n\nprint(show)\n"));

        var changed = await ApplyFix(session, "src/main.neb", "ErrInterfaceMethodNeedsColon", "Call the method with ':'");

        Assert.Contains("print(named:name())", changed["src/main.neb"]);
    }

    [Theory]
    [InlineData("Access it with '?.'", "print(found?.size)\n")]
    [InlineData("Assert that it is not nil with '!'", "print((found!).size)\n")]
    public async Task APossiblyNilValueCanBeGuarded(string title, string expectedEnd)
    {
        await using var session = await LspSession.StartAsync(
            ("nebra.toml", StrictConfig),
            ("src/main.neb",
                "class Box\n    size: number = 1\nend\n\nlocal function find(): Box?\n    return nil\nend\n\nlocal found = find()\nprint(found.size)\n"));

        var changed = await ApplyFix(session, "src/main.neb", "ErrPossiblyNil", title);

        Assert.EndsWith(expectedEnd, changed["src/main.neb"]);
    }

    [Fact]
    public async Task ANameAnotherModuleDoesNotExportGetsExportedThere()
    {
        await using var session = await LspSession.StartAsync(
            ("src/lib.neb", "local function helper(): number\n    return 1\nend\n\nprint(helper())\n"),
            ("src/main.neb", "import { helper } from \"lib\"\n\nprint(helper())\n"));

        await session.PublishedDiagnostics("src/lib.neb");
        var changed = await ApplyFix(session, "src/main.neb", "ErrSymbolNotExported", "Export 'helper' from 'lib'");

        Assert.StartsWith("export local function helper(): number", changed["src/lib.neb"]);
    }

    [Fact]
    public async Task AMismatchedAnnotationTakesTheValuesType()
    {
        await using var session = await LspSession.StartAsync(
            ("src/main.neb", "local count: number = \"three\"\nprint(count)\n"));

        var changed = await ApplyFix(session, "src/main.neb", "ErrTypeMismatch", "Change the annotation to 'string'");

        Assert.Equal("local count: string = \"three\"\nprint(count)\n", changed["src/main.neb"]);
    }
}
