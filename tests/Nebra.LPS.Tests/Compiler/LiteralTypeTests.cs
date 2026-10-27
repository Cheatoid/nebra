using Nebra.Compiler;
using Nebra.Configuration;
using Nebra.Diagnostics;

namespace Nebra.LPS.Tests.Compiler;

public sealed class LiteralTypeTests
{
    private const string Events = "class LoginEvent\n    userId: string = \"\"\nend\n\ninterface AppEvents\n    \"user:login\": (LoginEvent) -> nil\n    \"user:logout\": () -> nil\n    tick: (number) -> nil\nend\n\nlocal handlers: { [string]: any } = {}\n\nlocal function on<K extends keyof AppEvents>(name: K, callback: AppEvents[K]): nil\n    handlers[name] = callback\nend\n\n";

    private static ProjectBuild Build(string source) =>
        ProjectBuild.Run(config => config.Rules.StrictNil = true, ("src/main.neb", source));

    [Fact]
    public void AKeyOfAnInterfaceTypesTheCallbackOfTheFieldItNames()
    {
        var build = Build(Events + "on(\"user:login\", function(event)\n    print(event.userId)\nend)\non(\"tick\", function(delta)\n    local seconds: number = delta\n    print(seconds)\nend)\non(\"user:logout\", function() end)\n");

        Assert.Empty(build.Errors);
    }

    [Theory]
    [InlineData("on(\"user:login\", function(event)\n    local wrong: number = event\nend)\n", "expected 'number', but got 'LoginEvent'")]
    [InlineData("on(\"tick\", function(delta: string) end)\n", "expected '(number) -> nil', but got '(string) -> nil'")]
    [InlineData("on(\"user:logn\", function() end)\n", "expected '\"user:login\" | \"user:logout\" | \"tick\"', but got '\"user:logn\"'")]
    [InlineData("local name: string = \"tick\"\non(name, function() end)\n", "but got 'string'")]
    public void AKeyOfAnInterfaceOnlyTakesItsFieldNames(string use, string message)
    {
        var error = Assert.Single(Build(Events + use).Errors);

        Assert.Equal(DiagnosticCode.ErrTypeMismatch, error.Code);
        Assert.Contains(message, error.Message);
    }

    [Theory]
    [InlineData("local mode: \"fast\" | \"slow\" = \"medium\"\n", "'\"medium\"'")]
    [InlineData("local function pick(): \"a\" | \"b\"\n    return \"c\"\nend\n", "'\"c\"'")]
    [InlineData("local level: 1 | 2 = 3\n", "'3'")]
    [InlineData("local flag: true = false\n", "'false'")]
    [InlineData("local mode: \"fast\" | \"slow\" = \"fast\"\nlocal other: \"fast\" = mode\n", "'\"fast\" | \"slow\"'")]
    [InlineData("local text: string = \"fast\"\nlocal mode: \"fast\" = text\n", "'string'")]
    public void ALiteralTypeOnlyTakesItsOwnValue(string source, string got)
    {
        var error = Assert.Single(Build(source).Errors);

        Assert.Equal(DiagnosticCode.ErrTypeMismatch, error.Code);
        Assert.Contains($"but got {got}", error.Message);
    }

    [Fact]
    public void ALiteralTypeIsUsedLikeItsBaseType()
    {
        Assert.Empty(Build("local mode: \"fast\" | \"slow\" = \"fast\"\nlocal level: 1 | 2 = 2\nlocal text: string = mode\nlocal function pick(): \"a\" | \"b\"\n    return \"a\"\nend\nprint(mode .. \"!\", #mode, level + 1, level < 2, text, pick())\n").Errors);
    }

    [Fact]
    public void AStringKeyReadsAFieldWhoseNameIsNoIdentifier()
    {
        var build = Build(Events + "local events: AppEvents = {\n    [\"user:login\"] = function(event) print(event.userId) end,\n    [\"user:logout\"] = function() end,\n    tick = function(delta) print(delta + 1) end,\n}\nlocal login: (LoginEvent) -> nil = events[\"user:login\"]\nlocal missing = events[\"nope\"]\nprint(login, missing)\n");

        var error = Assert.Single(build.Errors);
        Assert.Equal(DiagnosticCode.ErrNoSuchKey, error.Code);
        Assert.Contains("\"nope\"", error.Message);
    }

    [Theory]
    [InlineData("local value: keyof number = \"a\"\n", DiagnosticCode.ErrKeyOfNonShape)]
    [InlineData("interface Events\n    tick: number\nend\n\nlocal value: Events[\"nope\"] = 1\n", DiagnosticCode.ErrNoSuchKey)]
    public void AKeyTypeNeedsATypeWithThatField(string source, DiagnosticCode code)
    {
        Assert.Equal(code, Assert.Single(Build(source).Errors).Code);
    }

    [Fact]
    public void AKeyTypedByKeyOfIsNoListIndex()
    {
        var build = ProjectBuild.Run(config => config.Code.IndexBase = 0, ("src/main.neb", Events));

        Assert.Empty(build.Errors);
        Assert.Contains("handlers[name] = callback", build.Lua("src/main.neb"));
    }

    [Fact]
    public void GeneratedDeclarationsKeepLiteralTypesKeyOfAndQuotedFields()
    {
        var root = Directory.CreateTempSubdirectory("nebra-declgen").FullName;
        var compiler = new NebraCompiler { Config = new Config { ProjectRoot = root, Source = "src" } };
        compiler.AddSource(Path.Combine(root, "src", "init.neb"), "export interface AppEvents\n    \"user:login\": (string) -> nil\n    tick: (number) -> nil\nend\n\nexport function on<K extends keyof AppEvents>(name: K, callback: AppEvents[K]): nil\nend\n\nexport function speed(mode: \"fast\" | \"slow\", shape: { \"max-speed\": number }): number\n    return shape[\"max-speed\"]\nend\n");

        Assert.True(compiler.Compile(), string.Join("\n", compiler.Diagnostics.Diagnostics));

        var declarations = (string)compiler.Cache["GeneratedDeclarations"];
        Assert.Contains("\"user:login\": (string) -> nil", declarations);
        Assert.Contains("function on<K extends keyof AppEvents>(name: K, callback: AppEvents[K]): nil", declarations);
        Assert.Contains("function speed(mode: \"fast\" | \"slow\", shape: { \"max-speed\": number }): number", declarations);
    }
}
