namespace Blitz3DCS.Audio;

/// <summary>
/// Internal infrastructure for future native-object bindings; no current FSOUND function exposes
/// these tokens. Callers must synchronize use of a returned pointer against Remove/Clear/Dispose.
/// </summary>
internal sealed class ManagedHandleTable : IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<int, NativeResource> _handles = [];
    private long _next = 1;
    private bool _disposed;

    internal ManagedHandleTable() { }
    internal ManagedHandleTable(long firstToken) => _next = firstToken;

    public int Add(nint pointer, Action<nint> release)
    {
        ArgumentNullException.ThrowIfNull(release);
        if (pointer == 0) throw new ArgumentOutOfRangeException(nameof(pointer));
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_next > int.MaxValue) throw new InvalidOperationException("Managed handle token space is exhausted.");
            var token = (int)_next++;
            _handles.Add(token, new NativeResource(pointer, release));
            return token;
        }
    }

    public bool TryGet(int token, out nint pointer)
    {
        lock (_gate)
        {
            if (_disposed) { pointer = 0; return false; }
            if (_handles.TryGetValue(token, out var resource))
            {
                pointer = resource.Pointer;
                return true;
            }
            pointer = 0;
            return false;
        }
    }

    public bool Remove(int token)
    {
        NativeResource? resource;
        lock (_gate)
        {
            if (_disposed) return false;
            if (!_handles.Remove(token, out resource)) return false;
        }
        resource.Dispose();
        return true;
    }

    public void Clear()
    {
        NativeResource[] resources;
        lock (_gate)
        {
            if (_disposed) return;
            resources = [.. _handles.Values];
            _handles.Clear();
        }
        DisposeResources(resources);
    }

    public void Dispose()
    {
        NativeResource[] resources;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            resources = _handles.Values.ToArray();
            _handles.Clear();
        }
        DisposeResources(resources);
    }

    private static void DisposeResources(IEnumerable<NativeResource> resources)
    {
        List<Exception>? errors = null;
        foreach (var resource in resources)
        {
            try { resource.Dispose(); }
            catch (Exception error) { (errors ??= []).Add(error); }
        }
        if (errors is not null) throw new AggregateException("One or more native resources failed to release.", errors);
    }

    private sealed class NativeResource(nint pointer, Action<nint> release) : IDisposable
    {
        private int _disposed;
        public nint Pointer { get; } = pointer;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) release(Pointer);
        }
    }
}
