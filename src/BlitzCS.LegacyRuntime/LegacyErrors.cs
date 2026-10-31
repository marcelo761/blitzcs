using System.Collections.Concurrent;

namespace BlitzCS.LegacyRuntime;

/// <summary>Optional runtime services. This library has no compiler or engine dependency.</summary>
public static class LegacyErrors
{
    private static readonly ConcurrentQueue<string> RecoverableErrors = new();

    /// <summary>Terminates execution by raising a diagnostic-bearing managed failure.</summary>
    public static int RuntimeError(string message) => throw new InvalidOperationException(message);

    /// <summary>Returns and removes the oldest recoverable runtime error, or an empty string when none remain.</summary>
    public static string ErrorLog() => RecoverableErrors.TryDequeue(out var error) ? error : string.Empty;

    internal static void RecordRecoverableError(string message)
    {
        ArgumentException.ThrowIfNullOrEmpty(message);
        RecoverableErrors.Enqueue(message);
    }
}
