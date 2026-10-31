using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using Blitz3DCS.Windowing;
using SharpGen.Runtime;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using static Vortice.Direct3D11.D3D11;

namespace Blitz3DCS.Graphics.D3D11;

/// <summary>A small D3D11 renderer that owns an SDL window and presents through DXGI.</summary>
/// <remarks>All methods must be called on the thread that constructs the renderer.</remarks>
public sealed unsafe partial class D3D11Renderer : IDisposable
{
    private const Format BackBufferFormat = Format.R8G8B8A8_UNorm;
    private const string TriangleShader = """
        cbuffer Transform : register(b0) { float4x4 WorldViewProjection; float4x4 WorldView; float4x4 World; float4x4 NormalMatrix; };
        cbuffer Material : register(b1)
        {
            float4 MaterialColor;
            float4 BlendModes0;
            float4 BlendModes1;
            float4 UseAlpha0;
            float4 UseAlpha1;
            float4 RegionFlags;
            float4 UvRowsX[8];
            float4 UvRowsY[8];
            float4 BumpMatrices[8];
            float4 FogColor;
            float4 FogParameters;
            float4 Shininess;
        };
        struct LightData { float4 DirectionType; float4 PositionRange; float4 ColorAttenuation; float4 Cone; };
        cbuffer Lighting : register(b2) { float4 AmbientLight; float4 EyePosition; float4 LightMetadata; LightData Lights[8]; };
        Texture2D Texture0 : register(t0);
        Texture2D Texture1 : register(t1);
        Texture2D Texture2 : register(t2);
        Texture2D Texture3 : register(t3);
        Texture2D Texture4 : register(t4);
        Texture2D Texture5 : register(t5);
        Texture2D Texture6 : register(t6);
        Texture2D Texture7 : register(t7);
        SamplerState Sampler0 : register(s0);
        SamplerState Sampler1 : register(s1);
        SamplerState Sampler2 : register(s2);
        SamplerState Sampler3 : register(s3);
        SamplerState Sampler4 : register(s4);
        SamplerState Sampler5 : register(s5);
        SamplerState Sampler6 : register(s6);
        SamplerState Sampler7 : register(s7);
        struct VertexInput { float3 Position : POSITION; float4 Color : COLOR; float3 Normal : NORMAL; float2 TexCoord : TEXCOORD; float2 TexCoord2 : TEXCOORD2; };
        struct BlitVertexInput { float3 Position : POSITION; float4 Color : COLOR; float2 TexCoord : TEXCOORD; float4 RegionUvBounds : TEXCOORD1; };
        struct PixelInput { float4 Position : SV_POSITION; float4 Color : COLOR; nointerpolation float4 FlatColor : COLOR1; float2 TexCoord : TEXCOORD; float4 RegionUvBounds : TEXCOORD1; float2 TexCoord2 : TEXCOORD2; float ViewDepth : TEXCOORD3; float3 Specular : TEXCOORD4; nointerpolation float3 FlatSpecular : TEXCOORD5; };
        float3 SafeNormalize(float3 value) { return value * rsqrt(max(dot(value, value), 1.0e-20)); }
        void EvaluateLighting(VertexInput input, out float4 diffuse, out float3 specular)
        {
            uint effects = (uint)RegionFlags.y;
            float4 vertexColor = (effects & 2) != 0 ? input.Color : float4(1, 1, 1, 1);
            vertexColor.a = (effects & 32) != 0 ? input.Color.a : 1.0;
            diffuse = vertexColor * MaterialColor;
            specular = 0;
            if ((effects & 1) != 0) return;

            float3 worldPosition = mul(float4(input.Position, 1), World).xyz;
            float3 normal = SafeNormalize(mul(input.Normal, (float3x3)NormalMatrix));
            float3 lighting = (effects & 0x4000) != 0 ? 0 : AmbientLight.rgb;
            float3 specularLighting = 0;
            [unroll] for (uint index = 0; index < 8; ++index)
            {
                if (index >= (uint)LightMetadata.x) break;
                LightData light = Lights[index];
                uint type = (uint)light.DirectionType.w;
                if ((effects & 0x4000) != 0 && type == 1) continue;
                float3 toLight;
                float attenuation = 1.0;
                if (type == 1)
                {
                    toLight = -light.DirectionType.xyz;
                }
                else
                {
                    float3 delta = light.PositionRange.xyz - worldPosition;
                    float distance = length(delta);
                    toLight = delta / max(distance, 1.0e-10);
                    attenuation = light.PositionRange.w / max(distance, 1.0e-10);
                    if (type == 3)
                    {
                        float coneCosine = dot(-toLight, SafeNormalize(light.DirectionType.xyz));
                        float innerCosine = cos(light.Cone.x * 0.5);
                        float outerCosine = cos(light.Cone.y * 0.5);
                        attenuation *= saturate((coneCosine - outerCosine) / max(innerCosine - outerCosine, 1.0e-7));
                    }
                }

                float ndotl = saturate(dot(normal, SafeNormalize(toLight)));
                lighting += light.ColorAttenuation.rgb * (ndotl * attenuation);
                if (Shininess.y > 0 && ndotl > 0)
                {
                    float3 halfVector = SafeNormalize(SafeNormalize(toLight) + SafeNormalize(EyePosition.xyz - worldPosition));
                    float spec = pow(saturate(dot(normal, halfVector)), Shininess.y) * attenuation;
                    // Legacy D3D lighting keeps the specular material color white;
                    // LightColor scales diffuse response only.
                    specularLighting += spec * Shininess.x;
                }
            }
            diffuse.rgb *= lighting;
            specular = saturate(specularLighting);
        }
        PixelInput VSMain(VertexInput input)
        {
            PixelInput output;
            output.Position = mul(float4(input.Position, 1.0), WorldViewProjection);
            output.ViewDepth = abs(mul(float4(input.Position, 1.0), WorldView).z);
            EvaluateLighting(input, output.Color, output.Specular);
            output.FlatColor = output.Color;
            output.FlatSpecular = output.Specular;
            output.TexCoord = input.TexCoord;
            output.TexCoord2 = input.TexCoord2;
            output.RegionUvBounds = float4(0, 0, 1, 1);
            return output;
        }
        PixelInput VSBlit(BlitVertexInput input)
        {
            PixelInput output;
            output.Position = mul(float4(input.Position, 1.0), WorldViewProjection);
            output.ViewDepth = 0;
            output.Color = input.Color * MaterialColor;
            output.Specular = 0;
            output.FlatColor = input.Color;
            output.FlatSpecular = 0;
            output.TexCoord = input.TexCoord;
            output.TexCoord2 = input.TexCoord;
            output.RegionUvBounds = input.RegionUvBounds;
            return output;
        }
        float4 ApplyFog(float4 color, float depth)
        {
            if (FogParameters.z == 0) return color;
            float range = FogParameters.y - FogParameters.x;
            float factor = abs(range) > 1.0e-20
                ? saturate((FogParameters.y - depth) / range)
                : (depth < FogParameters.y ? 1.0 : 0.0);
            color.rgb = lerp(FogColor.rgb, color.rgb, factor);
            return color;
        }
        float4 ResolveColor(PixelInput input) { return ((uint)RegionFlags.y & 4) != 0 ? input.FlatColor : input.Color; }
        float3 ResolveSpecular(PixelInput input) { return ((uint)RegionFlags.y & 4) != 0 ? input.FlatSpecular : input.Specular; }
        float4 PSColor(PixelInput input) : SV_TARGET { float4 result = ResolveColor(input); result.rgb = saturate(result.rgb + ResolveSpecular(input)); return ApplyFog(result, input.ViewDepth); }
        float4 BlendTexture(float4 current, float4 texel, int mode, float useAlpha)
        {
            if (mode == 1) current.rgb = lerp(current.rgb, texel.rgb, texel.a);
            else if (mode == 2) current.rgb *= texel.rgb;
            else if (mode == 3) current.rgb = saturate(current.rgb + texel.rgb);
            else if (mode == 5) current.rgb = saturate(current.rgb * texel.rgb * 2.0);
            if (useAlpha != 0) current.a *= texel.a;
            return current;
        }
        float2 StageUV(PixelInput input, int stage)
        {
            float2 source = UvRowsX[stage].w != 0 ? input.TexCoord2 : input.TexCoord;
            float3 uv = float3(source, 1);
            float2 result = float2(dot(uv, UvRowsX[stage].xyz), dot(uv, UvRowsY[stage].xyz));
            return RegionFlags.x != 0 ? clamp(result, input.RegionUvBounds.xy, input.RegionUvBounds.zw) : result;
        }
        float2 BumpedStageUV(PixelInput input, int stage)
        {
            float2 result = StageUV(input, stage);
            float2 bump;
            float4 coefficients;
            if (stage == 1 && BlendModes0.x == 6)
            {
                bump = Texture0.Sample(Sampler0, StageUV(input, 0)).rg * 2.0 - 1.0;
                coefficients = BumpMatrices[0];
                result += float2(coefficients.x * bump.x + coefficients.z * bump.y,
                    coefficients.y * bump.x + coefficients.w * bump.y);
            }
            else if (stage == 2 && BlendModes0.y == 6)
            {
                bump = Texture1.Sample(Sampler1, StageUV(input, 1)).rg * 2.0 - 1.0;
                coefficients = BumpMatrices[1];
                result += float2(coefficients.x * bump.x + coefficients.z * bump.y,
                    coefficients.y * bump.x + coefficients.w * bump.y);
            }
            else if (stage == 3 && BlendModes0.z == 6)
            {
                bump = Texture2.Sample(Sampler2, StageUV(input, 2)).rg * 2.0 - 1.0;
                coefficients = BumpMatrices[2];
                result += float2(coefficients.x * bump.x + coefficients.z * bump.y,
                    coefficients.y * bump.x + coefficients.w * bump.y);
            }
            else if (stage == 4 && BlendModes0.w == 6)
            {
                bump = Texture3.Sample(Sampler3, StageUV(input, 3)).rg * 2.0 - 1.0;
                coefficients = BumpMatrices[3];
                result += float2(coefficients.x * bump.x + coefficients.z * bump.y,
                    coefficients.y * bump.x + coefficients.w * bump.y);
            }
            else if (stage == 5 && BlendModes1.x == 6)
            {
                bump = Texture4.Sample(Sampler4, StageUV(input, 4)).rg * 2.0 - 1.0;
                coefficients = BumpMatrices[4];
                result += float2(coefficients.x * bump.x + coefficients.z * bump.y,
                    coefficients.y * bump.x + coefficients.w * bump.y);
            }
            else if (stage == 6 && BlendModes1.y == 6)
            {
                bump = Texture5.Sample(Sampler5, StageUV(input, 5)).rg * 2.0 - 1.0;
                coefficients = BumpMatrices[5];
                result += float2(coefficients.x * bump.x + coefficients.z * bump.y,
                    coefficients.y * bump.x + coefficients.w * bump.y);
            }
            else if (stage == 7 && BlendModes1.z == 6)
            {
                bump = Texture6.Sample(Sampler6, StageUV(input, 6)).rg * 2.0 - 1.0;
                coefficients = BumpMatrices[6];
                result += float2(coefficients.x * bump.x + coefficients.z * bump.y,
                    coefficients.y * bump.x + coefficients.w * bump.y);
            }
            return result;
        }
        float4 PSTextured(PixelInput input) : SV_TARGET
        {
            float4 result = ResolveColor(input);
            if (BlendModes0.x != 0 && BlendModes0.x != 6) result = BlendTexture(result, Texture0.Sample(Sampler0, BumpedStageUV(input, 0)), (int)BlendModes0.x, UseAlpha0.x);
            if (BlendModes0.y != 0 && BlendModes0.y != 6) result = BlendTexture(result, Texture1.Sample(Sampler1, BumpedStageUV(input, 1)), (int)BlendModes0.y, UseAlpha0.y);
            if (BlendModes0.z != 0 && BlendModes0.z != 6) result = BlendTexture(result, Texture2.Sample(Sampler2, BumpedStageUV(input, 2)), (int)BlendModes0.z, UseAlpha0.z);
            if (BlendModes0.w != 0 && BlendModes0.w != 6) result = BlendTexture(result, Texture3.Sample(Sampler3, BumpedStageUV(input, 3)), (int)BlendModes0.w, UseAlpha0.w);
            if (BlendModes1.x != 0 && BlendModes1.x != 6) result = BlendTexture(result, Texture4.Sample(Sampler4, BumpedStageUV(input, 4)), (int)BlendModes1.x, UseAlpha1.x);
            if (BlendModes1.y != 0 && BlendModes1.y != 6) result = BlendTexture(result, Texture5.Sample(Sampler5, BumpedStageUV(input, 5)), (int)BlendModes1.y, UseAlpha1.y);
            if (BlendModes1.z != 0 && BlendModes1.z != 6) result = BlendTexture(result, Texture6.Sample(Sampler6, BumpedStageUV(input, 6)), (int)BlendModes1.z, UseAlpha1.z);
            if (BlendModes1.w != 0 && BlendModes1.w != 6) result = BlendTexture(result, Texture7.Sample(Sampler7, BumpedStageUV(input, 7)), (int)BlendModes1.w, UseAlpha1.w);
            if (RegionFlags.z != 0 && result.a <= RegionFlags.w) discard;
            result.rgb = saturate(result.rgb + ResolveSpecular(input));
            return ApplyFog(result, input.ViewDepth);
        }
        """;

