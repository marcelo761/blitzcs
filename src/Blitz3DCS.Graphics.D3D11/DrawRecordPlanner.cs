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

    private static int Allocate(ref int cursor, int bytes)
    {
        var offset = cursor;
        cursor += ObjectConstantRing.Align(bytes);
        return offset;
    }
}
