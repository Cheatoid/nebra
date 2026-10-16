using Nebra.Compiler;
using Nebra.Configuration;
using Nebra.Diagnostics;

namespace Nebra.LPS.Tests.Compiler;

public sealed class LuaValueTests
{
    private static List<Diagnostic> Errors(string source)
    {
        var root = Directory.CreateTempSubdirectory("nebra-values").FullName;
        var config = new Config { ProjectRoot = root, Source = "src" };
        config.Rules.StrictNil = true;
        var compiler = new NebraCompiler { Config = config };
        compiler.AddSource(Path.Combine(root, "src", "main.neb"), source);
        compiler.Check();
        return compiler.Diagnostics.Diagnostics.Where(diagnostic => diagnostic.Level == DiagnosticLevel.Error).ToList();
    }

    private const string Civil = "local function civil(days: number): (number, number, number)\n    return days, 1, 2\nend\n\n";

    [Theory]
    [InlineData("local year = civil(5)\nlocal check: number = year\n")]
    [InlineData("local year: number = 0\nyear = civil(5)\nprint(year)\n")]
    [InlineData("local year, month = civil(5), 3\nlocal check: number = year + month\n")]
    public void ASingleSlotTakesTheFirstValueOfACall(string use)
    {
        Assert.Empty(Errors(Civil + use));
    }

    [Theory]
    [InlineData("local flag: boolean = true\nlocal pick: string = flag and \"yes\" or \"no\"\n")]
    [InlineData("local count: number? = nil\nlocal total: number = count or 0\n")]
    [InlineData("local count: number? = nil\nlocal label: string = count and \"set\" or \"unset\"\n")]
    [InlineData("local both: string = \"a\" and \"b\"\n")]
    public void AndAndOrAreTypedByWhatLuaReturns(string source)
    {
        Assert.Empty(Errors(source));
    }

    [Fact]
    public void OrKeepsTheFalseOfABoolean()
    {
        var error = Assert.Single(Errors("local flag: boolean = false\nlocal mixed: number = flag or 1\n"));

        Assert.Equal(DiagnosticCode.ErrTypeMismatch, error.Code);
    }

    [Fact]
    public void AReturnedTupleIsCheckedValueByValue()
    {
        Assert.Empty(Errors("local function pair(): (number?, number)\n    local first: number = 1\n    return first, 2\nend\n\nprint(pair())\n"));
        Assert.Contains(Errors("local function pair(): (number?, number)\n    return 1, \"two\"\nend\n\nprint(pair())\n"), error => error.Code == DiagnosticCode.ErrTypeMismatch);
    }

    [Fact]
    public void ANonNilAssertionIsFollowedByMembersAndMethodCalls()
    {
        Assert.Empty(Errors("class Box\n    value: number = 1\n\n    function size(): number\n        return 1\n    end\nend\n\nlocal maybe: Box? = new Box()\nprint(maybe!:size())\nprint(maybe!.value)\nmaybe!.value = 2\n"));
    }

    [Fact]
    public void KeywordsOfNebraAreMemberNames()
    {
        Assert.Empty(Errors("local key, value = string.match(\"a=b\", \"(%w+)=(%w+)\")\nprint(key, value)\nlocal options = { new = 1, match = 2 }\nprint(options.new + options.match)\n"));
    }

    [Theory]
    [InlineData("local name: string? = nil\nif name then\n    local known: string = name\n    print(known)\nend\n")]
    [InlineData("local name: string? = nil\nif not name then\n    print(\"none\")\nelse\n    local known: string = name\n    print(known)\nend\n")]
    [InlineData("local name: string? = nil\nlocal count: number? = nil\nif name and count then\n    local total: number = count\n    print(total, name)\nend\n")]
    [InlineData("local label: string = \"x\"\nlocal score: number = 1\nif label and score then\n    print(label)\nend\n")]
    public void AnyValueIsAConditionAndATestedValueIsNotNil(string source)
    {
        Assert.Empty(Errors(source));
    }
}
