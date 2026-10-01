namespace Descent.Service;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var app = ServiceApp.Build(LocalProfile.Expand(args));
        await app.RunAsync();
        return 0;
    }
}
