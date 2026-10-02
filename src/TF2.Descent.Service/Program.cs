namespace TF2.Descent.Service;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        // `bake`: what the bake Job and `make bake` run (plan §9.2); it bakes a pack and exits.
        if (args.Length > 0 && args[0] == "bake")
            return await new TF2.Descent.MapForge.BakeCommand(new TF2.Descent.MapForge.LinkedMapCompiler()).RunAsync(args[1..], Console.Out);
        var app = ServiceApp.Build(LocalProfile.Expand(args));
        await app.RunAsync();
        return 0;
    }
}
