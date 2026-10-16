using Nebra.Compiler;
using Nebra.Configuration;
using Nebra.Diagnostics;

namespace Nebra.LPS.Tests.Compiler;

public sealed class FieldDefaultTests
{
    private static List<Diagnostic> Errors(string source, bool strictNil = false)
    {
        var root = Directory.CreateTempSubdirectory("nebra-fields").FullName;
        var config = new Config { ProjectRoot = root, Source = "src" };
        config.Rules.StrictNil = strictNil;
        var compiler = new NebraCompiler { Config = config };
        compiler.AddSource(Path.Combine(root, "src", "main.neb"), source);
        compiler.Check();
        return compiler.Diagnostics.Diagnostics.Where(diagnostic => diagnostic.Code == DiagnosticCode.ErrTypeMismatch).ToList();
    }

    [Fact]
    public void AFieldDefaultOfTheWrongTypeIsRejected()
    {
        Assert.Equal(2, Errors("class C\n    x: number = \"text\"\n    static y: string = 5\nend\nprint(C)\n").Count);
    }

    [Fact]
    public void AFieldMayOnlyDefaultToNilWhenItIsNilableUnderStrictNil()
    {
        var source = "class Model\n    createdAt: string = nil\n    deletedAt: string? = nil\n    updatedAt: string = nil!\nend\nprint(Model)\n";

        Assert.Single(Errors(source, strictNil: true));
        Assert.Empty(Errors(source));
    }

    [Fact]
    public void AnEmptyTableIsAnEmptyListOrMap()
    {
        var source = "local names: string[] = {}\nlocal lookup: { [string]: number } = {}\nclass C\n    entries: { [string]: string } = {}\nend\nlookup = {}\nprint(names, lookup, C)\n";

        Assert.Empty(Errors(source));
        Assert.Single(Errors("local point: { x: number } = {}\nprint(point)\n"));
    }
}
