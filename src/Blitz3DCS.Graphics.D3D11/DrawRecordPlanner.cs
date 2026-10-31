namespace Blitz3DCS.Graphics.D3D11;

/// <summary>Byte-level questions the planner asks about the queued draws (implemented over the renderer queue and test fakes).</summary>
internal interface IDrawRecordSource
{
    int Count { get; }
    /// <summary>The transform records of draws <paramref name="a"/> and <paramref name="b"/> are byte-identical.</summary>
    bool SameTransform(int a, int b);
    /// <summary>The material records of draws <paramref name="a"/> and <paramref name="b"/> are byte-identical.</summary>
    bool SameMaterial(int a, int b);
    /// <summary>
    /// <paramref name="candidate"/> may join an instanced draw started by <paramref name="first"/>: same mesh, index
    /// count, texture layers and pipeline state, and a material record identical except for MaterialColor.
    /// </summary>
    bool CanInstance(int first, int candidate);
}

/// <summary>Adds the commutativity query the order-independent grouping needs on top of <see cref="IDrawRecordSource"/>.</summary>
internal interface IReorderSource : IDrawRecordSource
{
    /// <summary>
    /// Whether the draw's output does not depend on its position among other commutative draws: additive blending
    /// (a pure sum into the target) with a read-only depth test.
    /// </summary>
    bool Commutative(int index);
}

/// <summary>One device draw: a single queued draw or an instanced run of consecutive queued draws.</summary>
internal struct PlannedDraw
{
    public int First;
    public int Count;
    public int TransformOffset;
    public int MaterialOffset;
    public int InstanceOffset;
    public int InstanceBytes;
    public bool WriteTransform;
    public bool WriteMaterial;

    public readonly bool Instanced => Count > 1;
}

/// <summary>
/// Pure packing and batching decisions for one flush of queued mesh draws. Draws keep their submission order: a run
/// only merges consecutive draws, and an instanced draw rasterizes its instances in order, so blending and depth
/// results are those of the individual draws. Records are packed at 256-byte aligned offsets relative to the flush
/// region; a record equal to the previously written one is reused instead of written again (the old per-draw
/// SameBytes skip, now also skipping the rebind).
/// </summary>
internal static class DrawRecordPlanner
{
    /// <summary>cbuffer Transform: four float4x4.</summary>
    public const int TransformBytes = 256;
    /// <summary>cbuffer Material (33 float4); the bound window is rounded up to 48 constants.</summary>
    public const int MaterialBytes = 528;
    /// <summary>InstanceData: the transform record followed by MaterialColor.</summary>
    public const int InstanceRecordBytes = TransformBytes + 16;
    /// <summary>Instances one 64 KiB constant window can hold; must match the HLSL Instances array length.</summary>
    public const int MaxInstances = ObjectConstantRing.MaxWindowBytes / InstanceRecordBytes;

    public static int RunLength<T>(ref T source, int start, int maxRun) where T : IDrawRecordSource
    {
        var end = Math.Min(source.Count, start + maxRun);
        var next = start + 1;
        while (next < end && source.CanInstance(start, next)) next++;
        return next - start;
    }

    /// <summary>Fills <paramref name="plan"/> and returns the bytes the flush region needs.</summary>
    public static int Plan<T>(ref T source, bool instancing, List<PlannedDraw> plan) where T : IDrawRecordSource
    {
        plan.Clear();
        var cursor = 0;
        int lastTransform = -1, lastTransformOffset = 0, lastMaterial = -1, lastMaterialOffset = 0;
        var count = source.Count;
        for (var i = 0; i < count;)
        {
            var run = instancing ? RunLength(ref source, i, MaxInstances) : 1;
            var draw = new PlannedDraw { First = i, Count = run };
            if (lastMaterial >= 0 && source.SameMaterial(lastMaterial, i)) draw.MaterialOffset = lastMaterialOffset;
            else
            {
                draw.MaterialOffset = lastMaterialOffset = Allocate(ref cursor, MaterialBytes);
                draw.WriteMaterial = true;
                lastMaterial = i;
            }
            if (run > 1)
            {
                draw.InstanceBytes = run * InstanceRecordBytes;
                draw.InstanceOffset = Allocate(ref cursor, draw.InstanceBytes);
            }
            else if (lastTransform >= 0 && source.SameTransform(lastTransform, i)) draw.TransformOffset = lastTransformOffset;
            else
            {
                draw.TransformOffset = lastTransformOffset = Allocate(ref cursor, TransformBytes);
                draw.WriteTransform = true;
                lastTransform = i;
            }
            plan.Add(draw);
            i += run;
        }
        return cursor;
    }

    /// <summary>How many most recent groups a draw is compared with when looking for a group to join.</summary>
    public const int GroupSearchLimit = 32;

    /// <summary>
    /// Fills <paramref name="order"/> with the submit order: identity, except that inside each maximal block of
    /// consecutive commutative draws, draws that can share an instanced draw are gathered behind the first of
    /// their kind (groups keep first-appearance order, members keep their relative order). Non-commutative draws
    /// never move and nothing moves across them. Returns whether the order differs from the identity.
    /// </summary>
    public static bool GroupCommutative<T>(ref T source, int[] order, int[] groupOf, int[] groupStart) where T : IReorderSource
    {
        var count = source.Count;
        var changed = false;
        for (var i = 0; i < count; i++) order[i] = i;
        for (var start = 0; start < count;)
        {
            if (!source.Commutative(start)) { start++; continue; }
            var end = start + 1;
            while (end < count && source.Commutative(end)) end++;
            if (end - start > 1) changed |= GroupBlock(ref source, order, groupOf, groupStart, start, end);
            start = end;
        }
        return changed;
    }

    private static bool GroupBlock<T>(ref T source, int[] order, int[] groupOf, int[] groupStart, int start, int end)
        where T : IReorderSource
    {
        var groups = 0; // groupStart[g] first holds the head draw of group g, then its member count, then its output position
        for (var i = start; i < end; i++)
        {
            var group = -1;
            for (var g = groups - 1; g >= 0 && g >= groups - GroupSearchLimit; g--)
                if (source.CanInstance(groupStart[g], i)) { group = g; break; }
            if (group < 0) { group = groups++; groupStart[group] = i; }
            groupOf[i] = group;
        }
        if (groups == end - start) return false;
        for (var g = 0; g < groups; g++) groupStart[g] = 0;
        for (var i = start; i < end; i++) groupStart[groupOf[i]]++;
        var position = start;
        for (var g = 0; g < groups; g++) { var size = groupStart[g]; groupStart[g] = position; position += size; }
        var changed = false;
        for (var i = start; i < end; i++)
        {
            var slot = groupStart[groupOf[i]]++;
            order[slot] = i;
            changed |= slot != i;
        }
        return changed;
    }

    private static int Allocate(ref int cursor, int bytes)
    {
        var offset = cursor;
        cursor += ObjectConstantRing.Align(bytes);
        return offset;
    }
}
