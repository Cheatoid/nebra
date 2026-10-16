using Nebra.Compiler;
using Nebra.Configuration;
using Nebra.Diagnostics;

namespace Nebra.LPS.Tests.Compiler;

public sealed class ClassMemberTests
{
    private static List<Diagnostic> Check(string source, bool strictNil = false)
    {
        var root = Directory.CreateTempSubdirectory("nebra-members").FullName;
        var config = new Config { ProjectRoot = root, Source = "src" };
        config.Rules.StrictNil = strictNil;
        var compiler = new NebraCompiler { Config = config };
        compiler.AddSource(Path.Combine(root, "src", "main.neb"), source);
        compiler.Check();
        return compiler.Diagnostics.Diagnostics.Where(diagnostic => diagnostic.Level == DiagnosticLevel.Error).ToList();
    }

    [Fact]
    public void LocalFieldsAndFunctionsAreReachedByNameAndTyped()
    {
        var source = """
            class Box
                size: number = 0
                local count: number = 0

                local function clamp(value: number): number
                    return value + count
                end

                function grow(by: number): nil
                    count = count + 1
                    self.size = clamp(by)
                end
            end

            print(new Box())
            """;

        Assert.Empty(Check(source));
        Assert.Contains(Check(source.Replace("count = count + 1", "count = \"many\"")), error => error.Code == DiagnosticCode.ErrTypeMismatch);
        Assert.Contains(Check(source.Replace("return value + count", "return \"text\"")), error => error.Code == DiagnosticCode.ErrTypeMismatch);
    }

    [Fact]
    public void AFieldTheClassDoesNotDeclareIsReported()
    {
        var errors = Check("class Date\n    local epoch: number = 0\n\n    constructor(value: number? = nil)\n        self.epoch = value\n    end\nend\n\nlocal date = new Date()\nprint(date.year)\n");

        var missing = errors.Where(error => error.Code == DiagnosticCode.ErrNoSuchField).Select(error => error.Message).ToList();
        Assert.Equal(2, missing.Count);
        Assert.Contains(missing, message => message.Contains("'epoch'"));
        Assert.Contains(missing, message => message.Contains("'year'"));
    }

    [Fact]
    public void ADeclaredFieldIsCheckedOnAssignment()
    {
        var errors = Check("class Date\n    epoch: number = 0\n\n    constructor(value: number? = nil)\n        self.epoch = value\n    end\nend\n\nprint(new Date())\n", strictNil: true);

        Assert.Equal(DiagnosticCode.ErrTypeMismatch, Assert.Single(errors).Code);
    }

    [Theory]
    [InlineData("static function make(): nil", "a static function")]
    [InlineData("local function helper(): nil", "a local function of a class")]
    public void SelfOutsideAnInstanceMethodSaysWhy(string header, string where)
    {
        var error = Assert.Single(Check($"class Box\n    {header}\n        print(self)\n    end\nend\n\nprint(Box)\n"));

        Assert.Equal(DiagnosticCode.ErrSelfOutsideInstance, error.Code);
        Assert.Contains(where, error.Message);
    }
}
