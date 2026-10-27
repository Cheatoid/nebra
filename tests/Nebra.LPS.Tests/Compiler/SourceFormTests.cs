using Nebra.Diagnostics;

namespace Nebra.LPS.Tests.Compiler;

public sealed class SourceFormTests
{
    private static ProjectBuild Build(string main, params (string Name, string Source)[] others)
    {
        return ProjectBuild.Run(null, others.Append(("src/main.neb", main)).ToArray());
    }

    [Fact]
    public void AStaticFieldInitializedWithItsOwnNameReadsTheOuterValue()
    {
        var build = Build("class Meta\n    name: string = \"meta\"\nend\n\nclass Api\n    static Meta: any = Meta\n    static count: number = 1\n    static next: number = count + 1\n\n    static function describe(entry: Meta): string\n        return entry.name\n    end\nend\n\nprint(Api.describe(new Meta()), Api.next)\n");

        Assert.Empty(build.Errors);
        Assert.Contains("Api.Meta = Meta", build.Lua("src/main.neb"));
        Assert.Contains("Api.next = Api.count + 1", build.Lua("src/main.neb"));
    }

    [Fact]
    public void AStaticMemberPassesOnWhatItsClassImports()
    {
        var build = Build("import { toSnakeCase, Connection } from \"./util\"\n\nclass Api\n    static Connection: any = Connection\n    static count: number = 1\n    static next: number = count + 1\n\n    static function toSnakeCase(name: string): string\n        return toSnakeCase(name)\n    end\n\n    static function connect(url: string): Connection\n        return new Connection(url)\n    end\nend\n\nprint(Api.toSnakeCase(\"A\"), Api.connect(\"db\"), Api.next)\n",
            ("src/util.neb", "export function toSnakeCase(name: string): string\n    return string.lower(name)\nend\n\nexport class Connection\n    url: string\n\n    constructor(url: string)\n        self.url = url\n    end\nend\n"));

        Assert.Empty(build.Errors);
        var lua = build.Lua("src/main.neb");
        Assert.Contains("return toSnakeCase(name)", lua);
        Assert.Contains("return Connection.new(url)", lua);
        Assert.Contains("Api.next = Api.count + 1", lua);
    }

    [Fact]
    public void OperatorIsAKeywordOnlyWhereAClassMemberStarts()
    {
        var build = Build("class Rule\n    operator: string = \"==\"\n\n    constructor(operator: string)\n        self.operator = operator\n    end\n\n    operator ==(other: Rule): boolean\n        return self.operator == other.operator\n    end\nend\n\nlocal options = { operator = \"<\" }\nprint(new Rule(\"<\") == new Rule(options.operator))\n");

        Assert.Empty(build.Errors);
        Assert.Contains("__eq", build.Lua("src/main.neb"));
        Assert.Equal(DiagnosticCode.ErrUnexpectedToken, Assert.Single(Build("class Bad\n    operater +(other: Bad): Bad\n        return self\n    end\nend\n\nprint(Bad)\n").Errors).Code);
    }

    [Fact]
    public void FromIsAKeywordOnlyInImportsAndReExports()
    {
        var build = Build("import { from, Range } from \"./util\"\nexport { Range as Span } from \"./util\"\n\nlocal fromValue = from\nprint(fromValue, new Range():shift(2), { from = 3 })\n",
            ("src/util.neb", "export local from = 1\n\nexport class Range\n    from: number = 0\n\n    function shift(from: number): number\n        return self.from + from\n    end\nend\n"));

        Assert.Empty(build.Errors);
        Assert.Equal(DiagnosticCode.ErrUnexpectedToken, Assert.Single(Build("import { from } frm \"./util\"\n", ("src/util.neb", "export local from = 1\n")).Errors).Code);
    }

