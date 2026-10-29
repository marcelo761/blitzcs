using Blitz3DCS.Runtime;
using Blitz3DCS.Windowing;

namespace Blitz3DCS.Engine.Windows;

public static partial class LegacyGraphics
{
    public static void Delay(int milliseconds)
    {
        CheckThreadIfStarted();
        _ = ApplicationWindowBootstrap.EnsureApplicationWindow(InitialWindowTitle);
        DelayCore(milliseconds, PumpDelayEvents, LegacySystem.MilliSecs, Thread.Sleep);
    }

    private static void PumpDelayEvents()
    {
        var window = LegacyInput.CurrentWindow;
        window.PumpEvents();
        if (window.CloseRequested) throw new OperationCanceledException("The application window was closed.");
    }

    internal static void DelayCore(int milliseconds, Action pump, Func<int> ticks, Action<int> sleep)
    {
        var deadline = unchecked(ticks() + milliseconds);
        while (true)
        {
            pump();
            var remaining = unchecked(deadline - ticks());
            if (remaining <= 0) return;
            sleep(Math.Min(remaining, 100));
        }
    }
}
