using Nebra.Compiler.Annotations;
using Nebra.Compiler.Passes;
using Nebra.Configuration;
using Nebra.IR;
using Nebra.PackageManager;

namespace Nebra.Compiler;

/// <summary>
/// A declaration the reflection registry can describe: the id it is registered under, what kind
/// of declaration it is, and whether the configured mode records it.
/// </summary>
public sealed record ReflectionEntry(string Id, string Kind, string Name, Decl Declaration, string? File, bool Recorded);

/// <summary>
/// The declarations a build describes to runtime reflection, with the ids <c>reflect.get</c>
/// finds them under. Collected the way the code generator registers them: top-level classes,
/// interfaces, enums, global functions and variables of the project's own files.
/// </summary>
public sealed class ReflectionRegistry(IReadOnlyList<ReflectionEntry> entries)
{
    public const string CacheKey = "Reflection";

    public IReadOnlyList<ReflectionEntry> Entries { get; } = entries;

    public ReflectionEntry? Find(string id)
    {
        return Entries.FirstOrDefault(entry => entry.Id == id);
    }

    public static ReflectionRegistry Collect(PassContext context)
    {
        var entries = new List<ReflectionEntry>();
        var config = context.Config;

        var files = context.Pkgs
            .Where(package => !package.IsLibrary)
            .SelectMany(package => package.Files)
            .Where(file => !file.IsDeclarationFile)
            .Distinct();

        foreach (var file in files)
        {
            var prefix = Namespace(context, file.Filename);
            foreach (var statement in file.Hir.Body)
            {
                var declaration = statement is ExportStmt export ? export.Declaration : statement as Decl;
                entries.AddRange(EntriesOf(declaration, prefix, file.Filename, config));
            }
        }

        return new ReflectionRegistry(entries);
    }

    /// <summary>
    /// The namespace the declarations of <paramref name="file"/> are registered under: its module
    /// path, the path from the source directory of the package it belongs to (<c>Client/Index</c>).
    /// A dependency's file is named from the dependency's own source directory, which is the name
    /// its own build registers it under and the module name its generated declarations carry.
    /// Being derived from the file alone, it is the same in every build, whatever order the files
    /// are read in.
    /// </summary>
    /// <param name="isLibrary">Whether the file is one of the shared declaration libraries, which use the project name.</param>
    public static string Namespace(PassContext context, string? file, bool isLibrary = false)
    {
        if (isLibrary || string.IsNullOrEmpty(file) || !Path.IsPathRooted(file)) return context.Config.Name ?? Fallback;

        file = Path.GetFullPath(file);
        var config = context.Config;

        var own = ModulePath(file, Path.Combine(config.ProjectRoot, config.Source));
        if (own != null) return own;

        foreach (var dependency in InstalledPackagesOf(context))
        {
            var source = Path.Combine(dependency.RootPath, dependency.Manifest?.Source ?? "");
            var path = ModulePath(file, source) ?? ModulePath(file, dependency.RootPath);
            if (path != null) return path;
        }

        var name = Path.GetFileNameWithoutExtension(file);
        return string.IsNullOrEmpty(name) ? Fallback : name;
    }

    private const string Fallback = "main";

    /// <summary>The path of <paramref name="file"/> from <paramref name="directory"/>, without its extension; null when it lies outside.</summary>
    private static string? ModulePath(string file, string directory)
    {
        var root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!file.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return null;

        return Path.ChangeExtension(Path.GetRelativePath(root, file), null).Replace('\\', '/');
    }

    private static IReadOnlyList<InstalledPackage> InstalledPackagesOf(PassContext context)
    {
        if (context.Cache.TryGetValue(InstalledPackages.CacheKey, out var cached) && cached is IReadOnlyList<InstalledPackage> installed)
        {
            return installed;
        }

        var discovered = InstalledPackages.Discover(context.Config.ProjectRoot);
        context.Cache[InstalledPackages.CacheKey] = discovered;
        return discovered;
    }

    /// <summary>Whether the configured mode records a declaration carrying <paramref name="annotations"/>.</summary>
    public static bool IsRecorded(Config config, IEnumerable<Annotation> annotations)
    {
        return config.Reflection.Mode switch
        {
            ReflectionMode.All => true,
            ReflectionMode.Annotated => annotations.Any(annotation => annotation.Name.Name == BuiltinAnnotations.Reflectable),
            _ => false
        };
    }

    private static IEnumerable<ReflectionEntry> EntriesOf(Decl? declaration, string prefix, string? file, Config config)
    {
        switch (declaration)
        {
            case ClassDecl { IsDeclare: false } classDecl:
                yield return Entry(prefix, "class", classDecl.Name.Name, classDecl, file, classDecl.Annotations, config);
                break;
            case InterfaceDecl { IsDeclare: false } interfaceDecl:
                yield return Entry(prefix, "interface", interfaceDecl.Name.Name, interfaceDecl, file, interfaceDecl.Annotations, config);
                break;
            case EnumDecl enumDecl:
                yield return Entry(prefix, "enum", enumDecl.Name.Name, enumDecl, file, enumDecl.Annotations, config);
                break;
            case FunctionDecl { NamePath.Count: 1, MethodName: null } function:
                yield return Entry(prefix, "function", function.NamePath[0].Name, function, file, function.Annotations, config);
                break;
            case LocalDecl local:
                foreach (var variable in local.Variables)
                {
                    yield return Entry(prefix, "variable", variable.Name.Name, local, file, local.Annotations, config);
                }
                break;
        }
    }

    private static ReflectionEntry Entry(string prefix, string kind, string name, Decl declaration, string? file, List<Annotation> annotations, Config config)
    {
        return new ReflectionEntry($"{prefix}::{name}", kind, name, declaration, file, IsRecorded(config, annotations));
    }
}