    [Fact]
    public void AnOverrideCallsTheMethodItOverridesWithSuper()
    {
        var build = Build("class Model\n    createdAt: number = 0\n\n    function onPrePersist(now: number): string\n        self.createdAt = now\n        return \"model\"\n    end\nend\n\nclass User extends Model\n    override function onPrePersist(now: number): string\n        local base = super:onPrePersist(now)\n        super:onPrePersist(now)\n        return base .. \"+user\"\n    end\nend\n\nprint(new User():onPrePersist(1))\n");

        Assert.Empty(build.Errors);
        var lua = build.Lua("src/main.neb");
        Assert.Contains("local base = Model.onPrePersist(self, now)", lua);
        Assert.Contains("\n\tModel.onPrePersist(self, now)", lua);
        Assert.Equal(DiagnosticCode.ErrSuperMethodWithoutBase, Assert.Single(Build("class Lonely\n    function test(): nil\n        super:test()\n    end\nend\n\nprint(Lonely)\n").Errors).Code);
    }

    [Fact]
    public void ACallPickingAnOverloadAtCompileTimeCallsItDirectly()
    {
        var build = Build("class A\nend\n\nclass B extends A\nend\n\nfunction pick(value: A): string\n    return \"a\"\nend\n\nfunction pick(value: B): string\n    return \"b\"\nend\n\nclass Printer\n    function put(value: number): string\n        return \"n\"\n    end\n\n    function put(value: string): string\n        return \"s\"\n    end\nend\n\nlocal unknown: any = new B()\nprint(pick(new B()), pick(unknown), new Printer():put(1))\n");

        Assert.Empty(build.Errors);
        var lua = build.Lua("src/main.neb");
        Assert.Contains("function pick__B(value)", lua);
        Assert.Contains("__nebra_is_class(__a1, \"B\")", lua);
        Assert.Contains("function Printer:put__number(value)", lua);
        Assert.Contains("print(pick__B(B.new()), pick(unknown), Printer.new():put__number(1))", lua);
    }

    [Fact]
    public void OverloadsOfTheSameParameterTypesAreRejected()
    {
        Assert.Equal(DiagnosticCode.ErrIndistinguishableOverload, Assert.Single(Build("function foo(v: string)\n    print(v)\nend\n\nfunction foo(v: string)\n    print(v)\nend\n\nfoo(\"a\")\n").Errors).Code);
        Assert.Equal(DiagnosticCode.ErrDuplicateClassMember, Assert.Single(Build("class Twice\n    function put(v: number): nil\n    end\n\n    function put(v: number): string\n        return \"\"\n    end\nend\n\nprint(Twice)\n").Errors).Code);
    }

    [Fact]
    public void AnImportedFunctionKeepsEveryOverload()
    {
        var build = Build("import { show } from \"./lib\"\n\nprint(show(1), show(\"a\"))\n",
            ("src/lib.neb", "export function show(value: number): string\n    return \"n\"\nend\n\nexport function show(value: string): string\n    return \"s\"\nend\n"));

        Assert.Empty(build.Errors);
        Assert.Contains("print(show(1), show(\"a\"))", build.Lua("src/main.neb"));
    }

    [Fact]
    public void ExtensionGettersAndOperatorsLowerToCallsOfLocalFunctions()
    {
        var build = Build("import \"./units\"\n\nprint(1.hour + 2.min, \"hello\" << 3, -\"abc\")\n",
            ("src/units.neb", "export extend number\n    get hour(): number\n        return self * 3600\n    end\n\n    get min(): number\n        return self * 60\n    end\nend\n\nexport extend string\n    operator <<(n: number): string\n        return string.sub(self, n + 1) .. string.sub(self, 1, n)\n    end\n\n    operator -(): string\n        return string.reverse(self)\n    end\nend\n"));

        Assert.Empty(build.Errors);
        var main = build.Lua("src/main.neb");
        Assert.Contains("local __ext_PrimitiveNumber_get_hour = _mod0.__ext_PrimitiveNumber_get_hour", main);
        Assert.Contains("print(__ext_PrimitiveNumber_get_hour((1)) + __ext_PrimitiveNumber_get_min((2)), __ext_PrimitiveString_shl_number(\"hello\", 3), __ext_PrimitiveString_unm(\"abc\"))", main);
        var units = build.Lua("src/units.neb");
        Assert.Contains("function __ext_PrimitiveString_shl_number(self, n)", units);
        Assert.Contains("__ext_PrimitiveString_unm = __ext_PrimitiveString_unm", units);
    }

