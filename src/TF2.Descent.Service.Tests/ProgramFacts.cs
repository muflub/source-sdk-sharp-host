namespace TF2.Descent.Service.Tests;

/// <summary>The exe's subcommands: `bake` runs the bake and exits, never the service.</summary>
public class ProgramFacts
{
    [Fact]
    public async Task Bake_without_its_arguments_prints_the_usage_and_starts_no_service()
    {
        var output = new StringWriter();
        var console = Console.Out;
        Console.SetOut(output);
        int code;
        try { code = await Program.Main(["bake"]); }
        finally { Console.SetOut(console); }
        Assert.Equal(TF2.Descent.MapForge.BakeCommand.Usage, code);
        Assert.Contains("usage: bake", output.ToString());
    }
}
