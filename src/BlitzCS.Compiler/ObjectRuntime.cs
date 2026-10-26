namespace BlitzCS.Compiler;

// Position records preserve observable legacy ordering without owning deleted
// objects. Only a live record's Value is strong; aliases own deleted identities.
internal static class ObjectRuntime
{
    internal const string Source = """
internal abstract class __BlitzObject
{
    internal bool Alive = true;
    internal int HandleId;
    internal __BlitzObjectNode Position;
    internal abstract void ClearFields();
    internal virtual byte[] __PackFields() => System.Array.Empty<byte>();
    internal virtual void __UnpackFields(byte[] bytes) { }
    private static int __nextMarshalId;
    private static readonly object __marshalGate = new();
    internal static int Marshal(__BlitzObject value, string location)
    {
        if (value is null) return 0;
        Require(value, location);
        System.Collections.Concurrent.ConcurrentDictionary<int, (byte[] Bytes, System.Action<byte[]> WriteBack)> table;
        lock (__marshalGate)
        {
            if (System.AppContext.GetData("__BlitzCS.ObjectBuffers") is not System.Collections.Concurrent.ConcurrentDictionary<int, (byte[] Bytes, System.Action<byte[]> WriteBack)> existing)
            {
                existing = new();
                System.AppContext.SetData("__BlitzCS.ObjectBuffers", existing);
            }
            table = existing;
        }
        var id = System.Threading.Interlocked.Decrement(ref __nextMarshalId);
        if (id >= 0) id = -1;
        table[id] = (value.__PackFields(), value.__UnpackFields);
        return id;
    }
    internal static T RequireReference<T>(T value, string location) where T : __BlitzObject
    {
        if (value is null) throw new System.InvalidOperationException(location + ": Object does not exist.");
        return value;
    }
    internal static T Require<T>(T value, string location) where T : __BlitzObject
    {
        if (value is null || !value.Alive) throw new System.InvalidOperationException(location + ": Object does not exist.");
        return value;
    }
    internal static bool Equal(__BlitzObject left, __BlitzObject right) =>
        System.Object.ReferenceEquals(left is { Alive: true } ? left : null, right is { Alive: true } ? right : null);
}
internal static class __BlitzHandles
{
    private static int next;
    private static readonly System.Collections.Generic.Dictionary<int, __BlitzObject> live = new();
    internal static int ToHandle(__BlitzObject value, string location)
    {
        if (value is null || !value.Alive) return 0;
        if (value.HandleId != 0) return value.HandleId;
        if (next == int.MaxValue)
            throw new System.InvalidOperationException(location + ": Object handle identifiers exhausted.");
        var identifier = ++next;
        live.Add(identifier, value);
        value.HandleId = identifier;
        return identifier;
    }
    internal static T FromHandle<T>(int identifier) where T : __BlitzObject =>
        live.TryGetValue(identifier, out var value) && value.Alive && value.GetType() == typeof(T) ? (T)value : null;
    internal static void Release(__BlitzObject value)
    {
        if (value.HandleId == 0) return;
        live.Remove(value.HandleId);
        value.HandleId = 0;
    }
}
internal sealed class __BlitzObjectNode
{
    internal __BlitzObjectNode Previous, Next;
    internal __BlitzObject Value;
    internal System.WeakReference<__BlitzObject> Deleted;
    internal bool Empty => Value is null && !Deleted.TryGetTarget(out _);
}
internal sealed class __BlitzObjectList<T> where T : __BlitzObject, new()
{
    private __BlitzObjectNode head, tail, sweep;
    private void Unlink(__BlitzObjectNode node)
    {
        if (sweep == node) sweep = node.Next;
        if (node.Previous is null) head = node.Next; else node.Previous.Next = node.Next;
        if (node.Next is null) tail = node.Previous; else node.Next.Previous = node.Previous;
        node.Previous = node.Next = null;
    }
    // Bounded housekeeping; a GC alone need not immediately remove position records.
    private void Maintain()
    {
        for (var budget = 0; budget < 2; budget++)
        {
            sweep ??= head;
            if (sweep is null) return;
            var node = sweep;
            sweep = node.Next;
            if (node.Empty) Unlink(node);
        }
    }
    // Linear diagnostic sweep, used by memory probes; not run for each call.
    internal void Scavenge()
    {
        for (var node = head; node is not null;)
        {
            var next = node.Next;
            if (node.Empty) Unlink(node);
            node = next;
        }
    }
    internal T New()
    {
        Maintain();
        var value = new T();
        var node = new __BlitzObjectNode { Value = value, Previous = tail };
        value.Position = node;
        if (tail is null) head = node; else tail.Next = node;
        tail = node;
        return value;
    }
    private T Find(__BlitzObjectNode node, bool forward)
    {
        while (node is not null)
        {
            if (node.Value is T value) return value;
            var next = forward ? node.Next : node.Previous;
            if (node.Empty) Unlink(node);
            node = next;
        }
        return null;
    }
    internal T First() { Maintain(); return Find(head, true); }
    internal T Last() { Maintain(); return Find(tail, false); }
    internal T Following(T value) { Maintain(); return Find(value.Position.Next, true); }
    internal T Preceding(T value) { Maintain(); return Find(value.Position.Previous, false); }
    internal bool EachFirst(ref T value) { value = First(); return value is not null; }
    internal bool EachNext(ref T value) { value = Following(value); return value is not null; }
    private void DeleteValue(T value)
    {
        if (value is null || !value.Alive) return;
        __BlitzHandles.Release(value);
        value.Alive = false;
        value.ClearFields();
        value.Position.Deleted = new System.WeakReference<__BlitzObject>(value);
        value.Position.Value = null;
    }
    internal void Delete(T value) { Maintain(); DeleteValue(value); }
    internal void DeleteEach()
    {
        Maintain();
        for (var node = head; node is not null; node = node.Next)
            if (node.Value is T value) DeleteValue(value);
    }
    internal void Insert(T value, T anchor, bool before, string location)
    {
        Maintain();
        __BlitzObject.Require(value, location); __BlitzObject.Require(anchor, location);
        if (System.Object.ReferenceEquals(value, anchor)) return;
        var node = value.Position;
        var target = anchor.Position;
        Unlink(node);
        var previous = before ? target.Previous : target;
        var next = before ? target : target.Next;
        node.Previous = previous; node.Next = next;
        if (previous is null) head = node; else previous.Next = node;
        if (next is null) tail = node; else next.Previous = node;
    }
}
""";
}
