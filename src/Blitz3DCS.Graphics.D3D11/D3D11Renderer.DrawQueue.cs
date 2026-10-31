using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Vortice.D3DCompiler;
using Vortice.Direct3D11;

namespace Blitz3DCS.Graphics.D3D11;

/// <summary>
/// Object-constant submission. Mesh draws are queued; a flush packs every queued draw's Transform and Material record
/// into one dynamic constant buffer with a single Map (WRITE_NO_OVERWRITE ring, WRITE_DISCARD on wrap) and binds each
/// draw's records with the D3D11.1 VS/PSSetConstantBuffers1 offset variants instead of two Map(WRITE_DISCARD) per
/// draw. Consecutive queued draws that differ only in transform and MaterialColor (particles/sprites sharing the quad
/// mesh, repeated props) become one DrawIndexedInstanced whose vertex shader reads the same per-object values from
/// cbuffer Instances. The pixel shader and the non-instanced vertex shader are unchanged, and the instanced vertex
/// shader is derived from the original text by renaming the constant sources only, so the float arithmetic is the
/// same. Any other renderer call flushes the queue first (CheckAccess), so device-visible order is unchanged.
/// Disabled by BLITZCS_D3D11_NOOPT=1 (whole feature) or BLITZCS_D3D11_NOINSTANCING=1 (instancing only), and when the
/// device does not report ConstantBufferOffsetting.
/// </summary>
public sealed unsafe partial class D3D11Renderer
{
    internal static readonly bool InstancingRequested =
        Optimize && Environment.GetEnvironmentVariable("BLITZCS_D3D11_NOINSTANCING") != "1";

    internal const int LayerSlots = 8;
    private const int InitialObjectBufferBytes = 2 << 20;
    private static readonly uint TransformWindow = ObjectConstantRing.ConstantCount(DrawRecordPlanner.TransformBytes);
    private static readonly uint MaterialWindow = ObjectConstantRing.ConstantCount(DrawRecordPlanner.MaterialBytes);

    private ID3D11DeviceContext1? _context1;
    private ID3D11Buffer? _objectBuffer;
    private ObjectConstantRing? _objectRing;
    private ID3D11VertexShader? _instancedVertexShader;
    private bool _queueDraws;
    private bool _instancing;

    private QueuedMeshDraw[] _queuedDraws = new QueuedMeshDraw[256];
    private TransformConstants[] _queuedTransforms = new TransformConstants[256];
    private MaterialConstants[] _queuedMaterials = new MaterialConstants[256];
    private D3D11TextureLayer[] _queuedLayers = new D3D11TextureLayer[256 * LayerSlots];
    private int _queueCount;
    private int _queueEpoch = 1;
    private readonly List<PlannedDraw> _drawPlan = [];
    private readonly ID3D11Buffer[] _objectBufferOne = new ID3D11Buffer[1];
    private readonly ID3D11Buffer[] _objectBufferPair = new ID3D11Buffer[2];
    private long _vsTransformWindow, _vsMaterialWindow, _vsInstanceWindow, _psMaterialWindow;

    /// <summary>Whether mesh draws go through the object-constant queue on this device.</summary>
    internal bool QueuesMeshDraws => _queueDraws;

    /// <summary>Whether compatible consecutive mesh draws are merged into instanced draws on this device.</summary>
    internal bool InstancesMeshDraws => _instancing;

    /// <summary>Issues every queued mesh draw now (also implied by any other renderer call).</summary>
    public void SubmitQueuedDraws() => CheckAccess();

    internal readonly record struct QueuedMeshDraw(
        D3D11Mesh Mesh, uint IndexCount, int LayerCount, D3D11LightingDrawState State, bool AlphaBlend);

