using Nebra.Diagnostics;

namespace Nebra.LPS.Tests.Compiler;

public sealed class ConstructorOverloadTests
{
    private const string Overloaded = "class C\n    value: string | number\n\n    constructor(s: string)\n        self.value = s\n    end\n\n    constructor(n: number)\n        self.value = n\n    end\n\n    constructor()\n        self.value = -1\n    end\nend\n\n";

    private static ProjectBuild Build(string source) =>
        ProjectBuild.Run(config => config.Rules.StrictNil = true, ("src/main.neb", source));

    [Fact]
    public void ACallWhoseOverloadIsKnownCallsItDirectly()
    {
        var build = Build(Overloaded + "local anything: any = 1\nprint(new C(\"foo\"), new C(67), new C(), new C(anything))\n");

        Assert.Empty(build.Errors);
        var lua = build.Lua("src/main.neb");
        Assert.Contains("function C.new__string(s)", lua);
        Assert.Contains("function C.new__number(n)", lua);
        Assert.Contains("function C.new__none()", lua);
        Assert.Contains("function C.new(...)", lua);
        Assert.Contains("C.new__string(\"foo\")", lua);
        Assert.Contains("C.new__number(67)", lua);
        Assert.Contains("C.new__none()", lua);
        Assert.Contains("C.new(anything)", lua);
    }

    [Fact]
    public void ASuperCallPicksTheBaseConstructorItsArgumentsFit()
    {
        var build = Build(Overloaded + "class D extends C\n    constructor(s: string, label: string)\n        super(s)\n    end\n\n    constructor(n: number)\n        super(n)\n    end\nend\n\nclass E extends C\nend\n\nprint(new D(\"a\", \"b\"), new D(2), new E(5))\n");

        Assert.Empty(build.Errors);
        var lua = build.Lua("src/main.neb");
        Assert.Contains("local self = C.new__string(s)", lua);
        Assert.Contains("local self = C.new__number(n)", lua);
        Assert.Contains("setmetatable(C.new(...), E)", lua);
    }

    [Fact]
    public void ArgumentsNoConstructorTakesListTheConstructors()
    {
        var error = Assert.Single(Build(Overloaded + "print(new C(true))\n").Errors);

        Assert.Equal(DiagnosticCode.ErrNoMatchingConstructor, error.Code);
        Assert.Contains("(boolean)", error.Message);
    }

    [Fact]
    public void ALocalOverloadIsOnlyReachableFromTheClass()
    {
        const string source = "class P\n    constructor(name: string)\n    end\n\n    local constructor(flag: boolean)\n    end\n\n    static function make(): P\n        return new P(true)\n    end\nend\n\n";

        Assert.Empty(Build(source + "print(new P(\"a\"), P.make())\n").Errors);
        Assert.Equal(DiagnosticCode.ErrLocalConstructor, Assert.Single(Build(source + "print(new P(false))\n").Errors).Code);
    }

    [Fact]
    public void AFieldMustBeInitializedByEveryConstructor()
    {
        var error = Assert.Single(Build("class P\n    name: string\n\n    constructor(name: string)\n        self.name = name\n    end\n\n    constructor(count: number)\n    end\nend\n\nprint(new P(1))\n").Errors);

        Assert.Equal(DiagnosticCode.ErrUninitializedField, error.Code);
    }

    [Fact]
    public void ADeclaredClassKeepsEveryConstructor()
    {
        var build = Build("declare class Native\n    constructor(x: number)\n    constructor(x: string, y: number)\nend\n\nprint(new Native(1), new Native(\"a\", 2))\n");

        Assert.Empty(build.Errors);
        Assert.Contains("Native.new(\"a\", 2)", build.Lua("src/main.neb"));
    }
}
