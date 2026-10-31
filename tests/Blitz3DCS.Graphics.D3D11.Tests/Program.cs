namespace Blitz3DCS.Graphics.D3D11.Tests;

internal static class Program
{
    public static int Main(string[] args)
    {
        if (args.Length != 1 || args[0] is not ("--smoke" or "--antialias-smoke"))
        {
            Console.Error.WriteLine("Usage: Blitz3DCS.Graphics.D3D11.Tests --smoke | --antialias-smoke");
            return 2;
        }
        try
        {
            if (args[0] == "--smoke")
            {
                RendererWindowsSmokeTests.RunSmoke();
                Console.WriteLine("D3D11 SDL clear/draw/resize/present/readback smoke passed.");
            }
            RendererWindowsSmokeTests.RunAntialiasSmoke();
            RendererWindowsSmokeTests.RunMipmapsSmoke();
            RendererWindowsSmokeTests.RunTextureBlendSmoke();
            const string success = "D3D11 multisampling, mipmap and texture-blend smoke passed.";
            Console.WriteLine(success);
            if (Environment.GetEnvironmentVariable("BLITZCS_D3D11_SMOKE_LOG") is { Length: > 0 } successLogPath)
                File.WriteAllText(successLogPath, success);
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
