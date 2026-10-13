using Nebra.LPS.Tests.Harness;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;

namespace Nebra.LPS.Tests.Features;

public sealed class TypeHierarchyTests
{
    private static Task<LspSession> StartProject()
    {
        return LspSession.StartAsync(
            ("src/kinds.neb",
                "export interface {|named|}Named\n    function name(): string\nend\n\n" +
                "export interface Greeter extends Named\n    function greet(): string\nend\n"),
            ("src/main.neb",
                "import { Named, Greeter } from \"kinds\"\n\n" +
                "class {|animal|}Animal implements Named\n    function name(): string\n        return \"animal\"\n    end\nend\n\n" +
                "class Dog extends Animal implements Greeter\n    function greet(): string\n        return \"woof\"\n    end\nend\n\n" +
                "class Cat extends Animal\nend\n\n" +
                "local {|pet|}pet: Animal = new Dog()\nprint(pet:name())\n"));
    }

    [Fact]
    public async Task AClassShowsItsBaseClassAndInterfacesAboveAndItsSubclassesBelow()
    {
        await using var session = await StartProject();
        await session.OpenAsync("src/main.neb");

        var animal = await session.PrepareTypeHierarchyAsync("animal");
        Assert.NotNull(animal);
        Assert.Equal(SymbolKind.Class, animal.Kind);

        Assert.Equal(["Named"], await session.SupertypesAsync(animal));
        Assert.Equal(["Cat", "Dog"], await session.SubtypesAsync(animal));
    }

    [Fact]
    public async Task AnInterfaceShowsTheInterfacesExtendingAndClassesImplementingIt()
    {
        await using var session = await StartProject();
        await session.OpenAsync("src/kinds.neb");

        var named = await session.PrepareTypeHierarchyAsync("named");
        Assert.NotNull(named);

        Assert.Empty(await session.SupertypesAsync(named));
        Assert.Equal(["Animal", "Greeter"], await session.SubtypesAsync(named));
    }

    [Fact]
    public async Task AValueStartsTheHierarchyAtItsType()
    {
        await using var session = await StartProject();
        await session.OpenAsync("src/main.neb");

        var item = await session.PrepareTypeHierarchyAsync("pet");

        Assert.Equal("Animal", item?.Name);
    }
}
