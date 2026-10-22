namespace BlitzCS.Compiler;

internal static class VectorRuntime
{
    internal const string Source = """
internal sealed class __BlitzVector<T>
{
    private readonly T[] data;
    internal __BlitzVector(int length)
    {
        try { data = new T[length]; }
        catch (System.OutOfMemoryException) { throw new System.InvalidOperationException("Insufficient memory for fixed vector."); }
    }
    internal ref T Element(int index, string location)
    {
        if ((uint)index >= (uint)data.Length)
            throw new System.InvalidOperationException(location + ": Fixed vector subscript out of range.");
        return ref data[index];
    }
}
""";
}