    [Fact]
    public void AnExtensionIsOnlyUsableWhereItIsDeclaredOrImported()
    {
        var units = ("src/units.neb", "export extend number\n    get hour(): number\n        return self * 3600\n    end\nend\n\nextend number\n    get secret(): number\n        return 42\n    end\nend\n\nexport function local_(): number\n    return 1.secret\nend\n");

        Assert.Equal(DiagnosticCode.ErrExtensionNotImported, Assert.Single(Build("print(1.hour)\n", units).Errors).Code);
        Assert.Equal(DiagnosticCode.ErrExtensionNotExported, Assert.Single(Build("import \"./units\"\n\nprint(1.secret)\n", units).Errors).Code);
        Assert.Empty(Build("import \"./units\"\n\nprint(1.hour)\n", units).Errors);
    }

    [Fact]
    public void AnExtensionCannotReplaceABuiltInOperatorOrDeclareASetter()
    {
        Assert.Equal(DiagnosticCode.ErrExtensionReplacesOperator, Assert.Single(Build("extend number\n    operator +(n: number): number\n        return 0\n    end\nend\n").Errors).Code);
        Assert.Equal(DiagnosticCode.ErrExtensionSetter, Assert.Single(Build("extend number\n    set size(value: number)\n    end\nend\n").Errors).Code);
    }

    [Fact]
    public void TwoNumbersJoinedByDotsAreARange()
    {
        var build = Build("for i in 1..3 do\n    print(i)\nend\n\nlocal all: number[] = 1..3\nprint(all, 1 .. \"x\")\n");

        Assert.Empty(build.Errors);
        var lua = build.Lua("src/main.neb");
        Assert.Contains("for i = 1, 3 do", lua);
        Assert.Contains("local all = __nebra_range(1, 3)", lua);
        Assert.Contains("1 .. \"x\"", lua);
    }

    [Fact]
    public void IndexAndCallOperatorsReachTheClassThroughGeneratedMetamethods()
    {
        var build = Build("class Grid\n    cells: number[] = {}\n\n    operator [](i: number): string\n        return \"cell\"\n    end\n\n    operator ()(n: number): number\n        return n\n    end\nend\n\nlocal grid = new Grid()\nprint(grid[0], grid(1))\n");

        Assert.Empty(build.Errors);
        var lua = build.Lua("src/main.neb");
        Assert.Contains("Grid.__nebra_fields = { [\"cells\"] = true }", lua);
        Assert.Contains("return Grid.__op_index(t, k)", lua);
        Assert.Contains("Grid.__call = function(self, ...) return self:__op_call(...) end", lua);
        Assert.Contains("print(grid[0], grid(1))", lua);
        Assert.Equal(DiagnosticCode.ErrIndexWithoutAssignment, Assert.Single(Build("class Grid\n    operator [](i: number): string\n        return \"\"\n    end\nend\n\nlocal grid = new Grid()\ngrid[0] = \"x\"\n").Errors).Code);
    }

    [Fact]
    public void ExtensionIndexAndCallOperatorsLowerToCalls()
    {
        var build = Build("class Vec\n    x: number = 1\nend\n\nextend Vec\n    operator [](i: number): number\n        return self.x\n    end\n\n    operator []=(i: number, value: number)\n        self.x = value\n    end\n\n    operator ()(scale: number): number\n        return self.x * scale\n    end\nend\n\nlocal v = new Vec()\nv[0] = 2\nprint(v[0], v(3))\n");

        Assert.Empty(build.Errors);
        var lua = build.Lua("src/main.neb");
        Assert.Contains("__ext_class_Vec__op_newindex_number_number(v, 0, __value0)", lua);
        Assert.Contains("print(__ext_class_Vec__op_index_number(v, 0), __ext_class_Vec__op_call_number(v, 3))", lua);
        Assert.Equal(DiagnosticCode.ErrExtensionReplacesOperator, Assert.Single(Build("extend string\n    operator [](i: number): string\n        return \"\"\n    end\nend\n").Errors).Code);
    }

