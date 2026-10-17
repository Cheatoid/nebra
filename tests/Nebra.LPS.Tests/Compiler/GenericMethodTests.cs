using Nebra.Diagnostics;

namespace Nebra.LPS.Tests.Compiler;

public sealed class GenericMethodTests
{
    private static List<Diagnostic> Errors(string source) =>
        ProjectBuild.Run(config => config.Rules.StrictNil = true, ("src/main.neb", source)).Errors;

    [Fact]
    public void EveryKindOfMethodNamesItsOwnTypeParameters()
    {
        Assert.Empty(Errors("interface Store\n    method pick<T>(items: T[]): T?\nend\n\nclass Memory implements Store\n    function pick<T>(items: T[]): T?\n        return items[1]\n    end\n\n    static function empty<T>(): T[]\n        return {}\n    end\nend\n\nextend string\n    function wrap<T>(value: T): string\n        return self .. tostring(value)\n    end\nend\n\nlocal store: Store = new Memory()\nlocal picked: number? = store:pick({ 1, 2 })\nlocal none: string[] = Memory.empty()\nprint(picked, none, (\"x\"):wrap(1))\n"));
    }

    [Fact]
    public void AGenericParameterTakesAMapVariable()
    {
        Assert.Empty(Errors("class Item\n    name: string = \"\"\nend\n\nclass Registry\n    function first<T>(items: { [string]: T }, key: string): T?\n        return items[key]\n    end\nend\n\nlocal function nested<T>(groups: { [string]: { [string]: T } }, key: string): T?\n    local group = groups[key]\n    return group ~= nil and group[key] or nil\nend\n\nlocal flat: { [string]: Item } = {}\nlocal deep: { [string]: { [string]: Item } } = {}\nlocal a: Item? = new Registry():first(flat, \"x\")\nlocal b: Item? = nested(deep, \"x\")\nprint(a, b)\n"));
    }

    [Fact]
    public void AMapFitsAMapOfWiderValuesButNotOfOtherOnes()
    {
        Assert.Empty(Errors("class Item\nend\n\nlocal deep: { [string]: { [string]: Item } } = {}\nlocal loose: { [string]: { [string]: any } } = deep\nprint(loose)\n"));
        Assert.Equal(DiagnosticCode.ErrTypeMismatch, Assert.Single(Errors("local numbers: { [string]: number } = {}\nlocal strings: { [string]: string } = numbers\nprint(strings)\n")).Code);
    }
}
