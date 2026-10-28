using Nebra.Diagnostics;

namespace Nebra.LPS.Tests.Compiler;

public sealed class DeclarationMergingTests
{
    private const string Extension = "declare interface MathLib\n    function Clamp(value: number, min: number, max: number): number\nend\n";

    [Fact]
    public void ADeclaredInterfaceAddsToTheInterfaceOfTheSameName()
    {
        var build = ProjectBuild.Run(null, ("src/game.d.neb", Extension), ("src/main.neb", "local clamped: number = math.Clamp(5, 0, 1)\nlocal floored: number = math.floor(2.5)\nprint(clamped, floored)\n"));

        Assert.Empty(build.Errors);
        Assert.Contains("math.Clamp(5, 0, 1)", build.Lua("src/main.neb"));
    }

    [Fact]
    public void TheAddedMembersAreChecked()
    {
        var build = ProjectBuild.Run(null, ("src/game.d.neb", Extension), ("src/main.neb", "local clamped: string = math.Clamp(5, 0, 1)\nprint(math.Clampp(1), clamped)\n"));

        Assert.Equal([DiagnosticCode.ErrTypeMismatch, DiagnosticCode.ErrNoSuchMember], build.Errors.Select(error => error.Code).OrderBy(code => code));
    }

    [Fact]
    public void AnInterfaceWrittenInSourceIsStillDeclaredOnce()
    {
        var build = ProjectBuild.Run(null, ("src/main.neb", "interface Shape\n    method area(): number\nend\n\ninterface Shape\n    method name(): string\nend\n"));

        Assert.Equal(DiagnosticCode.ErrRedeclaration, Assert.Single(build.Errors).Code);
    }
}
