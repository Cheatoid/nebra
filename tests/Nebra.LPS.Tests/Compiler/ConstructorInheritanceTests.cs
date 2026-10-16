using Nebra.Compiler;
using Nebra.Configuration;
using Nebra.Diagnostics;

namespace Nebra.LPS.Tests.Compiler;

public sealed class ConstructorInheritanceTests
{
    private static List<DiagnosticCode> Errors(string source)
    {
        var root = Directory.CreateTempSubdirectory("nebra-constructors").FullName;
        var compiler = new NebraCompiler { Config = new Config { ProjectRoot = root, Source = "src" } };
        compiler.AddSource(Path.Combine(root, "src", "main.neb"), source);
        compiler.Check();
        return compiler.Diagnostics.Diagnostics
            .Where(diagnostic => diagnostic.Level == DiagnosticLevel.Error)
            .Select(diagnostic => diagnostic.Code)
            .ToList();
    }

    private const string Base = "class Format\n    constructor(pattern: string)\n        print(pattern)\n    end\nend\n";

    [Theory]
    [InlineData("local early = new Iso()\n\nclass Iso extends Format\nend\n\n" + Base)]
    [InlineData(Base + "\nclass Iso extends Format\nend\n\nlocal late = new Iso()\n")]
    public void ADerivedClassWithoutAConstructorIsBuiltWithTheInheritedOne(string source)
    {
        Assert.Equal([DiagnosticCode.ErrConstructorParamMismatch], Errors(source));
    }

    [Fact]
    public void AConstructorDeclaredBeforeItsBaseMustCallSuper()
    {
        Assert.Equal([DiagnosticCode.ErrMissingSuperCall], Errors("class Iso extends Format\n    constructor()\n        print(1)\n    end\nend\n\n" + Base));
    }

    [Theory]
    [InlineData("super()", DiagnosticCode.ErrConstructorParamMismatch)]
    [InlineData("super(42)", DiagnosticCode.ErrTypeMismatch)]
    public void SuperIsCheckedAgainstTheBaseConstructor(string call, DiagnosticCode expected)
    {
        Assert.Equal([expected], Errors("class Iso extends Format\n    constructor()\n        " + call + "\n    end\nend\n\n" + Base));
    }

    [Fact]
    public void AValidSuperCallPasses()
    {
        Assert.Empty(Errors("class Iso extends Format\n    constructor()\n        super(\"iso\")\n    end\nend\n\n" + Base + "\nprint(new Iso())\n"));
    }
}
