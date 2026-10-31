namespace Blitz3DCS.Engine.Windows.Tests;

internal static class Program
{
    public static int Main(string[] args)
    {
        if (args.Length != 1 || args[0] is not ("--smoke" or "--camera-smoke" or "--texture-smoke" or "--image-smoke" or "--system-smoke" or "--font-smoke" or "--model-smoke" or "--drawing-smoke" or "--sprite-smoke" or "--render-queue-smoke" or "--plane-smoke" or "--movie-smoke" or "--instancing-smoke" or "--aatext-smoke"))
        {
            Console.Error.WriteLine("Usage: Blitz3DCS.Engine.Windows.Tests --smoke|--camera-smoke|--texture-smoke|--image-smoke|--system-smoke|--font-smoke|--model-smoke|--drawing-smoke|--sprite-smoke|--render-queue-smoke|--plane-smoke|--movie-smoke|--instancing-smoke|--aatext-smoke");
            return 2;
        }

        try
        {
            if (args[0] is "--smoke" or "--camera-smoke")
                LegacyGraphicsWindowsSmokeTests.RunSmoke();
            if (args[0] is "--smoke" or "--texture-smoke")
                LegacyGraphicsTexturesWindowsSmokeTests.RunSmoke();
            if (args[0] == "--image-smoke")
                LegacyGraphicsImagesTests.RunSmoke();
            if (args[0] == "--system-smoke")
            {
                LegacyGraphicsSystemWindowsSmokeTests.RunSmoke();
                LegacyKernel32BindingsTests.RunSmoke();
            }
            if (args[0] == "--font-smoke")
                LegacyGraphicsFontSmokeTests.RunSmoke();
            if (args[0] == "--model-smoke")
                LegacyGraphicsAnimatedModelWindowsSmokeTests.RunSmoke();
            if (args[0] == "--drawing-smoke")
                LegacyGraphicsDrawingTests.RunSmoke();
            if (args[0] == "--sprite-smoke")
                LegacyGraphicsSpriteTests.RunSmoke();
            if (args[0] == "--render-queue-smoke")
                LegacyGraphicsRenderQueueTests.RunSmoke();
            if (args[0] == "--plane-smoke")
                LegacyGraphicsPlaneTests.RunSmoke();
            if (args[0] == "--movie-smoke")
                LegacyMovieBindingTests.RunSmoke();
            var report = "";
            if (args[0] == "--instancing-smoke")
            {
                LegacyGraphicsInstancingSmokeTests.RunSmoke();
                report = LegacyGraphicsInstancingSmokeTests.LastReport;
                Console.Write(report);
            }
            if (args[0] == "--aatext-smoke")
            {
                LegacyGraphicsAaTextSmokeTests.RunSmoke();
                report = LegacyGraphicsAaTextSmokeTests.LastReport;
                Console.Write(report);
            }
            Console.WriteLine($"Engine.Windows native {args[0].Trim('-')} smoke passed.");
            WriteSmokeStatus("passed" + report);
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            WriteSmokeStatus(error.ToString());
            return 1;
        }
    }

    private static void WriteSmokeStatus(string status)
    {
        if (Environment.GetEnvironmentVariable("BLITZCS_ENGINE_WINDOWS_SMOKE_LOG") is { Length: > 0 } path)
            File.WriteAllText(path, status);
    }
}
