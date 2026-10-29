namespace Blitz3DCS.Graphics.D3D11.Tests;

internal static class Program
{
    public static int Main(string[] args)
    {
        if (args.Length != 1 || args[0] != "--smoke")
        {
            Console.Error.WriteLine("Usage: Blitz3DCS.Graphics.D3D11.Tests --smoke");
            return 2;
        }
        try
        {
            RendererWindowsSmokeTests.RunSmoke();
            Console.WriteLine("D3D11 SDL clear/draw/resize/present/readback smoke passed.");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            if (Environment.GetEnvironmentVariable("BLITZCS_D3D11_SMOKE_LOG") is { Length: > 0 } logPath)
                File.WriteAllText(logPath, error.ToString());
            return 1;
        }
    }
}
