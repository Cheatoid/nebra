using Nebra.Diagnostics;

namespace Nebra.LPS.Tests.Compiler;

public sealed class EnumShorthandTests
{
    private const string Enums = "enum Color\n    RED\n    GREEN = 5\n    BLUE\nend\n\nenum Mode\n    FAST = \"fast\"\n    SLOW = \"slow\"\nend\n\n";

    private static ProjectBuild Build(string source) =>
        ProjectBuild.Run(config => config.Rules.StrictNil = true, ("src/main.neb", Enums + source));

    [Theory]
    [InlineData("local color: Color = GREEN\nprint(color)\n", "local color = 5")]
    [InlineData("local color: Color = RED\ncolor = BLUE\nprint(color)\n", "color = 6")]
    [InlineData("local function paint(color: Color, mode: Mode): nil\nend\npaint(GREEN, SLOW)\n", "paint(5, \"slow\")")]
    [InlineData("local function pick(): Mode\n    return FAST\nend\nprint(pick())\n", "return \"fast\"")]
    [InlineData("local color: Color = RED\nprint(color == BLUE)\n", "color == 6")]
    [InlineData("local shape: { color: Color } = { color = BLUE }\nprint(shape)\n", "color = 6")]
    public void AMemberNameStandsForTheEnumExpectedThere(string source, string lua)
    {
        var build = Build(source);

        Assert.Empty(build.Errors);
        Assert.Contains(lua, build.Lua("src/main.neb"));
    }

    [Fact]
    public void AMatchOnAnEnumTakesItsMemberNamesAndCountsThemAsCovered()
    {
        var build = Build("local function name(color: Color): string\n    return match color\n        case RED then \"red\"\n        case GREEN then \"green\"\n        case BLUE then \"blue\"\n    end\nend\nprint(name(RED))\n");

        Assert.Empty(build.Errors);
    }

    [Fact]
    public void ADeclarationInScopeWinsOverAMemberName()
    {
        var build = Build("local RED = \"local\"\nlocal value: string = RED\nprint(value)\n");

        Assert.Empty(build.Errors);
        Assert.Contains("local value = RED", build.Lua("src/main.neb"));
    }

    [Theory]
    [InlineData("local color = RED\n", "RED")]
    [InlineData("local color: Color = PURPLE\n", "PURPLE")]
    [InlineData("local mode: Mode = RED\n", "RED")]
    public void ANameNoExpectedEnumHasIsStillUndeclared(string source, string name)
    {
        var error = Assert.Single(Build(source).Errors);

        Assert.Equal(DiagnosticCode.ErrUndeclaredSymbol, error.Code);
        Assert.Contains(name, error.Message);
    }

    [Fact]
    public void AMemberOfADeclaredEnumIsReadFromItsTable()
    {
        var build = ProjectBuild.Run(null, ("src/main.neb", "declare enum CollisionType\n    Normal\n    NoCollision\nend\n\nlocal collision: CollisionType = NoCollision\nprint(collision)\n"));

        Assert.Empty(build.Errors);
        Assert.Contains("local collision = CollisionType.NoCollision", build.Lua("src/main.neb"));
    }
}
