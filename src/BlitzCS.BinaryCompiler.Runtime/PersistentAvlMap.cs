using System.Runtime.CompilerServices;

namespace BlitzCS.BinaryCompiler.Runtime;

/// <summary>Persistent ordered map. Updates copy only the AVL search path; readers retain an immutable root snapshot.</summary>
internal static class PersistentAvlMap<TKey, TValue> where TKey : notnull
{
    internal sealed class Node(TKey key, TValue value, Node? left, Node? right)
    {
        internal TKey Key { get; } = key;
        internal TValue Value { get; } = value;
        internal Node? Left { get; } = left;
        internal Node? Right { get; } = right;
        internal int Height { get; } = Math.Max(left?.Height ?? 0, right?.Height ?? 0) + 1;
    }

    internal readonly struct InOrderEnumerable(Node? root)
    {
        public Enumerator GetEnumerator() => new(root);
    }

    internal struct Enumerator
    {
        // Even an AVL tree containing more entries than a 64-bit counter can name has
        // height below 128. The inline stack avoids allocating during ordered reads.
        [InlineArray(128)]
        private struct Path
        {
            private Node? first;
        }

        private Path path;
        private int depth;
        private Node? cursor;
        private TValue current;

        internal Enumerator(Node? root)
        {
            path = default;
            depth = 0;
            cursor = root;
            current = default!;
            PushLeft();
        }

        public TValue Current => current;

        public bool MoveNext()
        {
            if (depth == 0) return false;
            var node = path[--depth]!;
            current = node.Value;
            cursor = node.Right;
            PushLeft();
            return true;
        }

        private void PushLeft()
        {
            while (cursor is not null)
            {
                if (depth == 128) throw new InvalidOperationException("Persistent AVL height exceeded its traversal stack.");
                path[depth++] = cursor;
                cursor = cursor.Left;
            }
        }
    }

    internal static InOrderEnumerable Enumerate(Node? root) => new(root);

    internal static Node Set(Node? root, TKey key, TValue value) => Set(root, key, value, Comparer<TKey>.Default);

    private static Node Set(Node? root, TKey key, TValue value, IComparer<TKey> comparer)
    {
        if (root is null) return new Node(key, value, null, null);
        var order = comparer.Compare(key, root.Key);
        if (order == 0) return new Node(key, value, root.Left, root.Right);
        return order < 0
            ? Balance(new Node(root.Key, root.Value, Set(root.Left, key, value, comparer), root.Right))
            : Balance(new Node(root.Key, root.Value, root.Left, Set(root.Right, key, value, comparer)));
    }

    internal static Node? Remove(Node? root, TKey key)
    {
        var removed = false;
        return Remove(root, key, Comparer<TKey>.Default, ref removed);
    }

    private static Node? Remove(Node? root, TKey key, IComparer<TKey> comparer, ref bool removed)
    {
        if (root is null) return null;
        var order = comparer.Compare(key, root.Key);
        if (order < 0)
        {
            var left = Remove(root.Left, key, comparer, ref removed);
            return removed ? Balance(new Node(root.Key, root.Value, left, root.Right)) : root;
        }
        if (order > 0)
        {
            var right = Remove(root.Right, key, comparer, ref removed);
            return removed ? Balance(new Node(root.Key, root.Value, root.Left, right)) : root;
        }

        removed = true;
        if (root.Left is null) return root.Right;
        if (root.Right is null) return root.Left;
        var successor = Minimum(root.Right);
        return Balance(new Node(successor.Key, successor.Value, root.Left, RemoveMinimum(root.Right)));
    }

    private static Node Minimum(Node node)
    {
        while (node.Left is not null) node = node.Left;
        return node;
    }

    private static Node? RemoveMinimum(Node node)
    {
        if (node.Left is null) return node.Right;
        return Balance(new Node(node.Key, node.Value, RemoveMinimum(node.Left), node.Right));
    }

    internal static bool TryGetPredecessor(Node? root, TKey key, out TKey foundKey, out TValue value)
    {
        var comparer = Comparer<TKey>.Default;
        Node? found = null;
        while (root is not null)
        {
            if (comparer.Compare(root.Key, key) <= 0)
            {
                found = root;
                root = root.Right;
            }
            else root = root.Left;
        }
        if (found is null)
        {
            foundKey = default!;
            value = default!;
            return false;
        }
        foundKey = found.Key;
        value = found.Value;
        return true;
    }

    internal static bool TryGetSuccessor(Node? root, TKey key, out TKey foundKey, out TValue value)
    {
        var comparer = Comparer<TKey>.Default;
        Node? found = null;
        while (root is not null)
        {
            if (comparer.Compare(root.Key, key) > 0)
            {
                found = root;
                root = root.Left;
            }
            else root = root.Right;
        }
        if (found is null)
        {
            foundKey = default!;
            value = default!;
            return false;
        }
        foundKey = found.Key;
        value = found.Value;
        return true;
    }

    private static Node Balance(Node node)
    {
        var balance = Height(node.Left) - Height(node.Right);
        if (balance > 1)
        {
            var left = node.Left!;
            if (Height(left.Left) < Height(left.Right))
                node = new Node(node.Key, node.Value, RotateLeft(left), node.Right);
            return RotateRight(node);
        }
        if (balance < -1)
        {
            var right = node.Right!;
            if (Height(right.Right) < Height(right.Left))
                node = new Node(node.Key, node.Value, node.Left, RotateRight(right));
            return RotateLeft(node);
        }
        return node;
    }

    private static int Height(Node? node) => node?.Height ?? 0;

    private static Node RotateLeft(Node root)
    {
        var pivot = root.Right!;
        var left = new Node(root.Key, root.Value, root.Left, pivot.Left);
        return new Node(pivot.Key, pivot.Value, left, pivot.Right);
    }

    private static Node RotateRight(Node root)
    {
        var pivot = root.Left!;
        var right = new Node(root.Key, root.Value, pivot.Right, root.Right);
        return new Node(pivot.Key, pivot.Value, pivot.Left, right);
    }
}
