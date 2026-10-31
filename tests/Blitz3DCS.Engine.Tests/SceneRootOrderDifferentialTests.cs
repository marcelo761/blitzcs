using Xunit;

namespace Blitz3DCS.Engine.Tests;

/// <summary>
/// Root enumeration order must equal the original dictionary iteration order (slot order with the LIFO free list)
/// after any sequence of create/free/reparent. The oracle is a plain Dictionary driven by the same operations.
/// </summary>
public sealed class SceneRootOrderDifferentialTests
{
    [Theory]
    [InlineData(11)] [InlineData(12)] [InlineData(13)] [InlineData(14)]
    public void Root_order_matches_dictionary_iteration_order_across_random_churn(int seed)
    {
        var rng = new Random(seed);
        var scene = new SceneGraph();
        var oracle = new Dictionary<int, (int Parent, List<int> Children)>();
        var ids = new List<int>();

        void CheckOrder(int step)
        {
            var expected = oracle.Where(pair => pair.Value.Parent == 0).Select(pair => pair.Key).ToArray();
            Assert.True(expected.SequenceEqual(scene.GetRootEntityIds()), $"root order diverged at step {step}");
            var visible = new List<int>();
            scene.CopyVisibleEntityIds(visible);
            Assert.True(expected.Length <= visible.Count);
        }

        void Free(int id)
        {
            var stack = new Stack<int>();
            stack.Push(id);
            var subtree = new List<int>();
            while (stack.TryPop(out var current)) { subtree.Add(current); foreach (var child in oracle[current].Children) stack.Push(child); }
            var parent = oracle[id].Parent;
            if (parent != 0) oracle[parent].Children.Remove(id);
            scene.FreeEntity(id);
            // The scene removes in discovery order; mirror it so the Dictionary free list evolves identically.
            foreach (var node in subtree) { oracle.Remove(node); ids.Remove(node); }
        }

        for (var step = 0; step < 4000; step++)
        {
            var action = rng.Next(10);
            if (action < 4 || ids.Count == 0)
            {
                var parent = ids.Count > 0 && rng.Next(3) == 0 ? ids[rng.Next(ids.Count)] : 0;
                var id = scene.CreatePivot(parent);
                oracle.Add(id, (parent, []));
                if (parent != 0) oracle[parent].Children.Add(id);
                ids.Add(id);
            }
            else if (action < 8) Free(ids[rng.Next(ids.Count)]);
            else
            {
                var id = ids[rng.Next(ids.Count)];
                var parent = rng.Next(3) == 0 ? 0 : ids[rng.Next(ids.Count)];
                if (parent == id) continue;
                var cycle = false;
                for (var a = parent; a != 0; a = oracle[a].Parent) if (a == id) cycle = true;
                if (cycle || oracle[id].Parent == parent) continue;
                scene.EntityParent(id, parent, false);
                var old = oracle[id];
                if (old.Parent != 0) oracle[old.Parent].Children.Remove(id);
                oracle[id] = (parent, old.Children);
                if (parent != 0) oracle[parent].Children.Add(id);
            }
            if (rng.Next(4) == 0) CheckOrder(step);
        }
        CheckOrder(-1);
    }
}
