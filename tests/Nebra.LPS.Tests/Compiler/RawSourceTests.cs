using Nebra.Compiler;
using Nebra.Configuration;

namespace Nebra.LPS.Tests.Compiler;

public sealed class RawSourceTests
{
    /// <summary>The REPL compiles each input as a source without a path.</summary>
    [Fact]
    public void ASourceWithoutAPathCompiles()
    {
        var compiler = new NebraCompiler { Config = new Config { ProjectRoot = Path.GetTempPath(), ReplMode = true } };
        compiler.AddRawSource("print(string.upper(\"a\"))\n");

        Assert.True(compiler.Compile(), string.Join("\n", compiler.Diagnostics.Diagnostics));
    }
}
