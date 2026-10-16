namespace Nebra.LPS;

/// <summary>
/// Walks the directories of a project without leaving it. A link to a directory found on the
/// way is not followed: it may point anywhere, up to the root of the filesystem as the
/// <c>dosdevices/z:</c> link of a Wine prefix does. Directories that never hold sources are
/// skipped: hidden ones, <c>nebra_modules</c>, <c>node_modules</c>, <c>bin</c> and <c>obj</c>.
/// The directory a walk starts at may itself be a link, so an installed package is walked
/// through the link <c>nebra_modules</c> holds for it.
/// </summary>
public static class ProjectFiles
{
    public const string ModulesDirectory = "nebra_modules";

    private static readonly string[] Skipped = [ModulesDirectory, "node_modules", "bin", "obj"];

    /// <summary><paramref name="root"/> and every directory beneath it the walk enters.</summary>
    public static IEnumerable<string> Directories(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);

        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            yield return directory;

            foreach (var child in Children(directory))
            {
                pending.Push(child);
            }
        }
    }

    /// <summary>The files matching <paramref name="pattern"/> in the directories <see cref="Directories"/> enters.</summary>
    public static IEnumerable<string> Files(string root, string pattern)
    {
        return Directories(root).SelectMany(directory => FilesIn(directory, pattern));
    }

    /// <summary>The files matching <paramref name="pattern"/> in every package installed in <paramref name="modulesDirectory"/>.</summary>
    public static IEnumerable<string> PackageFiles(string modulesDirectory, string pattern)
    {
        string[] packages;
        try
        {
            packages = Directory.GetDirectories(modulesDirectory);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return [];
        }

        return packages.SelectMany(package => Files(package, pattern));
    }

    private static IEnumerable<string> Children(string directory)
    {
        string[] children;
        try
        {
            children = Directory.GetDirectories(directory);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return [];
        }

        return children.Where(child => !IsSkipped(child));
    }

    private static bool IsSkipped(string directory)
    {
        var name = Path.GetFileName(directory);
        if (name.StartsWith('.') || Skipped.Contains(name, StringComparer.OrdinalIgnoreCase)) return true;

        try
        {
            return new DirectoryInfo(directory).LinkTarget != null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    private static IEnumerable<string> FilesIn(string directory, string pattern)
    {
        try
        {
            return Directory.GetFiles(directory, pattern);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}
