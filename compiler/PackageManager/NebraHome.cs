namespace Nebra.PackageManager;

/// <summary>
/// Resolves the per-user Nebra home directory and its well-known subpaths (store, cache, tmp).
/// Can be overridden via the <c>NEBRA_HOME</c> environment variable.
/// </summary>
public static class NebraHome
{
    /// <summary>
    /// Root directory for per-user Nebra data. Defaults to <c>~/.nebra</c>.
    /// </summary>
    public static string Root
    {
        get
        {
            var env = Environment.GetEnvironmentVariable("NEBRA_HOME");
            if (!string.IsNullOrWhiteSpace(env))
                return Path.GetFullPath(env);
            var user = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(user, ".nebra");
        }
    }

    /// <summary>Content-addressed package store.</summary>
    public static string StoreRoot => Path.Combine(Root, "store");

    /// <summary>Shared caches (git, registry, resolver).</summary>
    public static string CacheRoot => Path.Combine(Root, "cache");

    /// <summary>Bare git clones, reused across installs.</summary>
    public static string GitCacheRoot => Path.Combine(CacheRoot, "git");

    /// <summary>Scratch directory for staging extractions before moving into the store.</summary>
    public static string TmpRoot => Path.Combine(Root, "tmp");

    /// <summary>Global file-lock directory (per-install serialization).</summary>
    public static string LockRoot => Path.Combine(Root, "lock");

    /// <summary>
    /// Returns the store path for a specific package commit. A package living in a subdirectory of
    /// its repository gets a path of its own, <c>&lt;commit&gt;@&lt;subdir&gt;</c>, so packages from one
    /// repository and commit do not overwrite each other.
    /// </summary>
    public static string PackagePath(string host, string owner, string repo, string commit, string? subdir = null)
    {
        var folder = NormalizeSubdir(subdir);
        var snapshot = folder == null ? commit : $"{commit}@{SafeSeg(folder)}";
        return Path.Combine(StoreRoot, SafeSeg(host), SafeSeg(owner), SafeSeg(repo), snapshot);
    }

    /// <summary>A repository subdirectory without leading or trailing slashes, or <c>null</c> for the root.</summary>
    public static string? NormalizeSubdir(string? subdir)
    {
        var trimmed = subdir?.Replace('\\', '/').Trim('/');
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }

    /// <summary>
    /// Returns the bare-clone path for a repo in the git cache.
    /// </summary>
    public static string BareClonePath(string host, string owner, string repo)
    {
        return Path.Combine(GitCacheRoot, SafeSeg(host), SafeSeg(owner), SafeSeg(repo) + ".git");
    }

    private static string SafeSeg(string s)
    {
        var chars = s.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_').ToArray();
        return new string(chars);
    }
}
