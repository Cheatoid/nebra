using Nebra.Compiler.Annotations;
using Nebra.Configuration;
using Nebra.IR;

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

    public static ReflectionRegistry Collect(IReadOnlyList<PackageContext> packages, Config config)
    {
        var root = packages.FirstOrDefault(package => !package.IsLibrary);
        var entries = new List<ReflectionEntry>();

        foreach (var package in packages.Where(package => !package.IsLibrary))
        {
            var prefix = Namespace(package, root, config);
            foreach (var file in package.Files.Where(file => !file.IsDeclarationFile))
            {
                foreach (var statement in file.Hir.Body)
                {
                    var declaration = statement is ExportStmt export ? export.Declaration : statement as Decl;
                    entries.AddRange(EntriesOf(declaration, prefix, file.Filename, config));
                }
            }
        }

        return new ReflectionRegistry(entries);
    }

    /// <summary>
    /// The namespace a package's declarations are registered under: the configured name for the
    /// project's root package and for the shared libraries, the file name for every other one.
    /// </summary>
    public static string Namespace(PackageContext package, PackageContext? root, Config config)
    {
        var leaf = Path.GetFileNameWithoutExtension(package.Path.TrimEnd('/', '\\'));
        var fallback = string.IsNullOrEmpty(leaf) ? "main" : leaf;
        var isRoot = package.IsLibrary || ReferenceEquals(package, root);
        return isRoot ? config.Name ?? fallback : fallback;
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
