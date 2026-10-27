using Nebra.Diagnostics;

namespace Nebra.LPS.Tests.Compiler;

public sealed class ContextualTypingTests
{
    private static List<Diagnostic> Errors(string source) =>
        ProjectBuild.Run(config => config.Rules.StrictNil = true, ("src/main.neb", source)).Errors;

    [Theory]
    [InlineData("local function on(name: string, callback: (number) -> nil): nil\n    callback(1)\nend\n\non(\"x\", function(value)\n    local wrong: string = value\nend)\n", "number")]
    [InlineData("local function map<T, U>(items: T[], f: (T) -> U): U[]\n    local out: U[] = {}\n    for i, item in ipairs(items) do\n        out[i] = f(item)\n    end\n    return out\nend\n\nlocal labels = map({ 1, 2 }, function(item)\n    return tostring(item + 1)\nend)\nlocal wrong: number = labels\n", "string[]")]
    [InlineData("local handler: (string, number) -> nil = function(name, count)\n    local wrong: boolean = count\nend\n", "number")]
    [InlineData("interface Handlers\n    onLogin: (string) -> nil\nend\n\nlocal handlers: Handlers = { onLogin = function(user)\n    local wrong: number = user\nend }\n", "string")]
    [InlineData("class Button\n    onClick: ((number) -> nil)? = nil\nend\n\nlocal button = new Button()\nbutton.onClick = function(x)\n    local wrong: string = x\nend\n", "number")]
    public void AnUnannotatedParameterTakesItsTypeFromTheExpectedFunctionType(string source, string got)
    {
        var error = Assert.Single(Errors(source));

        Assert.Equal(DiagnosticCode.ErrTypeMismatch, error.Code);
        Assert.Contains($"but got '{got}'", error.Message);
    }

    [Fact]
    public void AnnotatedParametersKeepTheirOwnTypeAndAreChecked()
    {
        var error = Assert.Single(Errors("local function on(callback: (number) -> nil): nil\n    callback(1)\nend\n\non(function(value: string) end)\n"));

        Assert.Equal(DiagnosticCode.ErrTypeMismatch, error.Code);
        Assert.Contains("'(number) -> nil'", error.Message);
    }

    [Fact]
    public void AFunctionLiteralWithoutAnExpectedTypeStillTakesAnything()
    {
        Assert.Empty(Errors("local callback: any = function(value)\n    print(value + 1, value .. \"\")\nend\nlocal untyped = function(value)\n    return value\nend\nprint(callback, untyped(1))\n"));
    }
}
