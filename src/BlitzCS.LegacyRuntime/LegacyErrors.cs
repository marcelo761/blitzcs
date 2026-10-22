namespace BlitzCS.LegacyRuntime;

/// <summary>Optional runtime services. This library has no compiler or engine dependency.</summary>
public static class LegacyErrors
{
    /// <summary>Terminates execution by raising a diagnostic-bearing managed failure.</summary>
    public static int RuntimeError(string message) => throw new InvalidOperationException(message);
}
