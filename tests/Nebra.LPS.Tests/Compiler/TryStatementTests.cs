using Nebra.Configuration;
using Nebra.Diagnostics;

namespace Nebra.LPS.Tests.Compiler;

public sealed class TryStatementTests
{
    private static ProjectBuild Build(string source, LuaVersion target = LuaVersion.Lua54) =>
        ProjectBuild.Run(config =>
        {
            config.Rules.StrictNil = true;
            config.Target = target;
        }, ("src/main.neb", source));

    [Fact]
    public void ATryRunsItsBlocksThroughTheTryHelper()
    {
        var build = Build("local function load(): string\n    try\n        error(\"boom\")\n    catch (err)\n        return tostring(err)\n    finally\n        print(\"done\")\n    end\n    return \"never\"\nend\n\nprint(load())\n");

        Assert.Empty(build.Errors);
        var lua = build.Lua("src/main.neb");
        Assert.Contains("local function __nebra_try(", lua);
        Assert.Contains("return __nebra_try_return, tostring(err)", lua);
        Assert.Contains("if __try then return table.unpack(__try, 1, __try.n) end", lua);
    }

    [Fact]
    public void ABlockWithoutAReturnIsJustTheHelperCall()
    {
        var lua = Build("try\n    print(1)\nend\n").Lua("src/main.neb");

        Assert.Contains("end, nil, nil, true)", lua);
        Assert.DoesNotContain("local __try", lua);
    }

    [Fact]
    public void AFunctionReturningFromTryAndCatchAlwaysReturns()
    {
        Assert.Empty(Build("local function pick(): number\n    try\n        return 1\n    catch\n        return 2\n    end\nend\n\nprint(pick())\n").Errors);
    }

    [Theory]
    [InlineData("for i = 1, 2 do\n    try\n        break\n    end\nend\n", DiagnosticCode.ErrJumpOutOfTry)]
    [InlineData("for i = 1, 2 do\n    try\n    catch\n        continue\n    end\nend\n", DiagnosticCode.ErrJumpOutOfTry)]
    [InlineData("local function f(): nil\n    try\n    finally\n        return\n    end\nend\n", DiagnosticCode.ErrReturnInFinally)]
    [InlineData("local function f(): nil\n    try\n        defer print(1)\n    end\nend\n", DiagnosticCode.ErrDeferInTry)]
    public void ABlockCannotLeaveTheTryOtherThanByReturning(string source, DiagnosticCode code)
    {
        Assert.Equal(code, Assert.Single(Build(source).Errors).Code);
    }

    [Fact]
    public void ALoopInsideATryMayBreak()
    {
        Assert.Empty(Build("try\n    for i = 1, 3 do\n        if i > 1 then break end\n    end\nend\n").Errors);
    }

    [Fact]
    public void AwaitInsideATryNeedsLua52()
    {
        const string source = "local function instant(value: string, callback: (string) -> nil): nil\n    callback(value)\nend\n\nasync function load(): string\n    try\n        return await instant(\"x\")\n    end\n    return \"\"\nend\n\nprint(load)\n";

        Assert.Empty(Build(source).Errors);
        Assert.Equal(DiagnosticCode.ErrAwaitInTryOnLua51, Assert.Single(Build(source, LuaVersion.Lua51).Errors).Code);
    }

    [Fact]
    public void TryCatchAndFinallyStayMemberNames()
    {
        Assert.Empty(Build("local promise = { try = 1, catch = 2, finally = 3 }\nprint(promise.try, promise.catch, promise.finally)\n").Errors);
    }
}
