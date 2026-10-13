using Nebra.LPS.Tests.Harness;

namespace Nebra.LPS.Tests.Features;

public sealed class InterfaceMethodTests
{
    private const string Greeter = """
        interface Greeter
            method greet(name: string): string
            function create(): Greeter
        end

        local function use(greeter: Greeter): nil
            {0}
        end

        print(use)
        """;

    [Fact]
    public async Task AMethodCalledWithADotIsReported()
    {
        await using var session = await LspSession.StartAsync(("src/main.neb", Greeter.Replace("{0}", "print(greeter.greet(\"x\"))")));

        var diagnostics = await session.OpenAsync("src/main.neb");

        var error = Assert.Single(diagnostics);
        Assert.Equal("ErrInterfaceMethodNeedsColon", error.Code?.String);
    }

    [Fact]
    public async Task AMethodCalledWithAColonAndAFunctionCalledWithADotAreFine()
    {
        await using var session = await LspSession.StartAsync(("src/main.neb", Greeter.Replace("{0}", "print(greeter:greet(\"x\"), greeter.create())")));

        Assert.Empty(await session.OpenAsync("src/main.neb"));
    }

    [Fact]
    public async Task AnInheritedMethodIsStillAMethod()
    {
        await using var session = await LspSession.StartAsync(("src/main.neb", """
            interface Named
                method name(): string
            end

            interface Person extends Named
                age: number
            end

            local function describe(person: Person): string
                return person.name()
            end

            print(describe)
            """));

        var diagnostics = await session.OpenAsync("src/main.neb");

        Assert.Equal("ErrInterfaceMethodNeedsColon", Assert.Single(diagnostics).Code?.String);
    }

    [Fact]
    public async Task ADefaultMethodIsInheritedByItsImplementers()
    {
        await using var session = await LspSession.StartAsync(("src/main.neb", """
            interface Named
                method name(): string
                method shout(): string
                    return self:name() .. "!"
                end
            end

            class Dog implements Named
                function name(): string
                    return "rex"
                end
            end

            local dog: Named = new Dog()
            print(dog:shout())
            """));

        Assert.Empty(await session.OpenAsync("src/main.neb"));
    }

    [Fact]
    public async Task AnyOtherWordInPlaceOfMethodIsReported()
    {
        await using var session = await LspSession.StartAsync(("src/main.neb", "interface Broken\n    {|kind|}methd run(): nil\nend\n\nprint(1)\n"));

        var diagnostics = await session.OpenAsync("src/main.neb");

        var error = Assert.Single(diagnostics);
        Assert.Equal("ErrInvalidInterfaceMemberKind", error.Code?.String);
        Assert.Equal(session.Marker("kind").Position, error.Range.Start);
    }

    [Fact]
    public async Task MethodStaysAnOrdinaryName()
    {
        await using var session = await LspSession.StartAsync(("src/main.neb", "local method = \"get\"\nlocal function call(method: string): string\n    return method\nend\n\nprint(call(method))\n"));

        Assert.Empty(await session.OpenAsync("src/main.neb"));
    }

    [Fact]
    public async Task AssertionsOfTheTestFrameworkNeedAColon()
    {
        await using var session = await LspSession.StartAsync(("src/math_test.neb", """
            import { it, expect } from "nebra:test"

            it("adds", function(): nil
                expect(1 + 1).toBe(2)
                expect(1 + 1):toBe(2)
            end)
            """));

        var diagnostics = await session.OpenAsync("src/math_test.neb");

        var error = Assert.Single(diagnostics);
        Assert.Equal("ErrInterfaceMethodNeedsColon", error.Code?.String);
        Assert.Equal(3, error.Range.Start.Line);
    }

    [Fact]
    public async Task HoverShowsHowEachMemberIsDeclared()
    {
        await using var session = await LspSession.StartAsync(("src/main.neb", Greeter.Replace("greeter: Greeter", "greeter: {|type|}Greeter").Replace("{0}", "print(greeter:greet(\"x\"))")));
        await session.OpenAsync("src/main.neb");

        var hover = await session.HoverAsync("type");

        Assert.Contains("method greet(", hover);
        Assert.Contains("function create(", hover);
    }
}