    private readonly int _threadId = Environment.CurrentManagedThreadId;
    private WindowHost? _window;
    private bool _ownsWindow;
    private IDXGISwapChain? _swapChain;
    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private ID3D11RenderTargetView? _renderTarget;
    private ID3D11Texture2D? _depthTexture;
    private ID3D11DepthStencilView? _depthView;
    private ID3D11VertexShader? _vertexShader;
    private ID3D11VertexShader? _blitVertexShader;
    private ID3D11PixelShader? _colorPixelShader;
    private ID3D11PixelShader? _texturedPixelShader;
    private ID3D11InputLayout? _inputLayout;
    private ID3D11InputLayout? _blitInputLayout;
    private readonly ID3D11SamplerState?[] _textureSamplers = new ID3D11SamplerState[4];
    private ID3D11BlendState? _alphaBlendState;
    private readonly ID3D11ShaderResourceView[] _pixelShaderResources = new ID3D11ShaderResourceView[8];
    private readonly ID3D11SamplerState[] _pixelSamplers = new ID3D11SamplerState[8];
    private ID3D11Buffer? _vertexBuffer;
    private ID3D11Buffer? _indexBuffer;
    private ID3D11Buffer? _transformBuffer;
    private ID3D11Buffer? _materialBuffer;
    private ID3D11Buffer? _lightingBuffer;
    private ID3D11Buffer? _blitVertexBuffer;
    private ID3D11Texture2D? _blitScratch;
    private ID3D11ShaderResourceView? _blitScratchView;
    private int _blitScratchWidth;
    private int _blitScratchHeight;
    private readonly HashSet<D3D11Mesh> _meshes = [];
    private readonly HashSet<D3D11Texture> _textures = [];
    private uint _vertexCapacity;
    private uint _indexCapacity;
    private int _width;
    private int _height;
    private Viewport _viewport;
    private bool _disposed;

    public D3D11Renderer(int width, int height, string title, WindowHost? sharedWindow = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentNullException.ThrowIfNull(title);
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("The D3D11 renderer requires Windows.");

        if (sharedWindow is null)
            LegacyInput.OpenWindow(width, height, title);
        try
        {
            _ownsWindow = sharedWindow is null;
            _window = sharedWindow ?? LegacyInput.CurrentWindow;
            if (sharedWindow is not null)
            {
                sharedWindow.SetSize(width, height);
                sharedWindow.SetTitle(title);
                sharedWindow.Show();
            }
            var hwnd = _window.GetWin32WindowHandle();
            if (hwnd == 0) throw new InvalidOperationException("SDL returned a null Win32 window handle.");

            var clientWidth = _window.ClientWidth;
            var clientHeight = _window.ClientHeight;
            _width = clientWidth > 0 ? clientWidth : width;
            _height = clientHeight > 0 ? clientHeight : height;
            CreateDevice(hwnd);
            CreateWindowResources(_width, _height);
            CreateTrianglePipeline();
            CreateObjectConstantPipeline();
        }
        catch
        {
            ReleaseGraphicsResources();
            if (_ownsWindow)
                LegacyInput.CloseWindow();
            else
                sharedWindow?.Hide();
            _window = null;
            _ownsWindow = false;
            throw;
        }
    }

    /// <summary>Clears the color target and depth buffer using an opaque RGB color.</summary>
    public void Clear(float r, float g, float b, bool clearColor = true, bool clearDepth = true)
    {
        CheckAccess();
        ValidateColor(r, nameof(r));
        ValidateColor(g, nameof(g));
        ValidateColor(b, nameof(b));
        BindRenderTargets();
        if (clearColor) _context!.ClearRenderTargetView(_renderTarget!, new Color4(r, g, b, 1f));
        if (clearDepth) _context!.ClearDepthStencilView(_depthView!, DepthStencilClearFlags.Depth, 1f, 0);
    }

    /// <summary>Draws indexed, colored triangles after transforming their positions by the supplied matrix.</summary>
    public void DrawTriangles(ReadOnlySpan<D3D11Vertex> vertices, ReadOnlySpan<uint> indices, in Matrix4x4 worldViewProjection)
    {
        CheckAccess();
        ValidateTriangles(vertices, indices);

        EnsureBufferCapacity(ref _vertexBuffer, ref _vertexCapacity, checked((uint)vertices.Length), BindFlags.VertexBuffer, (uint)Marshal.SizeOf<D3D11Vertex>());
        EnsureBufferCapacity(ref _indexBuffer, ref _indexCapacity, checked((uint)indices.Length), BindFlags.IndexBuffer, sizeof(uint));
        Upload(_vertexBuffer!, MemoryMarshal.AsBytes(vertices));
        Upload(_indexBuffer!, MemoryMarshal.AsBytes(indices));
        UploadTransform(worldViewProjection, Matrix4x4.Identity);
        UploadMaterial(ReadOnlySpan<D3D11TextureLayer>.Empty, Vector4.One,
            effects: D3D11LightingDrawState.Default.Effects);

        BindRenderTargets();
        BindTriangleList();
        BindInputLayout(_inputLayout!);
        BindVertexBuffer(_vertexBuffer!, (uint)Marshal.SizeOf<D3D11Vertex>());
        BindIndexBuffer(_indexBuffer!);
        BindVertexShader(_vertexShader!);
        BindVsConstantBuffer(0, _transformBuffer!);
        BindVsConstantBuffer(1, _materialBuffer!);
        BindPixelShader(_colorPixelShader!);
        BindPsConstantBuffer(1, _materialBuffer!);
        BindVsConstantBuffer(2, _lightingBuffer!);
        BindLightingDrawState(D3D11LightingDrawState.Default, alphaBlend: false);
        SetPixelResources(ReadOnlySpan<D3D11TextureLayer>.Empty);
        _context!.DrawIndexed((uint)indices.Length, 0, 0);
    }

    /// <summary>Creates immutable GPU buffers for geometry that will be drawn repeatedly.</summary>
    public D3D11Mesh CreateMesh(ReadOnlySpan<D3D11Vertex> vertices, ReadOnlySpan<uint> indices)
        => CreateMeshCore(vertices, indices, dynamic: false);

    /// <summary>Creates a persistent writable vertex buffer with immutable triangle indices.</summary>
    public D3D11Mesh CreateDynamicMesh(ReadOnlySpan<D3D11Vertex> vertices, ReadOnlySpan<uint> indices)
        => CreateMeshCore(vertices, indices, dynamic: true);

    private D3D11Mesh CreateMeshCore(ReadOnlySpan<D3D11Vertex> vertices, ReadOnlySpan<uint> indices, bool dynamic)
    {
        CheckAccessKeepDraws();
        ValidateTriangles(vertices, indices);

        ID3D11Buffer? vertexBuffer = null;
        ID3D11Buffer? indexBuffer = null;
        try
        {
            vertexBuffer = _device!.CreateBuffer(vertices, BindFlags.VertexBuffer,
                dynamic ? ResourceUsage.Dynamic : ResourceUsage.Immutable,
                dynamic ? CpuAccessFlags.Write : CpuAccessFlags.None);
            indexBuffer = _device.CreateBuffer(indices, BindFlags.IndexBuffer, ResourceUsage.Immutable, CpuAccessFlags.None);
            var mesh = new D3D11Mesh(this, vertexBuffer, indexBuffer, checked((uint)indices.Length), vertices.Length, dynamic);
            _meshes.Add(mesh);
            return mesh;
        }
        catch
        {
            indexBuffer?.Dispose();
            vertexBuffer?.Dispose();
            throw;
        }
    }

