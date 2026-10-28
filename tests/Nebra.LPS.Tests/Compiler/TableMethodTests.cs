using Nebra.Diagnostics;

namespace Nebra.LPS.Tests.Compiler;

public sealed class TableMethodTests
{
    private const string Declarations = "declare class Entity\n    function SetModel(model: string): nil\nend\n\ndeclare class ScriptedEntity extends Entity\n    Initialize: (() -> any)?\n    Touch: ((Entity) -> any)?\nend\n\ndeclare ENT: ScriptedEntity\n\n";

    private static List<Diagnostic> Errors(string source) =>
        ProjectBuild.Run(config => config.Rules.StrictNil = true, ("src/main.neb", Declarations + source)).Errors;

    [Fact]
    public void AMethodDeclaredOnATableHasSelfTypedAsTheTable()
    {
        Assert.Empty(Errors("function ENT:Initialize()\n    self:SetModel(\"box.mdl\")\nend\n"));
        Assert.Equal(DiagnosticCode.ErrNoSuchMethod, Assert.Single(Errors("function ENT:Initialize()\n    self:SetModell(\"box.mdl\")\nend\n")).Code);
    }

    [Fact]
    public void AMethodDeclaredOnATableTakesItsParameterTypesFromTheField()
    {
        var error = Assert.Single(Errors("function ENT:Touch(other)\n    local wrong: string = other\nend\n"));

        Assert.Equal(DiagnosticCode.ErrTypeMismatch, error.Code);
        Assert.Contains("but got 'Entity'", error.Message);
    }

    [Fact]
    public void AParameterNamedLikeItsTypeDoesNotHideTheType()
    {
        Assert.Empty(Errors("local function rename(Entity: Entity): nil\n    Entity:SetModel(\"x\")\nend\n"));
    }

    [Fact]
    public void ADeclaredClassWithAConstructorNeedsNoSuperCall()
    {
        Assert.Empty(Errors("declare class Vector\n    constructor(x: number)\nend\n\ndeclare class Player extends Entity\n    constructor(index: number)\nend\n"));
    }
}