    [Fact]
    public void AnOptionalFunctionIsNotTheFunctionReturningAnOptional()
    {
        var build = Build("interface Source\n    function next(): number?\nend\n\nclass Holder\n    callback: (() -> number)? = nil\nend\n\nlocal holder = new Holder()\nholder.callback = \"x\"\n");

        var error = Assert.Single(build.Errors);
        Assert.Equal(DiagnosticCode.ErrTypeMismatch, error.Code);
        Assert.Contains("(() -> number) | nil", error.Message);
    }

    [Fact]
    public void ATableFitsAStructOrInterfaceItsFieldsSatisfy()
    {
        var shapes = "interface Point\n    X: number\n    Z: number?\nend\n\nfunction place(options: { X: number, Z: number? }): number\n    return options.X\nend\n\nfunction onPoint(point: Point): number\n    return point.X\nend\n\n";

        Assert.Empty(Build(shapes + "print(place({ X = 1, Z = 2 }), place({ X = 1 }), onPoint({ X = 3 }), onPoint({ X = 1, Z = 2, Extra = true }))\n").Errors);
        Assert.Equal(3, Build(shapes + "print(place({ Z = 2 }), place({ X = \"a\" }), onPoint({ Z = 1 }))\n").Errors.Count(error => error.Code == DiagnosticCode.ErrTypeMismatch));
    }

    [Fact]
    public void AClassOperatorPicksTheOverloadTakingTheRightOperand()
    {
        var classes = "class Point\n    x: number = 0\nend\n\nclass Num\n    v: number = 1\n\n    operator *(other: Num): Num\n        return self\n    end\n\n    operator *(other: Point): Point\n        return other\n    end\n\n    operator +(other: Num | number | string): Num\n        return self\nend\nend\n\nlocal value = new Num()\n";

        var build = Build(classes + "local squared: Num = value * value\nlocal point: Point = value * new Point()\nlocal summed: Num = value + \"2\"\nprint(squared, point, summed)\n");
        Assert.Empty(build.Errors);
        Assert.Contains("local summed = value + \"2\"", build.Lua("src/main.neb"));
        Assert.Equal(DiagnosticCode.ErrTypeMismatch, Assert.Single(Build(classes + "print(value * true)\n").Errors).Code);
    }

    [Fact]
    public void SuperCanOnlyBeCalledAsAStatementOfADerivedConstructor()
    {
        var build = Build("interface I\n    method M(): any\n        return super()\n    end\nend\n\nclass A implements I\n    constructor()\n    end\nend\n\nclass B extends A\n    constructor()\n        super()\n    end\n\n    override function M(): any\n        local i = super()\n        return i\n    end\nend\n\nprint(new B())\n");

        Assert.Equal(2, build.Errors.Count(error => error.Code == DiagnosticCode.ErrSuperOutsideConstructor));
        Assert.Equal(2, build.Errors.Count);
    }

    [Fact]
    public void ASecondConstructorIsAnError()
    {
        var build = Build("class Animal\n    constructor()\n    end\n\n    constructor(name: string)\n    end\nend\n\nprint(Animal)\n");

        Assert.Equal(DiagnosticCode.ErrDuplicateConstructor, Assert.Single(build.Errors).Code);
        Assert.Empty(Build("declare class Widget\n    constructor(path: string)\n    constructor(native: any)\nend\n\nprint(Widget)\n").Errors);
    }

