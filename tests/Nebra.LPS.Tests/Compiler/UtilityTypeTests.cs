using Nebra.Compiler;
using Nebra.Configuration;
using Nebra.Diagnostics;

namespace Nebra.LPS.Tests.Compiler;

public sealed class UtilityTypeTests
{
    private const string Types = "class User\n    name: string = \"\"\n    email: string = \"\"\n    age: number = 0\n    nickname: string? = nil\nend\n\ninterface Timestamps\n    createdAt: number\n    updatedAt: number\nend\n\ninterface Record\n    id: number\n    createdAt: number\n    updatedAt: number\n    title: string\nend\n\nlocal user = new User()\n\n";

    private static ProjectBuild Build(string source) =>
        ProjectBuild.Run(config => config.Rules.StrictNil = true, ("src/main.neb", Types + source));

    [Fact]
    public void UtilityTypesMakeStructsOfTheFieldsOfAType()
    {
        var build = Build("local function update(changes: Partial<User>): nil\n    user.name = changes.name ?? user.name\nend\n\nlocal function contact(person: Pick<User, \"name\" | \"email\">): string\n    return person.name .. person.email\nend\n\nlocal function draft(values: Omit<Record, keyof Timestamps>): string\n    return values.title .. tostring(values.id)\nend\n\nlocal function complete(person: Required<User>): string\n    return person.nickname\nend\n\nupdate({ age = 31 })\nupdate({})\nprint(contact({ name = \"a\", email = \"b\" }), draft({ id = 1, title = \"t\" }), complete)\n");

        Assert.Empty(build.Errors);
    }

    [Theory]
    [InlineData("local changes: Partial<User> = { age = \"31\" }\n", "expected 'Partial<User>', but got '{ age: string }'")]
    [InlineData("local person: Pick<User, \"name\" | \"email\"> = { name = \"a\" }\n", "expected 'Pick<User, \"name\" | \"email\">'")]
    [InlineData("local person: Required<User> = { name = \"a\", email = \"b\", age = 1 }\n", "expected 'Required<User>'")]
    public void AValueMustFitTheStructAUtilityTypeMakes(string source, string message)
    {
        var error = Assert.Single(Build(source).Errors);

        Assert.Equal(DiagnosticCode.ErrTypeMismatch, error.Code);
        Assert.Contains(message, error.Message);
    }

    [Theory]
    [InlineData("local values: Omit<Record, keyof Timestamps> = { id = 1, title = \"x\" }\nprint(values.createdAt)\n", DiagnosticCode.ErrNoSuchKey)]
    [InlineData("local person: Pick<User, \"nmae\"> = {}\n", DiagnosticCode.ErrNoSuchKey)]
    [InlineData("local changes: Partial<number> = {}\n", DiagnosticCode.ErrUtilityNonShape)]
    [InlineData("local changes: Partial<User, Record> = {}\n", DiagnosticCode.ErrTypeParamArityMismatch)]
    public void AUtilityTypeNeedsTheFieldsItNames(string source, DiagnosticCode code)
    {
        Assert.Equal(code, Assert.Single(Build(source).Errors).Code);
    }

    [Fact]
    public void AUtilityTypeOfATypeParameterIsFilledInByTheCall()
    {
        const string source = "local function patch<T>(target: T, changes: Partial<T>): T\n    return target\nend\n\nlocal function rename<T extends User>(target: T, changes: Partial<T>): string\n    return changes.name ?? target.name\nend\n\n";

        Assert.Empty(Build(source + "print(patch(user, { age = 5 }), rename(user, { name = \"n\" }))\n").Errors);
        Assert.Equal(DiagnosticCode.ErrTypeMismatch, Assert.Single(Build(source + "print(patch(user, { age = \"x\" }))\n").Errors).Code);
    }

    [Fact]
    public void ADeclarationOfTheSameNameWins()
    {
        Assert.Empty(Build("class Partial<T>\n    value: T\n\n    constructor(value: T)\n        self.value = value\n    end\nend\n\nlocal boxed: Partial<number> = new Partial(1)\nprint(boxed.value)\n").Errors);
    }

    [Fact]
    public void GeneratedDeclarationsKeepUtilityTypesAsWritten()
    {
        var root = Directory.CreateTempSubdirectory("nebra-declgen").FullName;
        var compiler = new NebraCompiler { Config = new Config { ProjectRoot = root, Source = "src" } };
        compiler.AddSource(Path.Combine(root, "src", "init.neb"), "export class User\n    name: string = \"\"\n    age: number = 0\nend\n\nexport function update(user: User, changes: Partial<User>): nil\nend\n\nexport function name(user: Pick<User, \"name\">): string\n    return user.name\nend\n");

        Assert.True(compiler.Compile(), string.Join("\n", compiler.Diagnostics.Diagnostics));

        var declarations = (string)compiler.Cache["GeneratedDeclarations"];
        Assert.Contains("function update(user: User, changes: Partial<User>): nil", declarations);
        Assert.Contains("function name(user: Pick<User, \"name\">): string", declarations);
    }
}
