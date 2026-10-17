using System.Diagnostics;
using Nebra.PackageManager;

namespace Nebra;

/// <summary>
/// Hands every call of the installed <c>nebra</c> to another build of it, such as a local
/// checkout, between <c>nebra use &lt;path&gt;</c> and <c>nebra unuse</c>. The target receives
/// the arguments, the console and the working directory as they are, so tools that start
/// <c>nebra</c>, the editor's language server among them, use it as well.
/// </summary>
internal static class Trampoline
{
    private const string ForwardedVariable = "NEBRA_FORWARDED";

    /// <summary>
    /// Commands that always run in the installed <c>nebra</c>: switching the target, and keeping
    /// the installation itself up to date, which must never replace the build in use.
    /// </summary>
    private static readonly HashSet<string> LocalCommands = ["use", "unuse", "upgrade", "check", "_upgrade_apply"];

    private static string OverrideFile(string? home) => Path.Combine(home ?? NebraHome.Root, "override");

    /// <summary>
    /// Runs the call in the build <c>nebra use</c> selected, if there is one, and returns its exit
    /// code; <c>null</c> when the call is to be handled here.
    /// </summary>
    public static int? TryForward(string[] args, string? home = null)
    {
        if (args.Length > 0 && LocalCommands.Contains(args[0].ToLowerInvariant())) return null;
        if (Environment.GetEnvironmentVariable(ForwardedVariable) == "1") return null;

        var target = ReadTarget(home);
        if (target == null) return null;

        if (!File.Exists(target))
        {
            Console.Error.WriteLine($"nebra: the build selected with 'nebra use' is missing: {target}");
            Console.Error.WriteLine("nebra: run 'nebra unuse' to go back to the installed version.");
            return 1;
        }

        if (IsCurrentProcess(target)) return null;

        var start = StartInfo(target, args);
        start.Environment[ForwardedVariable] = "1";

        using var process = Process.Start(start);
        if (process == null)
        {
            Console.Error.WriteLine($"nebra: could not start {target}");
            return 1;
        }

        Console.CancelKeyPress += (_, e) => e.Cancel = true;
        process.WaitForExit();
        return process.ExitCode;
    }

    /// <summary><c>nebra use [path]</c>: selects the build to forward to, or shows the current one.</summary>
    public static int RunUse(string[] args, string? home = null)
    {
        if (args.Length == 0)
        {
            var current = ReadTarget(home);
            Console.WriteLine(current == null
                ? "nebra: no build selected, the installed version is used."
                : $"nebra: every call goes to {current} until 'nebra unuse'.");
            return 0;
        }

        var target = ResolveTarget(Path.GetFullPath(args[0]));
        if (target == null)
        {
            Console.Error.WriteLine($"nebra: no nebra executable or nebra.dll found at {args[0]}");
            return 1;
        }

        Directory.CreateDirectory(home ?? NebraHome.Root);
        File.WriteAllText(OverrideFile(home), target);
        Console.WriteLine($"nebra: every call now goes to {target} until 'nebra unuse'.");
        return 0;
    }

    /// <summary><c>nebra unuse</c>: goes back to the installed version.</summary>
    public static int RunUnuse(string? home = null)
    {
        if (!File.Exists(OverrideFile(home)))
        {
            Console.WriteLine("nebra: no build selected, the installed version is already used.");
            return 0;
        }

        File.Delete(OverrideFile(home));
        Console.WriteLine("nebra: the installed version is used again.");
        return 0;
    }

    /// <summary>The build <c>nebra use</c> selected, if any.</summary>
    public static string? ReadTarget(string? home = null)
    {
        try
        {
            var file = OverrideFile(home);
            var text = File.Exists(file) ? File.ReadAllText(file).Trim() : "";
            return text.Length > 0 ? text : null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    /// <summary>
    /// The file a <c>nebra use</c> argument names: the executable or <c>nebra.dll</c> itself, or
    /// the directory a build put them in.
    /// </summary>
    private static string? ResolveTarget(string path)
    {
        if (File.Exists(path)) return path;
        if (!Directory.Exists(path)) return null;

        var executable = OperatingSystem.IsWindows() ? "nebra.exe" : "nebra";
        return new[] { executable, "nebra.dll" }
            .Select(name => Path.Combine(path, name))
            .FirstOrDefault(File.Exists);
    }

    private static ProcessStartInfo StartInfo(string target, string[] args)
    {
        var isAssembly = target.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);
        var start = new ProcessStartInfo(isAssembly ? "dotnet" : target) { UseShellExecute = false };
        if (isAssembly) start.ArgumentList.Add(target);
        foreach (var arg in args) start.ArgumentList.Add(arg);
        return start;
    }

    private static bool IsCurrentProcess(string target)
    {
        var own = Environment.ProcessPath;
        if (own == null) return false;

        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (string.Equals(Path.GetFullPath(own), target, comparison)) return true;

        var ownAssembly = typeof(Trampoline).Assembly.Location;
        return ownAssembly.Length > 0 && string.Equals(Path.GetFullPath(ownAssembly), target, comparison);
    }
}