    [Fact]
    public void AVarargsConstructorTakesAnyNumberOfArguments()
    {
        var build = Build("class A\nend\n\nclass B extends A\n    constructor(first: string, ...: number)\n    end\nend\n\nprint(new B(\"a\"), new B(\"a\", 1, 2))\n");

        Assert.Empty(build.Errors);
        Assert.Contains("function B.new(first, ...)", build.Lua("src/main.neb"));
        Assert.Equal(DiagnosticCode.ErrConstructorParamMismatch, Assert.Single(Build("class B\n    constructor(first: string, ...)\n    end\nend\n\nprint(new B())\n").Errors).Code);
    }

    [Fact]
    public void LoopVariablesTakeTheTypesTheIteratorProduces()
    {
        Assert.Empty(Build("local function sum(...values: number): number\n    local mut total: number = 0\n    for _, v in next, values do\n        total = total + v\n    end\n    return total\nend\n\nlocal function upTo(limit: number, current: number): number?\n    if current >= limit then return nil end\n    return current + 1\nend\n\nlocal mut steps: number = 0\nfor i in upTo, 3, 0 do\n    steps = steps + i\nend\nprint(sum(1, 2), steps)\n").Errors);
    }

    [Fact]
    public void ATableKeyedByNumbersFitsAnArray()
    {
        Assert.Empty(Build("local function foo(n: number?): nil\n    local values: number?[] = { [1] = n }\n    values[1] = nil\n    local fixed: number[] = { [1] = 5, [2] = 6 }\n    print(values, fixed)\nend\n\nfoo(1)\n").Errors);
        Assert.Equal(DiagnosticCode.ErrTypeMismatch, Assert.Single(Build("local wrong: number[] = { [1] = \"x\" }\nprint(wrong)\n").Errors).Code);
    }

    [Fact]
    public void MethodCallsFollowNewAndContinueOnTheNextLine()
    {
        var build = Build("class Builder\n    function column(name: string): Builder\n        return self\n    end\n\n    function build(): nil\n    end\nend\n\nnew Builder()\n    :column(\"a\")\n    :column(\"b\")\n    :build()\nprint(new Builder():column(\"c\"))\n");

        Assert.Empty(build.Errors);
        Assert.Contains("Builder.new():column(\"a\"):column(\"b\"):build()", build.Lua("src/main.neb"));
    }

    [Fact]
    public void ADeclarationFileInTheSourcesDeclaresGlobalsUnlessItExports()
    {
        var build = Build("print(GameApi.version)\n",
            ("src/types/globals.d.neb", "declare interface Api\n    version: string\nend\n\ndeclare GameApi: Api\n"),
            ("src/types/module.d.neb", "export declare ModuleOnly: number\n"));

        Assert.Empty(build.Errors);
        Assert.Equal(DiagnosticCode.ErrUndeclaredSymbol, Assert.Single(Build("print(ModuleOnly)\n", ("src/types/module.d.neb", "export declare ModuleOnly: number\n")).Errors).Code);
    }

    [Fact]
    public void AFunctionFitsWhenItsReturnAndParametersFit()
    {
        Assert.Empty(Build("class Column<T>\n    name: string = \"c\"\nend\n\nlocal factory: (string) -> Column<any> = function(name: string): any\n    return new Column()\nend\nlocal ignoring: (string, number) -> nil = function(name: string): nil\n    print(name)\nend\nprint(factory, ignoring)\n").Errors);
        Assert.Equal(DiagnosticCode.ErrTypeMismatch, Assert.Single(Build("local wrong: (string) -> number = function(name: string): string\n    return name\nend\nprint(wrong)\n").Errors).Code);
    }

    [Fact]
    public void OnlyNumericKeysAreShiftedByTheIndexBase()
    {
        var lua = Build("local t = { [\"a\"] = 1, [2] = \"x\" }\nprint(t[\"a\"], t[2])\n").Lua("src/main.neb");

        Assert.Contains("[\"a\"] = 1", lua);
        Assert.Contains("t[\"a\"]", lua);
        Assert.Contains("[(2 + 1)] = \"x\"", lua);
    }
}
