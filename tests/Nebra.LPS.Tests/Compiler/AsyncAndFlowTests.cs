using Nebra.Diagnostics;

namespace Nebra.LPS.Tests.Compiler;

public sealed class AsyncAndFlowTests
{
    private static ProjectBuild Build(string main) => ProjectBuild.Run(config => config.Rules.StrictNil = true, ("src/main.neb", main));

    private const string Manager = "class Manager\n    function flush(label: string? = nil, callback: ((boolean, string?) -> void)? = nil): void\n        if callback ~= nil then\n            callback(true, label)\n        end\n    end\nend\n\nlocal manager = new Manager()\n";

    [Fact]
    public void AwaitIsAStatementAndFillsTheArgumentsBeforeAnOptionalCallback()
    {
        var build = Build(Manager + "async function run(): nil\n    local ok = await manager:flush()\n    print(ok)\n    await manager:flush(\"second\")\nend\n\nrun()\n");

        Assert.Empty(build.Errors);
        var lua = build.Lua("src/main.neb");
        Assert.Contains("_t0, nil, n = 3", lua);
        Assert.Contains(";(function() local _t1 = manager", lua);
    }

    [Fact]
    public void AssigningNilRemovesAMapEntry()
    {
        Assert.Empty(Build("local seen: { [any]: boolean } = {}\nseen[\"a\"] = true\nseen[\"a\"] = nil\nprint(seen)\n").Errors);
        Assert.Equal(DiagnosticCode.ErrTypeMismatch, Assert.Single(Build("local list: boolean[] = {}\nlist[1] = nil\nprint(list)\n").Errors).Code);
    }

    [Fact]
    public void ElseifAndElseKnowTheEarlierConditionsWereFalse()
    {
        Assert.Empty(Build("local function first(rows: any[]?): any\n    if rows == nil then\n        return nil\n    elseif rows[1] == nil then\n        return 0\n    else\n        return rows[1]\n    end\nend\n\nprint(first(nil))\n").Errors);
    }

    [Fact]
    public void AClassCallsMethodsOfAClassDeclaredLater()
    {
        Assert.Empty(Build("class Repository\n    _manager: Manager\n\n    constructor(manager: Manager)\n        self._manager = manager\n    end\n\n    function find(id: number): string\n        return self._manager:find(id)\n    end\nend\n\nclass Manager\n    function find(id: number): string\n        return tostring(id)\n    end\nend\n\nprint(new Repository(new Manager()):find(1))\n").Errors);
    }

    [Fact]
    public void AnOperandIsTheFirstValueOfACall()
    {
        Assert.Empty(Build("local function quote(name: string): string\n    return \"`\" .. string.gsub(name, \"`\", \"``\") .. \"`\"\nend\n\nlocal size: number = -select(\"#\", 1) + #string.gsub(\"a\", \"a\", \"b\")\nprint(quote(\"a\"), size)\n").Errors);
    }
}
