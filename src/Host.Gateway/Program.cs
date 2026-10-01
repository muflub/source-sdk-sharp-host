namespace SourceSharp.Host.Gateway;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        await GatewayApp.Build(args).RunAsync();
        return 0;
    }
}