    private void CreateObjectConstantPipeline()
    {
        if (!Optimize) return;
        FeatureDataD3D11Options options;
        try { options = _device!.CheckFeatureOptions(); }
        catch { return; }
        if (!options.ConstantBufferOffsetting) return;
        var context1 = _context!.QueryInterfaceOrNull<ID3D11DeviceContext1>();
        if (context1 is null) return;
        try
        {
            var ring = new ObjectConstantRing(InitialObjectBufferBytes, options.MapNoOverwriteOnDynamicConstantBuffer);
            _objectBuffer = CreateObjectBuffer(ring.Capacity);
            _objectRing = ring;
            _context1 = context1;
        }
        catch
        {
            // Constant buffers larger than 64 KiB need the D3D11.1 runtime; keep the per-draw path otherwise.
            context1.Dispose();
            return;
        }
        _queueDraws = true;
        if (!InstancingRequested) return;
        try
        {
            var bytecode = Compiler.Compile(InstancedShaderSource, "VSInstanced", "triangle-instanced.hlsl", "vs_4_0",
                ShaderFlags.EnableStrictness);
            _instancedVertexShader = _device!.CreateVertexShader(bytecode.Span);
            _instancing = true;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"BlitzCS D3D11: instanced vertex shader unavailable, drawing objects individually: {error.Message}");
        }
    }

    private ID3D11Buffer CreateObjectBuffer(int bytes) =>
        _device!.CreateBuffer((uint)bytes, BindFlags.ConstantBuffer, ResourceUsage.Dynamic, CpuAccessFlags.Write);

    private void ReleaseObjectConstantPipeline()
    {
        DiscardMeshQueue();
        _queueDraws = _instancing = false;
        _instancedVertexShader?.Dispose(); _instancedVertexShader = null;
        _objectBuffer?.Dispose(); _objectBuffer = null;
        _objectBufferOne[0] = _objectBufferPair[0] = _objectBufferPair[1] = null!;
        _context1?.Dispose(); _context1 = null;
        _objectRing = null;
    }

    private void QueueMesh(D3D11Mesh mesh, uint indexCount, in Matrix4x4 worldViewProjection,
        ReadOnlySpan<D3D11TextureLayer> layers, bool alphaBlend, Vector4 color, D3D11Fog? fog,
        in Matrix4x4 worldView, D3D11LightingDrawState state, in Matrix4x4 world)
    {
        // Mip generation resets device state and rewrites texture contents: keep it ordered after earlier draws.
        if (_queueCount > 0 && NeedsMipmaps(layers)) FlushMeshQueue();
        EnsureMipmapsGenerated(layers);

        var index = _queueCount;
        if (index == _queuedDraws.Length) GrowQueue();
        _queuedDraws[index] = new QueuedMeshDraw(mesh, indexCount, layers.Length, state, alphaBlend);
        layers.CopyTo(_queuedLayers.AsSpan(index * LayerSlots, LayerSlots));
        // EvaluateLighting returns before reading World/NormalMatrix when FX_FULLBRIGHT (1) is set, so fullbright draws
        // (sprites, particles) skip the inversion: only constants the shader never reads differ.
        var normal = (state.Effects & 1) != 0 ? Matrix4x4.Identity : NormalMatrixFor(world);
        BuildTransformConstants(out _queuedTransforms[index], worldViewProjection, worldView, world, normal);
        ref var material = ref _queuedMaterials[index];
        material = default;
        BuildMaterialConstants(ref material, layers, color, clampRegion: false, fog, state.Effects, state.Shininess);
        mesh.QueueEpoch = _queueEpoch;
        _queueCount = index + 1;
        if (D3D11FrameCounters.Enabled) { D3D11FrameCounters.Add(D3D11FrameCounters.MeshDraws); D3D11FrameCounters.Add(D3D11FrameCounters.Triangles, indexCount / 3); }
    }

    private static bool NeedsMipmaps(ReadOnlySpan<D3D11TextureLayer> layers)
    {
        foreach (var layer in layers)
            if (layer.Texture is { MipmapsDirty: true }) return true;
        return false;
    }

    private void GrowQueue()
    {
        var size = _queuedDraws.Length * 2;
        Array.Resize(ref _queuedDraws, size);
        Array.Resize(ref _queuedTransforms, size);
        Array.Resize(ref _queuedMaterials, size);
        Array.Resize(ref _queuedLayers, size * LayerSlots);
    }

    /// <summary>Flushes when a queued draw uses <paramref name="mesh"/>, whose buffers are about to change or go away.</summary>
    private void FlushDrawsReferencing(D3D11Mesh mesh)
    {
        if (_queueCount > 0 && mesh.QueueEpoch == _queueEpoch) FlushMeshQueue();
    }

    private void DiscardMeshQueue()
    {
        Array.Clear(_queuedDraws, 0, _queueCount);
        Array.Clear(_queuedLayers, 0, _queueCount * LayerSlots);
        _queueCount = 0;
        _queueEpoch++;
    }

    private void FlushMeshQueue()
    {
        var count = _queueCount;
        if (count == 0) return;
        _queueCount = 0;
        _queueEpoch++;
        var source = new QueuedRecordSource(this, count);
        var bytes = DrawRecordPlanner.Plan(ref source, _instancing, _drawPlan);
        var reservation = _objectRing!.Reserve(bytes);
        if (reservation.GrowTo != 0)
        {
            _objectBuffer!.Dispose();
            _objectBuffer = null;
            _objectBuffer = CreateObjectBuffer(reservation.GrowTo);
        }
        var plan = CollectionsMarshal.AsSpan(_drawPlan);
        var mapped = _context!.Map(_objectBuffer!, 0, reservation.Discard ? MapMode.WriteDiscard : MapMode.WriteNoOverwrite);
        try
        {
            var region = new Span<byte>((byte*)mapped.DataPointer + reservation.Offset, bytes);
            for (var i = 0; i < plan.Length; i++) WritePlannedRecords(region, plan[i]);
        }
        finally { _context.Unmap(_objectBuffer!, 0); }

        BindRenderTargets();
        var instancedDraws = 0;
        var instancedObjects = 0;
        for (var i = 0; i < plan.Length; i++)
        {
            IssuePlannedDraw(plan[i], reservation.Offset);
            if (plan[i].Instanced) { instancedDraws++; instancedObjects += plan[i].Count; }
        }
        if (D3D11FrameCounters.Enabled)
        {
            D3D11FrameCounters.Add(D3D11FrameCounters.DrawQueueFlushes);
            D3D11FrameCounters.Add(D3D11FrameCounters.ConstantBufferMaps);
            D3D11FrameCounters.Add(D3D11FrameCounters.ObjectBufferBytes, bytes);
            D3D11FrameCounters.Add(D3D11FrameCounters.ApiDraws, plan.Length);
            D3D11FrameCounters.Add(D3D11FrameCounters.InstancedDraws, instancedDraws);
            D3D11FrameCounters.Add(D3D11FrameCounters.InstancedObjects, instancedObjects);
            CountSkips();
        }
    }

    private void WritePlannedRecords(Span<byte> region, in PlannedDraw draw)
    {
        if (draw.WriteMaterial)
            MemoryMarshal.Write(region[draw.MaterialOffset..], in _queuedMaterials[draw.First]);
        if (draw.WriteTransform)
            MemoryMarshal.Write(region[draw.TransformOffset..], in _queuedTransforms[draw.First]);
        if (!draw.Instanced) return;
        var instances = region.Slice(draw.InstanceOffset, draw.InstanceBytes);
        for (var k = 0; k < draw.Count; k++)
            WriteInstanceRecord(instances[(k * DrawRecordPlanner.InstanceRecordBytes)..],
                _queuedTransforms[draw.First + k], _queuedMaterials[draw.First + k].Color);
    }

    /// <summary>One HLSL InstanceData element: the object's Transform record followed by its MaterialColor.</summary>
    internal static void WriteInstanceRecord(Span<byte> destination, in TransformConstants transform, Vector4 materialColor)
    {
        MemoryMarshal.Write(destination, in transform);
        MemoryMarshal.Write(destination[DrawRecordPlanner.TransformBytes..], in materialColor);
    }

    private void IssuePlannedDraw(in PlannedDraw planned, int regionOffset)
    {
        ref readonly var draw = ref _queuedDraws[planned.First];
        BindTriangleList();
        BindInputLayout(_inputLayout!);
        BindVertexBuffer(draw.Mesh.VertexBuffer, (uint)sizeof(D3D11Vertex));
        BindIndexBuffer(draw.Mesh.IndexBuffer);
        var materialOffset = regionOffset + planned.MaterialOffset;
        if (planned.Instanced)
        {
            BindVertexShader(_instancedVertexShader!);
            if (WindowStale(PipelineSlot.VsConstantBuffer1, ref _vsMaterialWindow, materialOffset, MaterialWindow))
                SetVsWindow(1, materialOffset, MaterialWindow);
            var instanceWindow = ObjectConstantRing.ConstantCount(planned.InstanceBytes);
            var instanceOffset = regionOffset + planned.InstanceOffset;
            if (WindowStale(PipelineSlot.VsConstantBuffer3, ref _vsInstanceWindow, instanceOffset, instanceWindow))
                SetVsWindow(3, instanceOffset, instanceWindow);
        }
        else
        {
            BindVertexShader(_vertexShader!);
            var transformOffset = regionOffset + planned.TransformOffset;
            var transformStale = WindowStale(PipelineSlot.VsConstantBuffer0, ref _vsTransformWindow, transformOffset, TransformWindow);
            var materialStale = WindowStale(PipelineSlot.VsConstantBuffer1, ref _vsMaterialWindow, materialOffset, MaterialWindow);
            if (transformStale && materialStale) SetVsTransformAndMaterial(transformOffset, materialOffset);
            else if (transformStale) SetVsWindow(0, transformOffset, TransformWindow);
            else if (materialStale) SetVsWindow(1, materialOffset, MaterialWindow);
        }
        BindPixelShader(_texturedPixelShader!);
        SetPixelResources(_queuedLayers.AsSpan(planned.First * LayerSlots, draw.LayerCount));
        if (WindowStale(PipelineSlot.PsConstantBuffer1, ref _psMaterialWindow, materialOffset, MaterialWindow))
            SetPsMaterialWindow(materialOffset);
        BindLightingDrawState(draw.State, draw.AlphaBlend);
        if (planned.Instanced) _context!.DrawIndexedInstanced(draw.IndexCount, (uint)planned.Count, 0, 0, 0);
        else _context!.DrawIndexed(draw.IndexCount, 0, 0);
    }

    /// <summary>
    /// True when the slot must be (re)bound: another buffer is bound there (or device state was reset, which
    /// invalidates the tracker), or the object buffer is bound with a different window.
    /// </summary>
    private bool WindowStale(PipelineSlot slot, ref long bound, int offset, uint constants)
    {
        var window = ((long)constants << 32) | (uint)offset;
        if (!_tracker.Changed(slot, _objectBuffer) && bound == window) return false;
        bound = window;
        return true;
    }

    private void SetVsWindow(uint slot, int offset, uint constants)
    {
        _objectBufferOne[0] = _objectBuffer!;
        var first = ObjectConstantRing.FirstConstant(offset);
        _context1!.VSSetConstantBuffers1(slot, 1, _objectBufferOne, new ReadOnlySpan<uint>(in first), new ReadOnlySpan<uint>(in constants));
        if (D3D11FrameCounters.Enabled) D3D11FrameCounters.Add(D3D11FrameCounters.ConstantBufferBinds);
    }

    private void SetVsTransformAndMaterial(int transformOffset, int materialOffset)
    {
        _objectBufferPair[0] = _objectBufferPair[1] = _objectBuffer!;
        Span<uint> firsts = stackalloc uint[2]
            { ObjectConstantRing.FirstConstant(transformOffset), ObjectConstantRing.FirstConstant(materialOffset) };
        Span<uint> counts = stackalloc uint[2] { TransformWindow, MaterialWindow };
        _context1!.VSSetConstantBuffers1(0, 2, _objectBufferPair, firsts, counts);
        if (D3D11FrameCounters.Enabled) D3D11FrameCounters.Add(D3D11FrameCounters.ConstantBufferBinds);
    }

    private void SetPsMaterialWindow(int offset)
    {
        _objectBufferOne[0] = _objectBuffer!;
        var first = ObjectConstantRing.FirstConstant(offset);
        var constants = MaterialWindow;
        _context1!.PSSetConstantBuffers1(1, 1, _objectBufferOne, new ReadOnlySpan<uint>(in first), new ReadOnlySpan<uint>(in constants));
        if (D3D11FrameCounters.Enabled) D3D11FrameCounters.Add(D3D11FrameCounters.ConstantBufferBinds);
    }

    /// <summary>
    /// Whether two queued draws can be one instanced draw: same mesh, index count, texture layers, pipeline state and
    /// a material record that differs at most in MaterialColor (read only by the vertex shader, which takes it from
    /// the instance record). Masked materials whose alpha-test threshold depends on the color differ in RegionFlags.w
    /// and therefore never merge.
    /// </summary>
    internal static bool CanShareInstancedDraw(in QueuedMeshDraw first, in QueuedMeshDraw candidate,
        ReadOnlySpan<D3D11TextureLayer> firstLayers, ReadOnlySpan<D3D11TextureLayer> candidateLayers,
        in MaterialConstants firstMaterial, in MaterialConstants candidateMaterial) =>
        ReferenceEquals(first.Mesh, candidate.Mesh) && first.IndexCount == candidate.IndexCount &&
        first.AlphaBlend == candidate.AlphaBlend && first.State == candidate.State &&
        firstLayers.SequenceEqual(candidateLayers) && SameBytesExceptColor(firstMaterial, candidateMaterial);

    private static bool SameBytesExceptColor(in MaterialConstants a, in MaterialConstants b) =>
        MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(in a, 1))[16..]
            .SequenceEqual(MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(in b, 1))[16..]);

    private readonly struct QueuedRecordSource(D3D11Renderer renderer, int count) : IDrawRecordSource
    {
        public int Count => count;
        public bool SameTransform(int a, int b) => SameBytes(renderer._queuedTransforms[a], renderer._queuedTransforms[b]);
        public bool SameMaterial(int a, int b) => SameBytes(renderer._queuedMaterials[a], renderer._queuedMaterials[b]);

        public bool CanInstance(int first, int candidate)
        {
            var draws = renderer._queuedDraws;
            if (!ReferenceEquals(draws[first].Mesh, draws[candidate].Mesh)) return false;
            var layers = renderer._queuedLayers;
            return CanShareInstancedDraw(draws[first], draws[candidate],
                layers.AsSpan(first * LayerSlots, draws[first].LayerCount),
                layers.AsSpan(candidate * LayerSlots, draws[candidate].LayerCount),
                renderer._queuedMaterials[first], renderer._queuedMaterials[candidate]);
        }
    }

    /// <summary>Derived on use (not in a static initializer) so a derivation failure only disables instancing.</summary>
    internal static string InstancedShaderSource => BuildInstancedShaderSource(TriangleShader);

    /// <summary>The unchanged shader text the original entry points are compiled from.</summary>
    internal static string BaseShaderSource => TriangleShader;

    /// <summary>
    /// Appends VSInstanced to the shader text. Its body is the original VSMain/EvaluateLighting text with only the
    /// per-object constant sources renamed to the instance record (cbuffer Instances, indexed by SV_InstanceID), so
    /// every arithmetic expression, its operand order and the matrix layout stay those of VSMain. The original entry
    /// points are compiled from the unchanged <see cref="TriangleShader"/> text.
    /// </summary>
    internal static string BuildInstancedShaderSource(string source)
    {
        var lighting = Between(source, "void EvaluateLighting(", "PixelInput VSMain(");
        lighting = ReplaceOnce(lighting,
            "void EvaluateLighting(VertexInput input, out float4 diffuse, out float3 specular)",
            "void EvaluateInstanceLighting(VertexInput input, InstanceData instance, out float4 diffuse, out float3 specular)");
        lighting = ReplaceOnce(lighting, "* MaterialColor;", "* instance.MaterialColor;");
        lighting = ReplaceOnce(lighting, "float4(input.Position, 1), World)", "float4(input.Position, 1), instance.World)");
        lighting = ReplaceOnce(lighting, "(float3x3)NormalMatrix)", "(float3x3)instance.NormalMatrix)");

        var vertex = Between(source, "PixelInput VSMain(", "PixelInput VSBlit(");
        vertex = ReplaceOnce(vertex, "PixelInput VSMain(VertexInput input)",
            "PixelInput VSInstanced(VertexInput input, uint instanceId : SV_InstanceID)");
        vertex = ReplaceOnce(vertex, "PixelInput output;", "InstanceData instance = Instances[instanceId];\n    PixelInput output;");
        vertex = ReplaceOnce(vertex, ", WorldViewProjection);", ", instance.WorldViewProjection);");
        vertex = ReplaceOnce(vertex, ", WorldView).z", ", instance.WorldView).z");
        vertex = ReplaceOnce(vertex, "EvaluateLighting(input, output.Color", "EvaluateInstanceLighting(input, instance, output.Color");

        foreach (var derived in new[] { lighting, vertex })
            if (Regex.IsMatch(derived, @"(?<![\w.])(WorldViewProjection|WorldView|World|NormalMatrix|MaterialColor)\b"))
                throw new InvalidOperationException("The instanced vertex shader still reads a per-draw constant.");

        return source + $$"""

            struct InstanceData { float4x4 WorldViewProjection; float4x4 WorldView; float4x4 World; float4x4 NormalMatrix; float4 MaterialColor; };
            cbuffer InstanceBuffer : register(b3) { InstanceData Instances[{{DrawRecordPlanner.MaxInstances}}]; };

            """ + lighting + vertex;
    }

    private static string Between(string source, string start, string end)
    {
        var from = source.IndexOf(start, StringComparison.Ordinal);
        var to = from < 0 ? -1 : source.IndexOf(end, from, StringComparison.Ordinal);
        if (from < 0 || to < 0) throw new InvalidOperationException($"Shader section '{start}' was not found.");
        return source[from..to];
    }

    private static string ReplaceOnce(string text, string oldValue, string newValue)
    {
        var index = text.IndexOf(oldValue, StringComparison.Ordinal);
        if (index < 0 || text.IndexOf(oldValue, index + oldValue.Length, StringComparison.Ordinal) >= 0)
            throw new InvalidOperationException($"Shader text '{oldValue}' must occur exactly once.");
        return string.Concat(text.AsSpan(0, index), newValue, text.AsSpan(index + oldValue.Length));
    }
}
