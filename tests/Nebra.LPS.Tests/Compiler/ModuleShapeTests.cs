using Nebra.Diagnostics;

namespace Nebra.LPS.Tests.Compiler;

public sealed class ModuleShapeTests
{
    private static ProjectBuild Build(string main, params (string Name, string Source)[] others)
    {
        return ProjectBuild.Run(config => config.Rules.StrictNil = true, others.Append(("src/main.neb", main)).ToArray());
    }

    private const string User = "import type { House } from \"./House\"\n\nexport class User\n    name: string = \"user\"\n    houses: House[] = {}\nend\n";
    private const string House = "import type { User } from \"./User\"\n\nexport class House\n    street: string = \"main\"\n    owner: User? = nil\nend\n";

    [Fact]
    public void TypeImportsMayFormACycleAndLoadNothing()
    {
        var build = Build("import { User } from \"./User\"\nimport { House } from \"./House\"\n\nlocal user = new User()\nlocal house = new House()\nhouse.owner = user\nuser.houses[1] = house\nlocal street: string = user.houses[1].street\nprint(street)\n",
            ("src/User.neb", User), ("src/House.neb", House));

        Assert.Empty(build.Errors);
        Assert.DoesNotContain("require", build.Lua("src/User.neb"));
    }

    [Fact]
    public void ATypeImportCannotBeUsedAsAValue()
    {
        var error = Assert.Single(Build("import type { House } from \"./House\"\n\nprint(new House())\n", ("src/House.neb", "export class House\nend\n")).Errors);

        Assert.Equal(DiagnosticCode.ErrTypeImportUsedAsValue, error.Code);
    }

    [Fact]
    public void AnImportCycleOfValuesIsStillReported()
    {
        var errors = Build("print(1)\n", ("src/a.neb", "import { b } from \"./b\"\n\nexport local a = 1\nprint(b)\n"), ("src/b.neb", "import { a } from \"./a\"\n\nexport local b = 2\nprint(a)\n")).Errors;

        Assert.Contains(errors, error => error.Code == DiagnosticCode.ErrTopLevelCycle);
    }

    [Fact]
    public void TopLevelClassesAndLocalFunctionsAreDeclaredBeforeTheFirstStatement()
    {
        var lua = Build("class Collection\n    function describe(): string\n        return helper(\"items\") .. Registry.name()\n    end\nend\n\nlocal function helper(text: string): string\n    return \"[\" .. text .. \"]\"\nend\n\nclass Registry\n    static function name(): string\n        return \"registry\"\n    end\nend\n\nprint(new Collection():describe())\n").Lua("src/main.neb");

        Assert.Contains("local Collection, helper, Registry", lua);
        Assert.Contains("\nCollection = {}", lua);
        Assert.Contains("\nfunction helper(text)", lua);
    }

    [Fact]
    public void AValueWithAnIteratorMethodIsIteratedThroughIt()
    {
        var build = Build("class Collection<T>\n    items: T[] = {}\n\n    function iterator(): () -> (number?, T)\n        local index = 0\n        return function(): (number?, T)\n            index = index + 1\n            return index, self.items[index]\n        end\n    end\nend\n\nlocal names: Collection<string> = new Collection()\nfor index, name in names do\n    local upper: string = string.upper(name)\n    local position: number = index\n    print(position, upper)\nend\n");

        Assert.Empty(build.Errors);
        Assert.Contains("for index, name in names:iterator() do", build.Lua("src/main.neb"));
    }

    [Fact]
    public void AnErrorInAnAsyncFunctionKeepsItsOwnPosition()
    {
        var lua = Build("async function fail(): nil\n    error(\"boom\")\nend\n\nfail()\n").Lua("src/main.neb");

        Assert.Contains("error(payload, 0)", lua);
    }
}