    /// <summary>Updates an existing dynamic mesh without replacing its GPU buffers.</summary>
    /// <summary>
    /// Creates a mesh whose vertex and index buffers are dynamic with at least the given capacities, so geometry
    /// that is rebuilt every frame can be rewritten in place (Map WriteDiscard) instead of recreating buffers.
    /// </summary>
    public D3D11Mesh CreateResizableMesh(ReadOnlySpan<D3D11Vertex> vertices, ReadOnlySpan<uint> indices,
        int vertexCapacity, uint indexCapacity)
    {
        CheckAccessKeepDraws();
        ValidateTriangles(vertices, indices);
        vertexCapacity = Math.Max(vertexCapacity, vertices.Length);
        indexCapacity = Math.Max(indexCapacity, (uint)indices.Length);
        ID3D11Buffer? vertexBuffer = null;
        ID3D11Buffer? indexBuffer = null;
        try
        {
            vertexBuffer = _device!.CreateBuffer(checked((uint)vertexCapacity * (uint)sizeof(D3D11Vertex)), BindFlags.VertexBuffer,
                ResourceUsage.Dynamic, CpuAccessFlags.Write);
            indexBuffer = _device.CreateBuffer(checked(indexCapacity * sizeof(uint)), BindFlags.IndexBuffer,
                ResourceUsage.Dynamic, CpuAccessFlags.Write);
            if (vertices.Length > 0) Upload(vertexBuffer, MemoryMarshal.AsBytes(vertices));
            if (indices.Length > 0) Upload(indexBuffer, MemoryMarshal.AsBytes(indices));
            var mesh = new D3D11Mesh(this, vertexBuffer, indexBuffer, (uint)indices.Length, vertices.Length, dynamic: true)
            { IsResizable = true, VertexCapacity = vertexCapacity, IndexCapacity = indexCapacity };
            _meshes.Add(mesh);
            return mesh;
        }
        catch
        {
            indexBuffer?.Dispose();
            vertexBuffer?.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Rewrites a resizable mesh in place when the new geometry fits its buffers. Returns false (changing nothing)
    /// when it does not fit or the mesh is not resizable; the caller then creates a larger mesh.
    /// </summary>
    public bool TryUpdateResizableMesh(D3D11Mesh mesh, ReadOnlySpan<D3D11Vertex> vertices, ReadOnlySpan<uint> indices)
    {
        CheckAccessKeepDraws();
        ArgumentNullException.ThrowIfNull(mesh);
        if (!ReferenceEquals(mesh.Owner, this)) throw new ArgumentException("The mesh belongs to a different renderer.", nameof(mesh));
        mesh.ThrowIfDisposed();
        FlushDrawsReferencing(mesh);
        if (!mesh.IsResizable || vertices.Length > mesh.VertexCapacity || (uint)indices.Length > mesh.IndexCapacity) return false;
        ValidateTriangles(vertices, indices);
        var profileStart = D3D11FrameCounters.Enabled ? D3D11FrameCounters.Now() : 0;
        if (vertices.Length > 0) Upload(mesh.VertexBuffer, MemoryMarshal.AsBytes(vertices));
        if (indices.Length > 0) Upload(mesh.IndexBuffer, MemoryMarshal.AsBytes(indices));
        mesh.VertexCount = vertices.Length;
        mesh.IndexCount = (uint)indices.Length;
        if (D3D11FrameCounters.Enabled) { D3D11FrameCounters.Add(D3D11FrameCounters.DynamicVertexUploads); D3D11FrameCounters.AddSince(D3D11FrameCounters.DynamicUploadTicks, profileStart); }
        return true;
    }

    public void UpdateMeshVertices(D3D11Mesh mesh, ReadOnlySpan<D3D11Vertex> vertices)
    {
        CheckAccessKeepDraws();
        ArgumentNullException.ThrowIfNull(mesh);
        if (!ReferenceEquals(mesh.Owner, this)) throw new ArgumentException("The mesh belongs to a different renderer.", nameof(mesh));
        mesh.ThrowIfDisposed();
        FlushDrawsReferencing(mesh);
        if (!mesh.IsDynamic) throw new ArgumentException("The mesh must have been created with CreateDynamicMesh.", nameof(mesh));
        if (vertices.Length != mesh.VertexCount) throw new ArgumentException("Vertex updates must preserve the mesh vertex count.", nameof(vertices));
        var profileStart = D3D11FrameCounters.Enabled ? D3D11FrameCounters.Now() : 0;
        Upload(mesh.VertexBuffer, MemoryMarshal.AsBytes(vertices));
        if (D3D11FrameCounters.Enabled) { D3D11FrameCounters.Add(D3D11FrameCounters.DynamicVertexUploads); D3D11FrameCounters.AddSince(D3D11FrameCounters.DynamicUploadTicks, profileStart); }
    }

    /// <summary>Creates an immutable RGBA8 texture from tightly packed row-major pixel data.</summary>
    public D3D11Texture CreateTexture(int width, int height, ReadOnlySpan<byte> rgba8, bool mipmaps = false)
    {
        CheckAccess();
        ValidateTextureData(width, height, rgba8);

        var description = new Texture2DDescription(Format.R8G8B8A8_UNorm, (uint)width, (uint)height,
            1, mipmaps ? 0u : 1u, mipmaps ? BindFlags.ShaderResource | BindFlags.RenderTarget : BindFlags.ShaderResource,
            mipmaps ? ResourceUsage.Default : ResourceUsage.Immutable, CpuAccessFlags.None, 1, 0,
            mipmaps ? ResourceOptionFlags.GenerateMips : ResourceOptionFlags.None);
        ID3D11Texture2D? resource = null;
        ID3D11ShaderResourceView? view = null;
        try
        {
            if (mipmaps)
            {
                resource = _device!.CreateTexture2D(description);
                view = _device.CreateShaderResourceView(resource);
                fixed (byte* pixels = rgba8)
                    _context!.UpdateSubresource(resource, 0, null, (nint)pixels, checked((uint)(width * 4)), 0);
                _context!.GenerateMips(view);
            }
            else
            {
                fixed (byte* pixels = rgba8)
                {
                    var initialData = new SubresourceData((nint)pixels, checked((uint)(width * 4)), 0);
                    resource = _device!.CreateTexture2D(description, initialData);
                }
                view = _device!.CreateShaderResourceView(resource);
            }
            var texture = new D3D11Texture(this, resource, view, width, height, mutable: false);
            _textures.Add(texture);
            return texture;
        }
        catch
        {
            view?.Dispose();
            resource?.Dispose();
            throw;
        }
    }

    /// <summary>Creates a zero-initialized RGBA8 texture for GPU rendering and explicit pixel updates.</summary>
    public D3D11Texture CreateMutableTexture(int width, int height, bool mipmaps = false)
    {
        CheckAccess();
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        _ = checked(checked(width * height) * 4);

        var description = new Texture2DDescription(Format.R8G8B8A8_UNorm, (uint)width, (uint)height,
            1, mipmaps ? 0u : 1u, BindFlags.ShaderResource | BindFlags.RenderTarget, ResourceUsage.Default,
            CpuAccessFlags.None, 1, 0, mipmaps ? ResourceOptionFlags.GenerateMips : ResourceOptionFlags.None);
        ID3D11Texture2D? resource = null;
        ID3D11ShaderResourceView? shaderView = null;
        ID3D11RenderTargetView? renderView = null;
        D3D11Texture? texture = null;
        try
        {
            resource = _device!.CreateTexture2D(description);
            shaderView = _device.CreateShaderResourceView(resource);
            renderView = _device.CreateRenderTargetView(resource);
            texture = new D3D11Texture(this, resource, shaderView, width, height, mutable: true, renderTargetView: renderView);
            _textures.Add(texture);
            ClearTexture(texture, 0, 0, 0, 0);
            return texture;
        }
        catch
        {
            if (texture is not null)
            {
                _textures.Remove(texture);
                texture.DisposeFromRenderer();
            }
            else
            {
                renderView?.Dispose();
                shaderView?.Dispose();
                resource?.Dispose();
            }
            throw;
        }
    }

    /// <summary>Clears a mutable texture on the GPU without reading or uploading its pixels.</summary>
    public void ClearTexture(D3D11Texture texture, float r, float g, float b, float a = 0)
    {
        CheckAccess();
        ArgumentNullException.ThrowIfNull(texture);
        ValidateOwnedMutableTexture(texture, nameof(texture));
        ValidateColor(r, nameof(r));
        ValidateColor(g, nameof(g));
        ValidateColor(b, nameof(b));
        ValidateColor(a, nameof(a));

        ClearPipelineState();
        try
        {
            _context!.ClearRenderTargetView(texture.RenderTargetView!, new Color4(r, g, b, a));
        }
        finally
        {
            BindRenderTargets();
        }
        texture.MarkMipmapsDirty();
    }

    /// <summary>
    /// Copies a rectangle on the GPU. A null endpoint addresses the swap-chain backbuffer;
    /// coordinates are clipped together so source and destination remain aligned.
    /// </summary>
    public void CopyTextureRegion(
        D3D11Texture? source,
        D3D11Texture? destination,
        int sourceX,
        int sourceY,
        int destinationX,
        int destinationY,
        int width,
        int height)
    {
        CheckAccess();
        ValidateTextureEndpoint(source, nameof(source));
        ValidateTextureEndpoint(destination, nameof(destination));
        if (destination is { IsMutable: false })
            throw new ArgumentException("The destination texture must be mutable.", nameof(destination));
        ArgumentOutOfRangeException.ThrowIfNegative(width);
        ArgumentOutOfRangeException.ThrowIfNegative(height);
        if (width == 0 || height == 0) return;

        var sourceWidth = source?.Width ?? _width;
        var sourceHeight = source?.Height ?? _height;
        var destinationWidth = destination?.Width ?? _width;
        var destinationHeight = destination?.Height ?? _height;
        if (!TryClipCopyRegion(sourceX, sourceY, destinationX, destinationY, width, height,
                sourceWidth, sourceHeight, destinationWidth, destinationHeight, out var region))
            return;

        if (source is null)
            ResolveBackBuffer();

        if (_antialiasSampleCount > 1 && destination is null)
        {
            D3D11Texture? ownedSource = null;
            try
            {
                var blitSource = source;
                if (blitSource is null)
                {
                    ownedSource = CreateMutableTexture(_width, _height);
                    using var backBuffer = _swapChain!.GetBuffer<ID3D11Texture2D>(0);
                    _context!.CopyResource(ownedSource.Resource, backBuffer);
                    blitSource = ownedSource;
                }

                Span<D3D11TextureRegion> regions = stackalloc D3D11TextureRegion[1]
                {
                    new(region.SourceX, region.SourceY, region.Width, region.Height,
                        region.DestinationX, region.DestinationY, region.Width, region.Height)
                };
                DrawTextureRegions(blitSource, regions, alphaBlend: false);
            }
            finally
            {
                ownedSource?.Dispose();
            }
            return;
        }

        ID3D11Texture2D? sourceBackBuffer = null;
        ID3D11Texture2D? destinationBackBuffer = null;
        ClearPipelineState();
        try
        {
            var sourceResource = source?.Resource ?? (sourceBackBuffer = _swapChain!.GetBuffer<ID3D11Texture2D>(0));
            var destinationResource = destination?.Resource ?? (destinationBackBuffer = _swapChain!.GetBuffer<ID3D11Texture2D>(0));
            var sourceBox = new Box(region.SourceX, region.SourceY, 0,
                region.SourceX + region.Width, region.SourceY + region.Height, 1);

        if ((source is null && destination is null) || ReferenceEquals(source, destination))
            {
                var temporaryDescription = new Texture2DDescription(Format.R8G8B8A8_UNorm,
                    (uint)region.Width, (uint)region.Height, 1, 1, BindFlags.None,
                    ResourceUsage.Default, CpuAccessFlags.None, 1, 0, ResourceOptionFlags.None);
                using var temporary = _device!.CreateTexture2D(temporaryDescription);
                _context!.CopySubresourceRegion(temporary, 0, 0, 0, 0, sourceResource, 0, sourceBox);
                var temporaryBox = new Box(0, 0, 0, region.Width, region.Height, 1);
                _context.CopySubresourceRegion(destinationResource, 0, (uint)region.DestinationX,
                    (uint)region.DestinationY, 0, temporary, 0, temporaryBox);
            }
            else
            {
                _context!.CopySubresourceRegion(destinationResource, 0, (uint)region.DestinationX,
                    (uint)region.DestinationY, 0, sourceResource, 0, sourceBox);
            }
        }
        finally
        {
            destinationBackBuffer?.Dispose();
            sourceBackBuffer?.Dispose();
            BindRenderTargets();
        }
        destination?.MarkMipmapsDirty();
    }

    /// <summary>Reads a texture or the backbuffer into tightly packed row-major RGBA8 bytes.</summary>
    public byte[] ReadTexturePixels(D3D11Texture? texture = null)
    {
        CheckAccess();
        if (D3D11FrameCounters.Enabled) D3D11FrameCounters.Add(D3D11FrameCounters.Readbacks);
        ValidateTextureEndpoint(texture, nameof(texture));
        var width = texture?.Width ?? _width;
        var height = texture?.Height ?? _height;
        var rowBytes = checked(width * 4);
        var pixels = new byte[checked(rowBytes * height)];
        if (texture is null)
            ResolveBackBuffer();
        ID3D11Texture2D? backBuffer = null;
        ClearPipelineState();
        try
        {
            var source = texture?.Resource ?? (backBuffer = _swapChain!.GetBuffer<ID3D11Texture2D>(0));
            var description = source.Description;
            description.Usage = ResourceUsage.Staging;
            description.BindFlags = BindFlags.None;
            description.CPUAccessFlags = CpuAccessFlags.Read;
            description.MiscFlags = ResourceOptionFlags.None;
            using var staging = _device!.CreateTexture2D(description);
            _context!.CopyResource(staging, source);
            var mapped = _context.Map(staging, 0, MapMode.Read);
            try
            {
                var mappedByteCount = checked((int)(mapped.RowPitch * (uint)height));
                var sourceRowBytes = new ReadOnlySpan<byte>((void*)mapped.DataPointer, mappedByteCount);
                for (var y = 0; y < height; y++)
                    sourceRowBytes.Slice(checked(y * (int)mapped.RowPitch), rowBytes)
                        .CopyTo(pixels.AsSpan(checked(y * rowBytes), rowBytes));
            }
            finally
            {
                _context.Unmap(staging, 0);
            }
            return pixels;
        }
        finally
        {
            backBuffer?.Dispose();
            BindRenderTargets();
        }
    }

    internal byte[] ReadTextureMipPixelsForTesting(D3D11Texture texture, int mipLevel)
    {
        CheckAccess();
        ValidateTextureEndpoint(texture, nameof(texture));
        ArgumentOutOfRangeException.ThrowIfNegative(mipLevel);
        if (mipLevel >= texture.MipLevels) throw new ArgumentOutOfRangeException(nameof(mipLevel));
        EnsureMipmapsGenerated(texture);

        var width = Math.Max(1, texture.Width >> mipLevel);
        var height = Math.Max(1, texture.Height >> mipLevel);
        var pixels = new byte[checked(width * height * 4)];
        ClearPipelineState();
        try
        {
            var description = new Texture2DDescription(Format.R8G8B8A8_UNorm, (uint)width, (uint)height,
                1, 1, BindFlags.None, ResourceUsage.Staging, CpuAccessFlags.Read, 1, 0, ResourceOptionFlags.None);
            using var staging = _device!.CreateTexture2D(description);
            _context!.CopySubresourceRegion(staging, 0, 0, 0, 0, texture.Resource, (uint)mipLevel, null);
            var mapped = _context.Map(staging, 0, MapMode.Read);
            try
            {
                var rowBytes = checked(width * 4);
                var mappedPixels = new ReadOnlySpan<byte>((void*)mapped.DataPointer, checked((int)(mapped.RowPitch * (uint)height)));
                for (var y = 0; y < height; y++)
                    mappedPixels.Slice(checked(y * (int)mapped.RowPitch), rowBytes)
                        .CopyTo(pixels.AsSpan(checked(y * rowBytes), rowBytes));
            }
            finally
            {
                _context.Unmap(staging, 0);
            }
            return pixels;
        }
        finally
        {
            BindRenderTargets();
        }
    }

    /// <summary>Uploads one tightly packed RGBA8 rectangle; null targets the backbuffer.</summary>
    public void UpdateTexturePixels(
        D3D11Texture? texture,
        int x,
        int y,
        int width,
        int height,
        ReadOnlySpan<byte> rgba8)
    {
        CheckAccess();
        ValidateTextureEndpoint(texture, nameof(texture));
        if (texture is { IsMutable: false })
            throw new ArgumentException("Pixel updates require a mutable texture or the backbuffer.", nameof(texture));
        ValidateTextureData(width, height, rgba8);
        ValidateViewport(x, y, width, height, texture?.Width ?? _width, texture?.Height ?? _height);

        if (_antialiasSampleCount > 1 && texture is null)
        {
            using var upload = CreateMutableTexture(width, height);
            UpdateTexturePixels(upload, 0, 0, width, height, rgba8);
            DrawTextureRegion(upload, 0, 0, width, height, x, y, width, height,
                tint: Vector4.One, alphaBlend: false);
            return;
        }

        ID3D11Texture2D? backBuffer = null;
        ClearPipelineState();
        try
        {
            var destination = texture?.Resource ?? (backBuffer = _swapChain!.GetBuffer<ID3D11Texture2D>(0));
            var box = new Box(x, y, 0, checked(x + width), checked(y + height), 1);
            fixed (byte* pixels = rgba8)
                _context!.UpdateSubresource(destination, 0, box, (nint)pixels, checked((uint)(width * 4)), 0);
            texture?.MarkMipmapsDirty();
        }
        finally
        {
            backBuffer?.Dispose();
            BindRenderTargets();
        }
    }

    /// <summary>Draws a persistent mesh without copying its vertex or index data through CPU memory.</summary>
    public void DrawMesh(D3D11Mesh mesh, in Matrix4x4 worldViewProjection, D3D11Texture? texture = null, bool alphaBlend = false)
    {
        var layer = texture is null ? default : new D3D11TextureLayer(texture, Blend: 2);
        DrawMesh(mesh, worldViewProjection, MemoryMarshal.CreateReadOnlySpan(ref layer, 1), alphaBlend, Vector4.One);
    }

    /// <summary>Draws a persistent mesh with up to eight ordered texture stages and a per-draw color multiplier.</summary>
    public void DrawMesh(
        D3D11Mesh mesh,
        in Matrix4x4 worldViewProjection,
        ReadOnlySpan<D3D11TextureLayer> layers,
        bool alphaBlend = false,
        Vector4? materialColor = null,
        D3D11Fog? fog = null,
        Matrix4x4? worldView = null,
        D3D11LightingDrawState? lightingDrawState = null,
        Matrix4x4? world = null,
        uint? indexCount = null)
    {
        if (_queueDraws) CheckAccessKeepDraws();
        else CheckAccess();
        ArgumentNullException.ThrowIfNull(mesh);
        if (!ReferenceEquals(mesh.Owner, this)) throw new ArgumentException("The mesh belongs to a different renderer.", nameof(mesh));
        mesh.ThrowIfDisposed();
        var activeIndexCount = indexCount ?? mesh.IndexCount;
        if (activeIndexCount > mesh.IndexCount) throw new ArgumentOutOfRangeException(nameof(indexCount));
        if (layers.Length > 8) throw new ArgumentOutOfRangeException(nameof(layers), "D3D11 supports at most eight texture stages here.");
        var color = materialColor ?? Vector4.One;
        var drawState = lightingDrawState ?? D3D11LightingDrawState.Default;
        ValidateLightingDrawState(drawState);
        if ((drawState.Effects & 8) != 0) fog = null;
        ValidateColor(color.X, nameof(materialColor));
        ValidateColor(color.Y, nameof(materialColor));
        ValidateColor(color.Z, nameof(materialColor));
        ValidateColor(color.W, nameof(materialColor));
        if (fog is not null)
        {
            if (worldView is null) throw new ArgumentException("Fog requires the mesh world-view transform.", nameof(worldView));
            if (!float.IsFinite(fog.Value.Start) || !float.IsFinite(fog.Value.End))
                throw new ArgumentOutOfRangeException(nameof(fog), "Fog range endpoints must be finite.");
            ValidateColor(fog.Value.Color.X, nameof(fog));
            ValidateColor(fog.Value.Color.Y, nameof(fog));
            ValidateColor(fog.Value.Color.Z, nameof(fog));
        }
        for (var i = 0; i < layers.Length; i++)
        {
            var layer = layers[i];
            if ((uint)layer.CoordinateSet > 1) throw new ArgumentOutOfRangeException(nameof(layers), "Texture coordinate set must be zero or one.");
            if (layer.Blend is not (0 or 1 or 2 or 3 or 5 or 6))
                throw new ArgumentOutOfRangeException(nameof(layers), $"Texture blend mode {layer.Blend} is not supported.");
            if (layer.BumpMatrix is { } bumpMatrix &&
                (!float.IsFinite(bumpMatrix.X) || !float.IsFinite(bumpMatrix.Y) ||
                 !float.IsFinite(bumpMatrix.Z) || !float.IsFinite(bumpMatrix.W)))
                throw new ArgumentOutOfRangeException(nameof(layers), "Bump matrix coefficients must be finite.");
            if (layer.Texture is not null)
            {
                if (!ReferenceEquals(layer.Texture.Owner, this)) throw new ArgumentException("A texture belongs to a different renderer.", nameof(layers));
                layer.Texture.ThrowIfDisposed();
            }
        }

        if (_queueDraws)
        {
            QueueMesh(mesh, activeIndexCount, worldViewProjection, layers, alphaBlend, color, fog,
                worldView ?? Matrix4x4.Identity, drawState, world ?? Matrix4x4.Identity);
            return;
        }

        EnsureMipmapsGenerated(layers);

        UploadTransform(worldViewProjection, worldView ?? Matrix4x4.Identity, world ?? Matrix4x4.Identity);
        UploadMaterial(layers, color, fog: fog, effects: drawState.Effects, shininess: drawState.Shininess);
        BindRenderTargets();
        BindTriangleList();
        BindInputLayout(_inputLayout!);
        BindVertexBuffer(mesh.VertexBuffer, (uint)Marshal.SizeOf<D3D11Vertex>());
        BindIndexBuffer(mesh.IndexBuffer);
        BindVertexShader(_vertexShader!);
        BindVsConstantBuffer(0, _transformBuffer!);
        BindVsConstantBuffer(1, _materialBuffer!);
        BindPixelShader(_texturedPixelShader!);
        SetPixelResources(layers);
        BindPsConstantBuffer(1, _materialBuffer!);
        BindLightingDrawState(drawState, alphaBlend);
        _context!.DrawIndexed(activeIndexCount, 0, 0);
        if (D3D11FrameCounters.Enabled) { D3D11FrameCounters.Add(D3D11FrameCounters.MeshDraws); D3D11FrameCounters.Add(D3D11FrameCounters.ApiDraws); D3D11FrameCounters.Add(D3D11FrameCounters.Triangles, activeIndexCount / 3); D3D11FrameCounters.Add(D3D11FrameCounters.ConstantBufferMaps, 2); }
    }

    /// <summary>
    /// Draws a source rectangle as a screen-space quad. A null destination addresses the
    /// backbuffer. Source bounds are clipped while preserving scale; the rasterizer clips
    /// destination bounds to the selected target. Same-texture draws use a cached GPU copy.
    /// </summary>
    public void DrawTextureRegion(
        D3D11Texture source,
        int sourceX,
        int sourceY,
        int sourceWidth,
        int sourceHeight,
        float destinationX,
        float destinationY,
        float destinationWidth,
        float destinationHeight,
        Vector4? tint = null,
        bool alphaBlend = true,
        D3D11Texture? destination = null)
    {
        Span<D3D11TextureRegion> region = stackalloc D3D11TextureRegion[1]
        {
            new(sourceX, sourceY, sourceWidth, sourceHeight,
                destinationX, destinationY, destinationWidth, destinationHeight)
        };
        DrawTextureRegions(source, region, tint, alphaBlend, destination);
    }

    /// <summary>Draws many rectangles from one texture with one dynamic vertex upload and draw call per 128 visible regions.</summary>
    public void DrawTextureRegions(
        D3D11Texture source,
        ReadOnlySpan<D3D11TextureRegion> regions,
        Vector4? tint = null,
        bool alphaBlend = true,
        D3D11Texture? destination = null)
    {
        CheckAccessNoFlush();
        FlushMeshQueue();
        ArgumentNullException.ThrowIfNull(source);
        ValidateTextureEndpoint(source, nameof(source));
        ValidateTextureEndpoint(destination, nameof(destination));
        if (destination is { IsMutable: false })
            throw new ArgumentException("The destination texture must be mutable.", nameof(destination));
        var color = tint ?? Vector4.One;
        ValidateColor(color.X, nameof(tint));
        ValidateColor(color.Y, nameof(tint));
        ValidateColor(color.Z, nameof(tint));
        ValidateColor(color.W, nameof(tint));

        var targetWidth = destination?.Width ?? _width;
        var targetHeight = destination?.Height ?? _height;
        // Validate the entire batch before changing the render target or issuing any draws.
        foreach (var region in regions)
            _ = TryClipRegion(source, region, targetWidth, targetHeight, out _);
        if (regions.IsEmpty) return;

        if (destination is null)
        {
            // Backbuffer quads with identical state accumulate and draw together (flushed by the next renderer call).
            if (source.MipmapsDirty) FlushBlits();
            EnsureMipmapsGenerated(source);
            var key = new BlitBatchKey(source, color, alphaBlend, targetWidth, targetHeight);
            foreach (var region in regions)
            {
                if (!TryClipRegion(source, region, targetWidth, targetHeight, out var clipped)) continue;
                _blits.Add(key, clipped, _drawBlitBatch);
                if (D3D11FrameCounters.Enabled) D3D11FrameCounters.Add(D3D11FrameCounters.BatchedBlitOps);
            }
            return;
        }

        FlushBlits();
        EnsureMipmapsGenerated(source);
        ID3D11ShaderResourceView sourceView = source.ShaderResourceView;
        ClearPipelineState();
        try
        {
            if (ReferenceEquals(source, destination))
            {
                EnsureBlitScratch(source.Width, source.Height);
                var sourceBox = new Box(0, 0, 0, source.Width, source.Height, 1);
                _context!.CopySubresourceRegion(_blitScratch!, 0, 0, 0, 0, source.Resource, 0, sourceBox);
                sourceView = _blitScratchView!;
            }

            var targetView = destination?.RenderTargetView ?? _renderTarget!;
            BindTargetAndViewport(targetView, null, new Viewport(0, 0, targetWidth, targetHeight, 0, 1));
            DrawBlitQuads(source, sourceView, color, alphaBlend, targetWidth, targetHeight, regions, destination);
        }
        finally
        {
            BindRenderTargets();
        }
    }

    private void DrawBlitBatch(in BlitBatchKey key, ReadOnlySpan<ClippedTextureRegion> quads)
    {
        var source = (D3D11Texture)key.Source;
        // 3D draws leave the rasterizer state they used; blits always draw with the default (post-ClearState) state.
        BindRasterizerState(null);
        BindTargetAndViewport(_renderTarget!, null, new Viewport(0, 0, key.TargetWidth, key.TargetHeight, 0, 1));
        DrawBlitQuads(source, source.ShaderResourceView, key.Tint, key.AlphaBlend, key.TargetWidth, key.TargetHeight, quads, null);
    }

    private void DrawBlitQuads(
        D3D11Texture source, ID3D11ShaderResourceView sourceView, Vector4 color, bool alphaBlend,
        int targetWidth, int targetHeight, ReadOnlySpan<D3D11TextureRegion> regions, D3D11Texture? destination)
    {
        BeginBlitState(source, sourceView, color, alphaBlend);
        const int MaxRegionsPerDraw = 128;
        const int VerticesPerRegion = 6;
        Span<D3D11BlitVertex> vertices = stackalloc D3D11BlitVertex[MaxRegionsPerDraw * VerticesPerRegion];
        var regionIndex = 0;
        while (regionIndex < regions.Length)
        {
            var vertexCount = 0;
            while (regionIndex < regions.Length && vertexCount < vertices.Length)
            {
                var region = regions[regionIndex++];
                if (!TryClipRegion(source, region, targetWidth, targetHeight, out var clipped)) continue;
                AppendBlitQuad(vertices, ref vertexCount, clipped, targetWidth, targetHeight, source.Width, source.Height);
            }
            if (vertexCount == 0) continue;
            EmitBlitDraw(vertices[..vertexCount]);
            destination?.MarkMipmapsDirty();
        }
    }

    private void DrawBlitQuads(
        D3D11Texture source, ID3D11ShaderResourceView sourceView, Vector4 color, bool alphaBlend,
        int targetWidth, int targetHeight, ReadOnlySpan<ClippedTextureRegion> quads, D3D11Texture? destination)
    {
        BeginBlitState(source, sourceView, color, alphaBlend);
        const int MaxRegionsPerDraw = 128;
        Span<D3D11BlitVertex> vertices = stackalloc D3D11BlitVertex[MaxRegionsPerDraw * 6];
        for (var offset = 0; offset < quads.Length; offset += MaxRegionsPerDraw)
        {
            var vertexCount = 0;
            var end = Math.Min(quads.Length, offset + MaxRegionsPerDraw);
            for (var i = offset; i < end; i++)
                AppendBlitQuad(vertices, ref vertexCount, quads[i], targetWidth, targetHeight, source.Width, source.Height);
            EmitBlitDraw(vertices[..vertexCount]);
            destination?.MarkMipmapsDirty();
        }
    }

    private void BeginBlitState(D3D11Texture source, ID3D11ShaderResourceView sourceView, Vector4 color, bool alphaBlend)
    {
        UploadTransform(Matrix4x4.Identity, Matrix4x4.Identity);
        BindTriangleList();
        BindInputLayout(_blitInputLayout!);
        BindVertexBuffer(_blitVertexBuffer!, (uint)Marshal.SizeOf<D3D11BlitVertex>());
        BindVertexShader(_blitVertexShader!);
        BindVsConstantBuffer(0, _transformBuffer!);
        BindVsConstantBuffer(1, _materialBuffer!);
        BindPixelShader(_texturedPixelShader!);
        var layer = new D3D11TextureLayer(source, Blend: 2, UseAlpha: true, ClampU: true, ClampV: true);
        UploadMaterial(MemoryMarshal.CreateReadOnlySpan(ref layer, 1), color, clampRegion: true);
        Array.Clear(_pixelShaderResources);
        Array.Clear(_pixelSamplers);
        _pixelShaderResources[0] = sourceView;
        _pixelSamplers[0] = _textureSamplers[3]!;
        BindPixelResources();
        BindPsConstantBuffer(1, _materialBuffer!);
        BindBlendState(alphaBlend ? _alphaBlendState : null);
    }

    private void EmitBlitDraw(ReadOnlySpan<D3D11BlitVertex> vertices)
    {
        Upload(_blitVertexBuffer!, MemoryMarshal.AsBytes(vertices));
        _context!.Draw((uint)vertices.Length, 0);
        if (D3D11FrameCounters.Enabled) { D3D11FrameCounters.Add(D3D11FrameCounters.BlitDraws); D3D11FrameCounters.Add(D3D11FrameCounters.ConstantBufferMaps, 3); }
    }

    private static bool TryClipRegion(
        D3D11Texture source,
        D3D11TextureRegion region,
        int targetWidth,
        int targetHeight,
        out ClippedTextureRegion clipped)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(region.SourceWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(region.SourceHeight);
        if (!float.IsFinite(region.DestinationX) || !float.IsFinite(region.DestinationY) ||
            !float.IsFinite(region.DestinationWidth) || !float.IsFinite(region.DestinationHeight) ||
            region.DestinationWidth <= 0 || region.DestinationHeight <= 0)
            throw new ArgumentOutOfRangeException(nameof(region), "Destination coordinates and dimensions must be finite, with positive dimensions.");

        var left = Math.Max(0L, -(long)region.SourceX);
        var top = Math.Max(0L, -(long)region.SourceY);
        var right = Math.Min((long)region.SourceWidth, (long)source.Width - region.SourceX);
        var bottom = Math.Min((long)region.SourceHeight, (long)source.Height - region.SourceY);
        if (right <= left || bottom <= top)
        {
            clipped = default;
            return false;
        }

        var clippedWidth = checked((int)(right - left));
        var clippedHeight = checked((int)(bottom - top));
        var x = region.DestinationX + (float)left * region.DestinationWidth / region.SourceWidth;
        var y = region.DestinationY + (float)top * region.DestinationHeight / region.SourceHeight;
        var width = (float)clippedWidth * region.DestinationWidth / region.SourceWidth;
        var height = (float)clippedHeight * region.DestinationHeight / region.SourceHeight;
        if (!float.IsFinite(x + width) || !float.IsFinite(y + height))
            throw new ArgumentOutOfRangeException(nameof(region), "The clipped destination rectangle must remain finite.");
        var leftNdc = x * (2f / targetWidth) - 1f;
        var rightNdc = (x + width) * (2f / targetWidth) - 1f;
        var topNdc = 1f - y * (2f / targetHeight);
        var bottomNdc = 1f - (y + height) * (2f / targetHeight);
        if (!float.IsFinite(leftNdc) || !float.IsFinite(rightNdc) ||
            !float.IsFinite(topNdc) || !float.IsFinite(bottomNdc))
            throw new ArgumentOutOfRangeException(nameof(region), "The destination rectangle is outside the finite clip-space range.");

        clipped = new ClippedTextureRegion(
            checked((int)(region.SourceX + left)), checked((int)(region.SourceY + top)), clippedWidth, clippedHeight,
            x, y, width, height);
        return true;
    }

    private static void AppendBlitQuad(
        Span<D3D11BlitVertex> vertices,
        ref int vertexCount,
        ClippedTextureRegion region,
        int targetWidth,
        int targetHeight,
        int sourceWidth,
        int sourceHeight)
    {
        var leftNdc = region.DestinationX * (2f / targetWidth) - 1f;
        var rightNdc = (region.DestinationX + region.DestinationWidth) * (2f / targetWidth) - 1f;
        var topNdc = 1f - region.DestinationY * (2f / targetHeight);
        var bottomNdc = 1f - (region.DestinationY + region.DestinationHeight) * (2f / targetHeight);
        var uv0 = new Vector2((float)region.SourceX / sourceWidth, (float)region.SourceY / sourceHeight);
        var uv1 = new Vector2((float)(region.SourceX + region.SourceWidth) / sourceWidth,
            (float)(region.SourceY + region.SourceHeight) / sourceHeight);
        var bounds = new Vector4(
            (region.SourceX + 0.5f) / sourceWidth,
            (region.SourceY + 0.5f) / sourceHeight,
            (region.SourceX + region.SourceWidth - 0.5f) / sourceWidth,
            (region.SourceY + region.SourceHeight - 0.5f) / sourceHeight);
        var topLeft = new D3D11BlitVertex(new(leftNdc, topNdc, 0.5f), Vector4.One, uv0, bounds);
        var bottomLeft = new D3D11BlitVertex(new(leftNdc, bottomNdc, 0.5f), Vector4.One,
            new(uv0.X, uv1.Y), bounds);
        var bottomRight = new D3D11BlitVertex(new(rightNdc, bottomNdc, 0.5f), Vector4.One, uv1, bounds);
        var topRight = new D3D11BlitVertex(new(rightNdc, topNdc, 0.5f), Vector4.One,
            new(uv1.X, uv0.Y), bounds);
        vertices[vertexCount++] = topLeft;
        vertices[vertexCount++] = bottomRight;
        vertices[vertexCount++] = bottomLeft;
        vertices[vertexCount++] = topLeft;
        vertices[vertexCount++] = topRight;
        vertices[vertexCount++] = bottomRight;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct D3D11BlitVertex(Vector3 position, Vector4 color, Vector2 texCoord, Vector4 regionUvBounds)
    {
        public readonly Vector3 Position = position;
        public readonly Vector4 Color = color;
        public readonly Vector2 TexCoord = texCoord;
        public readonly Vector4 RegionUvBounds = regionUvBounds;
    }

    /// <summary>Presents the rendered frame; vertical synchronization is enabled by default.</summary>
    public void Present(bool vsync = true)
    {
        CheckAccess();
        var profileStart = D3D11FrameCounters.Enabled ? D3D11FrameCounters.Now() : 0;
        ResolveBackBuffer();
        CountSkips();
        _swapChain!.Present(vsync ? 1u : 0u, PresentFlags.None).CheckError();
        if (D3D11FrameCounters.Enabled) D3D11FrameCounters.AddSince(D3D11FrameCounters.PresentTicks, profileStart);
    }

    /// <summary>Recreates back-buffer and depth resources at the given size after the host window is resized.</summary>
    public void Resize(int width, int height)
    {
        CheckAccess();
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        if (width == _width && height == _height) return;

        ReleaseWindowTargets();
        _swapChain!.ResizeBuffers(0, (uint)width, (uint)height, Format.Unknown, SwapChainFlags.None).CheckError();
        _width = width;
        _height = height;
        CreateWindowResources(width, height);
    }

    /// <summary>Restricts rasterization to a rectangle in client pixel coordinates.</summary>
    public void SetViewport(int x, int y, int width, int height)
    {
        CheckAccess();
        ValidateViewport(x, y, width, height, _width, _height);
        _viewport = new Viewport(x, y, width, height, 0, 1);
        BindRenderTargets();
    }

    internal Rgba32 ReadPixelForTesting(int x, int y)
    {
        CheckAccess();
        ArgumentOutOfRangeException.ThrowIfNegative(x);
        ArgumentOutOfRangeException.ThrowIfNegative(y);
        if (x >= _width || y >= _height) throw new ArgumentOutOfRangeException(nameof(x));

        ResolveBackBuffer();
        using var source = _swapChain!.GetBuffer<ID3D11Texture2D>(0);
        var description = source.Description;
        description.Usage = ResourceUsage.Staging;
        description.BindFlags = BindFlags.None;
        description.CPUAccessFlags = CpuAccessFlags.Read;
        description.MiscFlags = ResourceOptionFlags.None;
        using var staging = _device!.CreateTexture2D(description);
        _context!.CopyResource(staging, source);
        var mapped = _context.Map(staging, 0, MapMode.Read);
        try
        {
            var pixel = (byte*)mapped.DataPointer + checked(y * (int)mapped.RowPitch) + checked(x * 4);
            return new(pixel[0], pixel[1], pixel[2], pixel[3]);
        }
        finally
        {
            _context.Unmap(staging, 0);
        }
    }

    internal static void ValidateTriangles(ReadOnlySpan<D3D11Vertex> vertices, ReadOnlySpan<uint> indices)
    {
        if (vertices.IsEmpty) throw new ArgumentException("Triangle vertices cannot be empty.", nameof(vertices));
        if (indices.IsEmpty || indices.Length % 3 != 0)
            throw new ArgumentException("Triangle indices must contain one or more complete triangles.", nameof(indices));
        for (var i = 0; i < indices.Length; i++)
            if (indices[i] >= vertices.Length) throw new ArgumentOutOfRangeException(nameof(indices), $"Index {indices[i]} exceeds the vertex count {vertices.Length}.");
    }

    internal static void ValidateTextureData(int width, int height, ReadOnlySpan<byte> rgba8)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        var requiredBytes = checked(checked(width * height) * 4);
        if (rgba8.Length != requiredBytes)
            throw new ArgumentException($"An RGBA8 texture requires exactly {requiredBytes} bytes.", nameof(rgba8));
    }

    private static bool TryClipCopyRegion(
        int sourceX,
        int sourceY,
        int destinationX,
        int destinationY,
        int width,
        int height,
        int sourceWidth,
        int sourceHeight,
        int destinationWidth,
        int destinationHeight,
        out ClippedCopyRegion region)
    {
        var left = Math.Max(0L, Math.Max(-(long)sourceX, -(long)destinationX));
        var right = Math.Min((long)width,
            Math.Min((long)sourceWidth - sourceX, (long)destinationWidth - destinationX));
        var top = Math.Max(0L, Math.Max(-(long)sourceY, -(long)destinationY));
        var bottom = Math.Min((long)height,
            Math.Min((long)sourceHeight - sourceY, (long)destinationHeight - destinationY));
        if (right <= left || bottom <= top)
        {
            region = default;
            return false;
        }

        region = new(
            checked((int)(sourceX + left)),
            checked((int)(sourceY + top)),
            checked((int)(destinationX + left)),
            checked((int)(destinationY + top)),
            checked((int)(right - left)),
            checked((int)(bottom - top)));
        return true;
    }

    private void ValidateTextureEndpoint(D3D11Texture? texture, string parameter)
    {
        if (texture is null) return;
        if (!ReferenceEquals(texture.Owner, this))
            throw new ArgumentException("The texture belongs to a different renderer.", parameter);
        texture.ThrowIfDisposed();
    }

    private void ValidateOwnedMutableTexture(D3D11Texture texture, string parameter)
    {
        ValidateTextureEndpoint(texture, parameter);
        if (!texture.IsMutable)
            throw new ArgumentException("The operation requires a mutable texture.", parameter);
    }

    private readonly record struct ClippedCopyRegion(
        int SourceX,
        int SourceY,
        int DestinationX,
        int DestinationY,
        int Width,
        int Height);

    internal static void ValidateViewport(int x, int y, int width, int height, int targetWidth, int targetHeight)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(x);
        ArgumentOutOfRangeException.ThrowIfNegative(y);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        if ((long)x + width > targetWidth || (long)y + height > targetHeight)
            throw new ArgumentOutOfRangeException(nameof(width), "The viewport must fit inside the renderer's client area.");
    }

    private void CreateDevice(nint hwnd)
    {
        var description = new SwapChainDescription
        {
            BufferCount = 2,
            BufferDescription = new ModeDescription((uint)_width, (uint)_height, new Rational(0, 1), BackBufferFormat),
            BufferUsage = Usage.RenderTargetOutput,
            OutputWindow = hwnd,
            SampleDescription = new SampleDescription(1, 0),
            Windowed = true,
            SwapEffect = SwapEffect.Discard,
            Flags = SwapChainFlags.None
        };
        var levels = new[] { FeatureLevel.Level_11_0, FeatureLevel.Level_10_1, FeatureLevel.Level_10_0 };
        D3D11CreateDeviceAndSwapChain(null, DriverType.Hardware, DeviceCreationFlags.BgraSupport,
            levels, description, out _swapChain, out _device, out _, out _context).CheckError();
    }

    private void CreateWindowResources(int width, int height)
    {
        _viewport = new Viewport(0, 0, width, height, 0, 1);
        using var backBuffer = _swapChain!.GetBuffer<ID3D11Texture2D>(0);
        if (_antialiasSampleCount > 1)
        {
            _antialiasColorTexture = _device!.CreateTexture2D(new Texture2DDescription
            {
                Width = (uint)width,
                Height = (uint)height,
                MipLevels = 1,
                ArraySize = 1,
                Format = BackBufferFormat,
                SampleDescription = new SampleDescription((uint)_antialiasSampleCount, (uint)_antialiasQuality),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.RenderTarget,
                CPUAccessFlags = CpuAccessFlags.None,
                MiscFlags = ResourceOptionFlags.None
            });
            _renderTarget = _device.CreateRenderTargetView(_antialiasColorTexture);
            _depthTexture = _device.CreateTexture2D(new Texture2DDescription
            {
                Width = (uint)width,
                Height = (uint)height,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.D32_Float,
                SampleDescription = new SampleDescription((uint)_antialiasSampleCount, (uint)_antialiasQuality),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.DepthStencil,
                CPUAccessFlags = CpuAccessFlags.None,
                MiscFlags = ResourceOptionFlags.None
            });
        }
        else
        {
            _renderTarget = _device!.CreateRenderTargetView(backBuffer);
            _depthTexture = _device.CreateTexture2D(Format.D32_Float, (uint)width, (uint)height,
                mipLevels: 1, arraySize: 1, bindFlags: BindFlags.DepthStencil);
        }
        _depthView = _device.CreateDepthStencilView(_depthTexture);
        BindRenderTargets();
    }

    private void CreateTrianglePipeline()
    {
        var vertexBytecode = Compiler.Compile(TriangleShader, "VSMain", "triangle.hlsl", "vs_4_0", ShaderFlags.EnableStrictness);
        var blitVertexBytecode = Compiler.Compile(TriangleShader, "VSBlit", "triangle.hlsl", "vs_4_0", ShaderFlags.EnableStrictness);
        var colorPixelBytecode = Compiler.Compile(TriangleShader, "PSColor", "triangle.hlsl", "ps_4_0", ShaderFlags.EnableStrictness);
        var texturedPixelBytecode = Compiler.Compile(TriangleShader, "PSTextured", "triangle.hlsl", "ps_4_0", ShaderFlags.EnableStrictness);
        _vertexShader = _device!.CreateVertexShader(vertexBytecode.Span);
        _blitVertexShader = _device.CreateVertexShader(blitVertexBytecode.Span);
        _colorPixelShader = _device.CreatePixelShader(colorPixelBytecode.Span);
        _texturedPixelShader = _device.CreatePixelShader(texturedPixelBytecode.Span);
        InputElementDescription[] elements =
        [
            new("POSITION", 0, Format.R32G32B32_Float, 0, 0),
            new("COLOR", 0, Format.R32G32B32A32_Float, 12, 0),
            new("NORMAL", 0, Format.R32G32B32_Float, 28, 0),
            new("TEXCOORD", 0, Format.R32G32_Float, 40, 0),
            new("TEXCOORD", 2, Format.R32G32_Float, 48, 0)
        ];
        _inputLayout = _device.CreateInputLayout(elements, vertexBytecode.Span);
        CreateLightingStates();
        InputElementDescription[] blitElements =
        [
            new("POSITION", 0, Format.R32G32B32_Float, 0, 0),
            new("COLOR", 0, Format.R32G32B32A32_Float, 12, 0),
            new("TEXCOORD", 0, Format.R32G32_Float, 28, 0),
            new("TEXCOORD", 1, Format.R32G32B32A32_Float, 36, 0)
        ];
        _blitInputLayout = _device.CreateInputLayout(blitElements, blitVertexBytecode.Span);
        CreateTextureSamplers();
        _alphaBlendState = _device.CreateBlendState(BlendDescription.NonPremultiplied);
        _transformBuffer = _device.CreateBuffer((uint)sizeof(TransformConstants), BindFlags.ConstantBuffer,
            ResourceUsage.Dynamic, CpuAccessFlags.Write);
        _materialBuffer = _device.CreateBuffer((uint)sizeof(MaterialConstants), BindFlags.ConstantBuffer,
            ResourceUsage.Dynamic, CpuAccessFlags.Write);
        _lightingBuffer = _device.CreateBuffer((uint)sizeof(LightingConstants), BindFlags.ConstantBuffer,
            ResourceUsage.Dynamic, CpuAccessFlags.Write);
        UploadLighting(Vector3.One, Vector3.Zero, ReadOnlySpan<D3D11Light>.Empty);
        const uint blitVertexCapacity = 128 * 6;
        _blitVertexBuffer = _device.CreateBuffer(checked(blitVertexCapacity * (uint)sizeof(D3D11BlitVertex)), BindFlags.VertexBuffer,
            ResourceUsage.Dynamic, CpuAccessFlags.Write);
    }

    private void EnsureBlitScratch(int width, int height)
    {
        if (_blitScratch is not null && _blitScratchView is not null &&
            _blitScratchWidth == width && _blitScratchHeight == height) return;
        _blitScratchView?.Dispose();
        _blitScratch?.Dispose();
        _blitScratchView = null;
        _blitScratch = null;
        var description = new Texture2DDescription(Format.R8G8B8A8_UNorm,
            (uint)width, (uint)height, 1, 1, BindFlags.ShaderResource,
            ResourceUsage.Default, CpuAccessFlags.None, 1, 0, ResourceOptionFlags.None);
        ID3D11Texture2D? scratch = null;
        ID3D11ShaderResourceView? view = null;
        try
        {
            scratch = _device!.CreateTexture2D(description);
            view = _device.CreateShaderResourceView(scratch);
            _blitScratch = scratch;
            _blitScratchView = view;
            _blitScratchWidth = width;
            _blitScratchHeight = height;
        }
        catch
        {
            view?.Dispose();
            scratch?.Dispose();
            throw;
        }
    }

    private void EnsureBufferCapacity(ref ID3D11Buffer? buffer, ref uint capacity, uint requested, BindFlags bindFlags, uint stride)
    {
        if (buffer is not null && capacity >= requested) return;
        buffer?.Dispose();
        capacity = Math.Max(3, capacity);
        while (capacity < requested) capacity = checked(capacity * 2);
        buffer = _device!.CreateBuffer(checked(capacity * stride), bindFlags, ResourceUsage.Dynamic, CpuAccessFlags.Write);
    }

    private void Upload(ID3D11Buffer buffer, ReadOnlySpan<byte> bytes)
    {
        var mapped = _context!.Map(buffer, 0, MapMode.WriteDiscard);
        try { bytes.CopyTo(new Span<byte>((void*)mapped.DataPointer, bytes.Length)); }
        finally { _context.Unmap(buffer, 0); }
    }

    private readonly NormalMatrixCache _normalCache = new();

    private void UploadTransform(in Matrix4x4 worldViewProjection, in Matrix4x4 worldView) =>
        UploadTransform(worldViewProjection, worldView, Matrix4x4.Identity);

    private Matrix4x4 NormalMatrixFor(in Matrix4x4 world)
    {
        Matrix4x4 normal;
        if (_hasLastNormal && world == _lastWorld) normal = _lastNormal;
        else
        {
            normal = _normalCache.Get(world);
            _lastWorld = world; _lastNormal = normal; _hasLastNormal = true;
        }
        return normal;
    }

    /// <summary>The cbuffer Transform record exactly as both submission paths upload it (HLSL column-major, so transposed).</summary>
    internal static void BuildTransformConstants(out TransformConstants constants, in Matrix4x4 worldViewProjection,
        in Matrix4x4 worldView, in Matrix4x4 world, in Matrix4x4 normal)
    {
        constants.WorldViewProjection = Matrix4x4.Transpose(worldViewProjection);
        constants.WorldView = Matrix4x4.Transpose(worldView);
        constants.World = Matrix4x4.Transpose(world);
        constants.NormalMatrix = Matrix4x4.Transpose(normal);
    }

    private void UploadTransform(in Matrix4x4 worldViewProjection, in Matrix4x4 worldView, in Matrix4x4 world)
    {
        var normal = NormalMatrixFor(world);
        BuildTransformConstants(out var transformed, worldViewProjection, worldView, world, normal);
        if (_hasLastTransform && SameBytes(transformed, _lastTransform))
        {
            if (D3D11FrameCounters.Enabled) D3D11FrameCounters.Add(D3D11FrameCounters.SkippedConstantBufferMaps);
            return;
        }
        var mapped = _context!.Map(_transformBuffer!, 0, MapMode.WriteDiscard);
        try { *(TransformConstants*)mapped.DataPointer = transformed; }
        finally { _context.Unmap(_transformBuffer!, 0); }
        _lastTransform = transformed;
        _hasLastTransform = true;
    }

    private void BindRenderTargets()
    {
        BindTargetAndViewport(_renderTarget!, _depthView!, _viewport);
    }

    private void UploadMaterial(ReadOnlySpan<D3D11TextureLayer> layers, Vector4 color, bool clampRegion = false, D3D11Fog? fog = null,
        int effects = 3, float shininess = 0)
    {
        var constants = new MaterialConstants();
        BuildMaterialConstants(ref constants, layers, color, clampRegion, fog, effects, shininess);
        if (_hasLastMaterial && SameBytes(constants, _lastMaterial))
        {
            if (D3D11FrameCounters.Enabled) D3D11FrameCounters.Add(D3D11FrameCounters.SkippedConstantBufferMaps);
            return;
        }
        var mapped = _context!.Map(_materialBuffer!, 0, MapMode.WriteDiscard);
        try { *(MaterialConstants*)mapped.DataPointer = constants; }
        finally { _context.Unmap(_materialBuffer!, 0); }
        _lastMaterial = constants;
        _hasLastMaterial = true;
    }

    /// <summary>
    /// The cbuffer Material record exactly as both submission paths upload it. <paramref name="constants"/> must be
    /// zeroed (default) on entry: unused stages stay zero.
    /// </summary>
    internal static void BuildMaterialConstants(ref MaterialConstants constants, ReadOnlySpan<D3D11TextureLayer> layers,
        Vector4 color, bool clampRegion, D3D11Fog? fog, int effects, float shininess)
    {
        constants.Color = color;
        constants.RegionFlags = new Vector4(clampRegion ? 1 : 0, effects, 0, 0);
        constants.FogColor = fog is { } activeFog ? new Vector4(activeFog.Color, 1) : Vector4.Zero;
        constants.FogParameters = fog is { } parameters ? new Vector4(parameters.Start, parameters.End, 1, 0) : Vector4.Zero;
        constants.Shininess = new Vector4(Math.Clamp(shininess, 0, 1), Math.Clamp(shininess, 0, 1) * 128, 0, 0);
        if (layers.Length > 0 && layers[0].Texture is not null && layers[0].Masked)
        {
            constants.RegionFlags.Z = 1;
            constants.RegionFlags.W = (effects & 32) != 0 ? 0 : MathF.Truncate(128 * color.W) / 255f;
        }
        // Blend0/Blend1 and Alpha0/Alpha1 are adjacent float4s: stage i is element i of each 8-float run. Disabled
        // stages keep the zero they were initialized with (the original code wrote 0 explicitly).
        var blends = MemoryMarshal.CreateSpan(ref constants.Blend0.X, 8);
        var alphas = MemoryMarshal.CreateSpan(ref constants.Alpha0.X, 8);
        for (var i = 0; i < layers.Length; i++)
        {
            ref readonly var layer = ref layers[i];
            if (layer.UvTransform is { } uv)
            {
                constants.UvRowsX[i] = new Vector4(uv.M11, uv.M21, uv.M31, layer.CoordinateSet);
                constants.UvRowsY[i] = new Vector4(uv.M12, uv.M22, uv.M32, 0);
            }
            else
            {
                // Matrix3x2.Identity: M11 = M22 = 1, all other elements 0.
                constants.UvRowsX[i] = new Vector4(1, 0, 0, layer.CoordinateSet);
                constants.UvRowsY[i] = new Vector4(0, 1, 0, 0);
            }
            if (layer.BumpMatrix is { } bump) constants.BumpMatrices[i] = bump;
            if (layer.Texture is not null && layer.Blend != 0)
            {
                blends[i] = layer.Blend;
                if (layer.UseAlpha) alphas[i] = 1;
            }
        }
    }

    private void SetPixelResources(ReadOnlySpan<D3D11TextureLayer> layers)
    {
        Array.Clear(_pixelShaderResources);
        Array.Clear(_pixelSamplers);
        for (var i = 0; i < layers.Length; i++)
        {
            var layer = layers[i];
            if (layer.Texture is null || layer.Blend == 0) continue;
            _pixelShaderResources[i] = layer.Texture.ShaderResourceView;
            var samplerIndex = (layer.ClampU ? 1 : 0) | (layer.ClampV ? 2 : 0);
            _pixelSamplers[i] = _textureSamplers[samplerIndex]!;
        }
        BindPixelResources();
    }

    private void EnsureMipmapsGenerated(ReadOnlySpan<D3D11TextureLayer> layers)
    {
        foreach (var layer in layers)
            if (layer.Texture is { MipmapsDirty: true } texture)
                EnsureMipmapsGenerated(texture);
    }

    private void EnsureMipmapsGenerated(D3D11Texture texture)
    {
        if (!texture.MipmapsDirty) return;
        ClearPipelineState();
        var profileStart = D3D11FrameCounters.Enabled ? D3D11FrameCounters.Now() : 0;
        _context!.GenerateMips(texture.ShaderResourceView);
        texture.MarkMipmapsClean();
        if (D3D11FrameCounters.Enabled) { D3D11FrameCounters.Add(D3D11FrameCounters.MipGenerations); D3D11FrameCounters.AddSince(D3D11FrameCounters.MipTicks, profileStart); }
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct TransformConstants
    {
        public Matrix4x4 WorldViewProjection;
        public Matrix4x4 WorldView;
        public Matrix4x4 World;
        public Matrix4x4 NormalMatrix;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MaterialConstants
    {
        public Vector4 Color;
        public Vector4 Blend0;
        public Vector4 Blend1;
        public Vector4 Alpha0;
        public Vector4 Alpha1;
        public Vector4 RegionFlags;
        public StageVectors UvRowsX;
        public StageVectors UvRowsY;
        public StageVectors BumpMatrices;
        public Vector4 FogColor;
        public Vector4 FogParameters;
        public Vector4 Shininess;
    }

    [InlineArray(8)]
    internal struct StageVectors { private Vector4 _element0; }

    private void ClearPipelineState()
    {
        if (D3D11FrameCounters.Enabled) D3D11FrameCounters.Add(D3D11FrameCounters.ClearPipelineStates);
        _context?.ClearState();
        Array.Clear(_pixelShaderResources);
        Array.Clear(_pixelSamplers);
        ResetShadowState();
    }

    private static void ValidateColor(float value, string parameter)
    {
        if (!float.IsFinite(value) || value is < 0f or > 1f) throw new ArgumentOutOfRangeException(parameter, "Color components must be finite values in [0, 1].");
    }

    private void CheckAccess()
    {
        CheckAccessNoFlush();
        FlushBlits();
        FlushMeshQueue();
    }

    /// <summary>Thread/disposal checks and pending blits, but queued mesh draws stay queued (they only reference
    /// resources this call does not modify).</summary>
    private void CheckAccessKeepDraws()
    {
        CheckAccessNoFlush();
        FlushBlits();
    }

    private void CheckAccessNoFlush()
    {
        if (Environment.CurrentManagedThreadId != _threadId)
            throw new InvalidOperationException("D3D11 renderer access must stay on its creating thread.");
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    internal void DisposeMesh(D3D11Mesh mesh)
    {
        if (Environment.CurrentManagedThreadId != _threadId)
            throw new InvalidOperationException("D3D11 mesh resources must be disposed on the renderer's creating thread.");
        if (!ReferenceEquals(mesh.Owner, this)) throw new ArgumentException("The mesh belongs to a different renderer.", nameof(mesh));
        if (_meshes.Remove(mesh))
        {
            FlushBlits();
            FlushDrawsReferencing(mesh);
            ClearPipelineState();
            mesh.DisposeFromRenderer();
        }
    }

    internal void DisposeTexture(D3D11Texture texture)
    {
        if (Environment.CurrentManagedThreadId != _threadId)
            throw new InvalidOperationException("D3D11 texture resources must be disposed on the renderer's creating thread.");
        if (!ReferenceEquals(texture.Owner, this)) throw new ArgumentException("The texture belongs to a different renderer.", nameof(texture));
        if (_textures.Remove(texture))
        {
            FlushBlits();
            FlushMeshQueue();
            ClearPipelineState();
            texture.DisposeFromRenderer();
        }
    }

    private float _textureLodBias;

    /// <summary>Legacy global mip LOD bias applied to every texture stage sampler (D3D11 range -16 to 15.99).</summary>
    public float TextureLodBias
    {
        get => _textureLodBias;
        set
        {
            CheckAccess();
            if (!float.IsFinite(value) || value < -16f || value >= 16f)
                throw new ArgumentOutOfRangeException(nameof(value), "Texture LOD bias must be finite and within [-16, 16).");
            if (value == _textureLodBias) return;
            _textureLodBias = value;
            ClearPipelineState();
            DisposeTextureSamplers();
            CreateTextureSamplers();
        }
    }

    private void CreateTextureSamplers()
    {
        for (var samplerIndex = 0; samplerIndex < _textureSamplers.Length; samplerIndex++)
        {
            var clampU = (samplerIndex & 1) != 0;
            var clampV = (samplerIndex & 2) != 0;
            _textureSamplers[samplerIndex] = _device!.CreateSamplerState(new SamplerDescription(Filter.MinMagMipLinear,
                clampU ? TextureAddressMode.Clamp : TextureAddressMode.Wrap,
                clampV ? TextureAddressMode.Clamp : TextureAddressMode.Wrap,
                TextureAddressMode.Clamp, _textureLodBias, 1, ComparisonFunction.Never,
                new Color4(0, 0, 0, 0), 0, float.MaxValue));
        }
    }

    private void DisposeTextureSamplers()
    {
        for (var i = 0; i < _textureSamplers.Length; i++)
        {
            _textureSamplers[i]?.Dispose();
            _textureSamplers[i] = null;
        }
    }

    private void ReleaseGraphicsResources()
    {
        ClearPipelineState();
        foreach (var mesh in _meshes) mesh.DisposeFromRenderer();
        _meshes.Clear();
        foreach (var texture in _textures) texture.DisposeFromRenderer();
        _textures.Clear();
        _alphaBlendState?.Dispose(); _alphaBlendState = null;
        DisposeTextureSamplers();
        _inputLayout?.Dispose(); _inputLayout = null;
        _transformBuffer?.Dispose(); _transformBuffer = null;
        ReleaseObjectConstantPipeline();
        _materialBuffer?.Dispose(); _materialBuffer = null;
        _lightingBuffer?.Dispose(); _lightingBuffer = null;
        _multiplyBlendState?.Dispose(); _multiplyBlendState = null;
        _additiveBlendState?.Dispose(); _additiveBlendState = null;
        _noCullRasterizerState?.Dispose(); _noCullRasterizerState = null;
        _wireframeRasterizerState?.Dispose(); _wireframeRasterizerState = null;
        _wireframeNoCullRasterizerState?.Dispose(); _wireframeNoCullRasterizerState = null;
        _readOnlyDepthState?.Dispose(); _readOnlyDepthState = null;
        _disabledDepthState?.Dispose(); _disabledDepthState = null;
        _blitVertexBuffer?.Dispose(); _blitVertexBuffer = null;
        _blitScratchView?.Dispose(); _blitScratchView = null;
        _blitScratch?.Dispose(); _blitScratch = null;
        _indexBuffer?.Dispose(); _indexBuffer = null;
        _vertexBuffer?.Dispose(); _vertexBuffer = null;
        _texturedPixelShader?.Dispose(); _texturedPixelShader = null;
        _colorPixelShader?.Dispose(); _colorPixelShader = null;
        _vertexShader?.Dispose(); _vertexShader = null;
        _renderTarget?.Dispose(); _renderTarget = null;
        _depthView?.Dispose(); _depthView = null;
        _depthTexture?.Dispose(); _depthTexture = null;
        _antialiasColorTexture?.Dispose(); _antialiasColorTexture = null;
        _swapChain?.Dispose(); _swapChain = null;
        _context?.Dispose(); _context = null;
        _device?.Dispose(); _device = null;
    }

    public void Dispose()
    {
        if (Environment.CurrentManagedThreadId != _threadId)
            throw new InvalidOperationException("D3D11 renderer must be disposed on its creating thread.");
        if (_disposed) return;
        _disposed = true;
        _blits.Discard();
        DiscardMeshQueue();
        ReleaseGraphicsResources();
        if (_window is not null)
        {
            if (_ownsWindow)
                LegacyInput.CloseWindow();
            else
                _window.Hide();
            _window = null;
            _ownsWindow = false;
        }
    }
}
