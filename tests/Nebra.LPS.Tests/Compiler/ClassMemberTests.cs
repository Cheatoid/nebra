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

    private const string Counter = """
        class Counter
            local count: number = 0

            local function bump(by: number): number
                self.count = self.count + by
                return self.count
            end

            function add(by: number): number
                return self:bump(by)
            end
        end

        """;

    [Fact]
    public void LocalMembersArePrivateMembersOfTheInstance()
    {
        Assert.Empty(Check(Counter + "local counter = new Counter()\nprint(counter:add(2))\n"));
        Assert.Contains(Check(Counter.Replace("self.count = self.count + by", "self.count = \"many\"")), error => error.Code == DiagnosticCode.ErrTypeMismatch);
    }

    [Theory]
    [InlineData("local counter = new Counter()\nprint(counter:bump(1))\n")]
    [InlineData("local counter = new Counter()\nprint(counter.count)\n")]
    [InlineData("class Sub extends Counter\n    function peek(): number\n        return self.count\n    end\nend\n\nprint(Sub)\n")]
    public void PrivateMembersAreUnreachableOutsideTheirClass(string use)
    {
        Assert.Equal(DiagnosticCode.ErrPrivateAccess, Assert.Single(Check(Counter + use)).Code);
    }

    [Fact]
    public void AFieldTheClassDoesNotDeclareIsReported()
    {
        var errors = Check("class Date\n    constructor(value: number? = nil)\n        self.epoch = value\n    end\nend\n\nlocal date = new Date()\nprint(date.year)\n");

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

    [Fact]
    public void SelfInAStaticFunctionSaysWhy()
    {
        var error = Assert.Single(Check("class Box\n    static function make(): nil\n        print(self)\n    end\nend\n\nprint(Box)\n"));

        Assert.Equal(DiagnosticCode.ErrSelfOutsideInstance, error.Code);
        Assert.Contains("a static function", error.Message);
    }

    private const string Registry = """
        class Registry
            local static prefix: string = "id-"
            static count: number = 0

            static function next(): string
                count = count + 1
                return prefix .. tostring(count)
            end

            function label(): string
                return next()
            end
        end

        """;

    [Fact]
    public void StaticMembersAreReachableByNameInsideTheirClass()
    {
        Assert.Empty(Check(Registry + "local registry = new Registry()\nprint(Registry.next() .. registry:label())\n"));
        Assert.Contains(Check(Registry.Replace("count = count + 1", "count = \"many\"")), error => error.Code == DiagnosticCode.ErrTypeMismatch);
        Assert.Equal(DiagnosticCode.ErrPrivateAccess, Assert.Single(Check(Registry + "print(Registry.prefix)\n")).Code);
    }

    [Fact]
    public void AnAnnotatedTopLevelLocalKeepsItsTypeInsideAClass()
    {
        var errors = Check("local fallback: string = \"none\"\n\nclass Box\n    function size(): number\n        return fallback\n    end\nend\n\nprint(Box)\n");

        Assert.Equal(DiagnosticCode.ErrTypeMismatch, Assert.Single(errors).Code);
    }
}
