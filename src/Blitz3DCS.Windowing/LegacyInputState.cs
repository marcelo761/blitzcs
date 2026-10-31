namespace Blitz3DCS.Windowing;

/// <summary>Buffered legacy keyboard and mouse state, independent of the native event pump.</summary>
/// <remarks>
/// Scan codes are 1–255 and mouse buttons are 1 (left), 2 (right), and 3 (middle).
/// Invalid indexes return zero or are ignored rather than accessing unchecked native memory.
/// The owner serializes access; polling and event processing allocate no managed objects.
/// </remarks>
public sealed class LegacyInputState
{
    private const int QueueCapacity = 32;
    private readonly bool[] _keysDown = new bool[256];
    private readonly int[] _keyHits = new int[256];
    private readonly bool[] _mouseDown = new bool[4];
    private readonly int[] _mouseHits = new int[4];
    private readonly int[] _keyQueue = new int[QueueCapacity];
    private readonly int[] _mouseQueue = new int[QueueCapacity];
    private int _keyRead, _keyCount, _mouseRead, _mouseCount;
    private int _mouseWheel;
    private int _mouseXBaseline, _mouseYBaseline, _mouseZBaseline;

    public int MouseX { get; private set; }
    public int MouseY { get; private set; }
    public int MouseZ => _mouseWheel / 120;

    public int KeyDown(int dik) => ValidKey(dik) && _keysDown[dik] ? 1 : 0;

    public int KeyHit(int dik) => ValidKey(dik) ? ReadHits(_keyHits, dik) : 0;

    public int GetScanCode() => Dequeue(_keyQueue, ref _keyRead, ref _keyCount);

    /// <summary>Clears buffered presses and hit counts, preserving held keys.</summary>
    public void FlushKeys()
    {
        Array.Clear(_keyHits);
        _keyRead = _keyCount = 0;
    }

    public int MouseDown(int button) => ValidButton(button) && _mouseDown[button] ? 1 : 0;

    public int MouseHit(int button) => ValidButton(button) ? ReadHits(_mouseHits, button) : 0;

    public int GetMouse() => Dequeue(_mouseQueue, ref _mouseRead, ref _mouseCount);

    /// <summary>Clears buffered presses and hit counts, preserving held buttons and axes.</summary>
    public void FlushMouse()
    {
        Array.Clear(_mouseHits);
        _mouseRead = _mouseCount = 0;
    }

    /// <summary>Applies a native key event; operating system auto-repeat events are ignored.</summary>
    public void SetKey(int dik, bool down, bool repeat = false)
    {
        if (!ValidKey(dik) || repeat) return;
        _keysDown[dik] = down;
        if (!down) return;
        IncrementHits(_keyHits, dik);
        Enqueue(_keyQueue, _keyRead, ref _keyCount, dik);
    }

    public void SetMouseButton(int button, bool down)
    {
        if (!ValidButton(button)) return;
        _mouseDown[button] = down;
        if (!down) return;
        IncrementHits(_mouseHits, button);
        Enqueue(_mouseQueue, _mouseRead, ref _mouseCount, button);
    }

    /// <summary>Moves the pointer and establishes new X/Y speed baselines.</summary>
    public void MoveMouse(int x, int y)
    {
        MouseX = _mouseXBaseline = x;
        MouseY = _mouseYBaseline = y;
    }

    /// <summary>Applies native pointer motion without consuming accumulated speed.</summary>
    public void SetMousePosition(int x, int y)
    {
        MouseX = x;
        MouseY = y;
    }

    /// <summary>Accumulates wheel motion in native units, with 120 units per whole notch.</summary>
    public void AddMouseWheel(int delta) => _mouseWheel = unchecked(_mouseWheel + delta);

    public int MouseXSpeed() => ReadSpeed(MouseX, ref _mouseXBaseline);
    public int MouseYSpeed() => ReadSpeed(MouseY, ref _mouseYBaseline);
    public int MouseZSpeed() => ReadSpeed(MouseZ, ref _mouseZBaseline);

    /// <summary>Resets device state and axes while retaining the speed query baselines.</summary>
    public void ResetDevices()
    {
        Array.Clear(_keysDown);
        Array.Clear(_mouseDown);
        FlushKeys();
        FlushMouse();
        MouseX = MouseY = _mouseWheel = 0;
    }

    private static bool ValidKey(int dik) => dik > 0 && dik < 256;
    private static bool ValidButton(int button) => button > 0 && button < 4;

    private static int ReadHits(int[] hits, int index)
    {
        var value = hits[index];
        hits[index] = 0;
        return value;
    }

    private static void IncrementHits(int[] hits, int index)
    {
        // Keep unread counters nonnegative even beyond the native signed-overflow domain.
        if (hits[index] < int.MaxValue) hits[index]++;
    }

    private static void Enqueue(int[] queue, int read, ref int count, int value)
    {
        if (count == QueueCapacity) return;
        queue[(read + count) & (QueueCapacity - 1)] = value;
        count++;
    }

    private static int Dequeue(int[] queue, ref int read, ref int count)
    {
        if (count == 0) return 0;
        var value = queue[read];
        read = (read + 1) & (QueueCapacity - 1);
        count--;
        return value;
    }

    private static int ReadSpeed(int position, ref int baseline)
    {
        var delta = unchecked(position - baseline);
        baseline = position;
        return delta;
    }
}
