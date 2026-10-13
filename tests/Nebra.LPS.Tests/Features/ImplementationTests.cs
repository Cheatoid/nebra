using Nebra.LPS.Tests.Harness;

namespace Nebra.LPS.Tests.Features;

public sealed class ImplementationTests
{
    private static Task<LspSession> StartProject()
    {
        return LspSession.StartAsync(
            ("src/named.neb", "export interface {|iface|}Named\n    function {|ifaceMethod|}name(): string\nend\n"),
            ("src/people.neb",
                "import { Named } from \"named\"\n\n" +
                "export class {|person|}Person implements Named\n    function {|personName|}name(): string\n        return \"ada\"\n    end\nend\n\n" +
                "export class {|student|}Student extends Person\nend\n\n" +
                "export class {|robot|}Robot implements Named\n    function {|robotName|}name(): string\n        return \"r2\"\n    end\nend\n"),
            ("src/main.neb", "import { Named } from \"named\"\nimport { Person } from \"people\"\n\nlocal {|value|}value: Named = new Person()\nprint(value:name())\n"));
    }

    [Fact]
    public async Task AnInterfaceListsEveryClassImplementingIt()
    {
        await using var session = await StartProject();
        await session.OpenAsync("src/named.neb");

        var implementations = await session.ImplementationAsync("iface");

        Assert.Equal([session.Location("person"), session.Location("student"), session.Location("robot")], implementations);
    }

    [Fact]
    public async Task AnInterfaceMethodListsTheMethodsImplementingIt()
    {
        await using var session = await StartProject();
        await session.OpenAsync("src/named.neb");

        var implementations = await session.ImplementationAsync("ifaceMethod");

        Assert.Equal([session.Location("personName"), session.Location("robotName")], implementations);
    }

    [Fact]
    public async Task AClassListsItsSubclasses()
    {
        await using var session = await StartProject();
        await session.OpenAsync("src/people.neb");

        Assert.Equal([session.Location("student")], await session.ImplementationAsync("person"));
    }

    [Fact]
    public async Task AValueOfAnInterfaceTypeListsItsImplementations()
    {
        await using var session = await StartProject();
        await session.OpenAsync("src/main.neb");

        var implementations = await session.ImplementationAsync("value");

        Assert.Equal([session.Location("person"), session.Location("student"), session.Location("robot")], implementations);
    }
}
