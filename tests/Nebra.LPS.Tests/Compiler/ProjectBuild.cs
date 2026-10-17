using Nebra.Compiler;
using Nebra.Configuration;
using Nebra.Diagnostics;

namespace Nebra.LPS.Tests.Compiler;

/// <summary>
/// Compiles a project written to a temporary directory, for behavior that reads files from disk
/// such as imports, annotation files and declaration files.
/// </summary>
internal sealed class ProjectBuild
{
    private ProjectBuild(NebraCompiler compiler, string root)
    {
        Compiler = compiler;
        Root = root;
    }

    public NebraCompiler Compiler { get; }

    public string Root { get; }

    public List<Diagnostic> Errors => Compiler.Diagnostics.Diagnostics.Where(diagnostic => diagnostic.Level == DiagnosticLevel.Error).ToList();

    public static ProjectBuild Run(Action<Config>? configure, params (string Name, string Source)[] files)
    {
        var root = Directory.CreateTempSubdirectory("nebra-project").FullName;
        var config = new Config { ProjectRoot = root, Source = "src" };
        configure?.Invoke(config);
        var compiler = new NebraCompiler { Config = config };

        foreach (var (name, source) in files)
        {
            var path = Path.Combine(root, name);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, source);
            if (name.StartsWith("src/", StringComparison.Ordinal) && !name.EndsWith(".d.neb", StringComparison.Ordinal))
                compiler.AddSource(path, source);
        }

        compiler.Compile();
        return new ProjectBuild(compiler, root);
    }

    /// <summary>The Lua generated for the source file at <paramref name="name"/>.</summary>
    public string Lua(string name)
    {
        var path = Path.Combine(Root, name);
        return Compiler.Packages.Values.SelectMany(package => package.Files).First(file => file.Filename == path).GeneratedLua ?? "";
    }
}
