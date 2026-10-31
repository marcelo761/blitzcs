namespace BlitzCS.Compiler;

// Generated storage stays separate from the public scalar/function C# bridge.
internal static class ArrayRuntime
{
    internal const string Source = """
internal sealed class __BlitzArray<T>
{
    private readonly string name;
    private T[] data;
    private int[] lengths;
    internal __BlitzArray(string name) { this.name = name; }
    internal void Reset() { data = null; lengths = null; }
    private System.InvalidOperationException Error(string location, string reason) =>
        new System.InvalidOperationException($"{location}: Array '{name}': {reason}");
    internal void Dimension(int[] bounds, string location)
    {
        var sizes = new int[bounds.Length];
        long count = 1;
        for (var axis = 0; axis < bounds.Length; axis++)
        {
            if (bounds[axis] < 0) throw Error(location, $"negative upper bound {bounds[axis]} on axis {axis + 1}.");
            var size = (long)bounds[axis] + 1;
            if (size > System.Array.MaxLength || count > System.Array.MaxLength / size)
                throw Error(location, "dimension product exceeds the supported array size.");
            sizes[axis] = (int)size;
            count *= size;
        }
        try { data = new T[(int)count]; }
        catch (System.OutOfMemoryException) { throw Error(location, "insufficient memory for these dimensions."); }
        lengths = sizes;
    }
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private System.InvalidOperationException Fail(string location, int axis, int index) =>
        data is null ? Error(location, "array has not been dimensioned.")
        : Error(location, $"index {index} on axis {axis + 1} is outside 0..{lengths[axis] - 1}.");
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal ref T Element(int index, string location)
    {
        var d = data;
        if (d is null || (uint)index >= (uint)lengths[0]) throw Fail(location, 0, index);
        return ref d[index];
    }
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal ref T Element(int index0, int index1, string location)
    {
        var d = data;
        if (d is null) throw Fail(location, 0, index0);
        var l = lengths;
        if ((uint)index0 >= (uint)l[0]) throw Fail(location, 0, index0);
        if ((uint)index1 >= (uint)l[1]) throw Fail(location, 1, index1);
        return ref d[index0 + index1 * l[0]];
    }
    internal ref T Element(scoped System.ReadOnlySpan<int> indices, string location)
    {
        if (data is null) throw Error(location, "array has not been dimensioned.");
        var offset = 0;
        var stride = 1;
        for (var axis = 0; axis < indices.Length; axis++)
        {
            if (indices[axis] < 0 || indices[axis] >= lengths[axis])
                throw Error(location, $"index {indices[axis]} on axis {axis + 1} is outside 0..{lengths[axis] - 1}.");
            offset += indices[axis] * stride;
            stride *= lengths[axis];
        }
        return ref data[offset];
    }
}
""";
}
