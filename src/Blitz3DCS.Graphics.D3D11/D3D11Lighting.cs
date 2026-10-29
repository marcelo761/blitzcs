using System.Numerics;
using System.Runtime.InteropServices;
using Vortice.Direct3D11;
using Vortice.Mathematics;

namespace Blitz3DCS.Graphics.D3D11;

/// <summary>A source-compatible directional, point, or spot light for one rendered scene.</summary>
public readonly record struct D3D11Light(
    int Type,
    Vector3 Position,
    Vector3 Direction,
    Vector3 Color,
    float Range,
    float LinearAttenuation,
    float InnerConeRadians,
    float OuterConeRadians);

/// <summary>Per-mesh legacy lighting and rasterization settings.</summary>
public readonly record struct D3D11LightingDrawState(int Effects, float Shininess, int Blend = 0, int DepthMode = 0)
{
    /// <summary>Preserves the renderer's historical vertex-colored, unlit default.</summary>
    public static D3D11LightingDrawState Default => new(Effects: 35, Shininess: 0);
}

public sealed unsafe partial class D3D11Renderer
{
    private const int MaximumLightingLights = 8;
    private const int SupportedEffects = 0x403f;
    private ID3D11BlendState? _multiplyBlendState;
    private ID3D11BlendState? _additiveBlendState;
    private ID3D11RasterizerState? _noCullRasterizerState;
    private ID3D11DepthStencilState? _readOnlyDepthState;
    private ID3D11DepthStencilState? _disabledDepthState;

    /// <summary>Uploads one ambient value and up to eight ordered lights for subsequent mesh draws.</summary>
    public void SetSceneLighting(Vector3 ambient, Vector3 eye, ReadOnlySpan<D3D11Light> lights)
    {
        CheckAccess();
        ValidateUnitColor(ambient, nameof(ambient));
        ValidateFiniteVector(eye, nameof(eye));
        if (lights.Length > MaximumLightingLights)
            throw new ArgumentOutOfRangeException(nameof(lights), "At most eight ordered lights can be active per scene.");
        for (var i = 0; i < lights.Length; i++) ValidateLight(lights[i], nameof(lights));
        UploadLighting(ambient, eye, lights);
    }

    private void UploadLighting(Vector3 ambient, Vector3 eye, ReadOnlySpan<D3D11Light> lights)
    {
        var constants = new LightingConstants
        {
            Ambient = new Vector4(ambient, 1),
            Eye = new Vector4(eye, 1),
            Metadata = new Vector4(lights.Length, 0, 0, 0),
        };
        for (var i = 0; i < lights.Length; i++)
        {
            var light = lights[i];
            constants[i] = new LightConstants
            {
                DirectionType = new Vector4(light.Direction, light.Type),
                PositionRange = new Vector4(light.Position, light.Range),
                ColorAttenuation = new Vector4(light.Color, light.LinearAttenuation),
                Cone = new Vector4(light.InnerConeRadians, light.OuterConeRadians, 1, 0),
            };
        }
        var mapped = _context!.Map(_lightingBuffer!, 0, MapMode.WriteDiscard);
        try { *(LightingConstants*)mapped.DataPointer = constants; }
        finally { _context.Unmap(_lightingBuffer!, 0); }
    }

    private static void ValidateLight(D3D11Light light, string parameter)
    {
        if (light.Type is < 1 or > 3) throw new ArgumentOutOfRangeException(parameter, "Light type must be directional (1), point (2), or spot (3).");
        ValidateFiniteVector(light.Position, parameter);
        ValidateFiniteVector(light.Direction, parameter);
        ValidateUnitColor(light.Color, parameter);
        if (!float.IsFinite(light.Range) || light.Range <= 0 || !float.IsFinite(light.LinearAttenuation) || light.LinearAttenuation <= 0)
            throw new ArgumentOutOfRangeException(parameter, "Light range and linear attenuation must be finite and positive.");
        if (!float.IsFinite(light.InnerConeRadians) || !float.IsFinite(light.OuterConeRadians) ||
            light.InnerConeRadians < 0 || light.OuterConeRadians < light.InnerConeRadians || light.OuterConeRadians > MathF.PI)
            throw new ArgumentOutOfRangeException(parameter, "Spotlight angles must be finite radians with 0 <= inner <= outer <= pi.");
    }

    private static void ValidateFiniteVector(Vector3 value, string parameter)
    {
        if (!float.IsFinite(value.X) || !float.IsFinite(value.Y) || !float.IsFinite(value.Z))
            throw new ArgumentOutOfRangeException(parameter, "Vector components must be finite.");
    }

    private static void ValidateUnitColor(Vector3 value, string parameter)
    {
        if (!float.IsFinite(value.X) || value.X is < 0 or > 1 ||
            !float.IsFinite(value.Y) || value.Y is < 0 or > 1 ||
            !float.IsFinite(value.Z) || value.Z is < 0 or > 1)
            throw new ArgumentOutOfRangeException(parameter, "Color components must be finite values in [0, 1].");
    }

    private void CreateLightingStates()
    {
        var multiply = new BlendDescription(Blend.DestinationColor, Blend.Zero, Blend.One, Blend.Zero)
        {
            AlphaToCoverageEnable = false,
            IndependentBlendEnable = false,
        };
        var add = new BlendDescription(Blend.SourceAlpha, Blend.One, Blend.SourceAlpha, Blend.One)
        {
            AlphaToCoverageEnable = false,
            IndependentBlendEnable = false,
        };
        _multiplyBlendState = _device!.CreateBlendState(multiply);
        _additiveBlendState = _device.CreateBlendState(add);
        _noCullRasterizerState = _device!.CreateRasterizerState(RasterizerDescription.CullNone);
        _readOnlyDepthState = _device.CreateDepthStencilState(new DepthStencilDescription
        {
            DepthEnable = true, DepthWriteMask = DepthWriteMask.Zero, DepthFunc = ComparisonFunction.LessEqual,
        });
        _disabledDepthState = _device.CreateDepthStencilState(new DepthStencilDescription
        {
            DepthEnable = false, DepthWriteMask = DepthWriteMask.Zero, DepthFunc = ComparisonFunction.Always,
        });
    }

    private void BindLightingDrawState(D3D11LightingDrawState state, bool alphaBlend)
    {
        _context!.VSSetConstantBuffer(2, _lightingBuffer!);
        _context.PSSetConstantBuffer(2, _lightingBuffer!);
        if ((state.Effects & 16) != 0) _context.RSSetState(_noCullRasterizerState);
        else _context.RSSetState(null);
        var blend = state.Blend switch
        {
            2 => _multiplyBlendState,
            3 => _additiveBlendState,
            1 => _alphaBlendState,
            _ => alphaBlend ? _alphaBlendState : null,
        };
        _context.OMSetBlendState(blend, null, uint.MaxValue);
        _context.OMSetDepthStencilState(state.DepthMode switch
        {
            1 => _readOnlyDepthState,
            2 => _disabledDepthState,
            _ => null,
        });
    }

    private static void ValidateLightingDrawState(D3D11LightingDrawState state)
    {
        if ((state.Effects & ~SupportedEffects) != 0)
            throw new ArgumentOutOfRangeException(nameof(state), $"Entity FX flags 0x{state.Effects:x} include unsupported bits.");
        if (!float.IsFinite(state.Shininess))
            throw new ArgumentOutOfRangeException(nameof(state), "Shininess must be finite.");
        if (state.Blend is < 0 or > 3)
            throw new ArgumentOutOfRangeException(nameof(state), "Only replace, alpha, multiply, and additive entity blends are supported.");
        if (state.DepthMode is < 0 or > 2)
            throw new ArgumentOutOfRangeException(nameof(state), "Only normal, read-only and disabled depth modes are supported.");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LightingConstants
    {
        public Vector4 Ambient;
        public Vector4 Eye;
        public Vector4 Metadata;
        public LightConstants Lights0;
        public LightConstants Lights1;
        public LightConstants Lights2;
        public LightConstants Lights3;
        public LightConstants Lights4;
        public LightConstants Lights5;
        public LightConstants Lights6;
        public LightConstants Lights7;

        public LightConstants this[int index]
        {
            readonly get => index switch
            {
                0 => Lights0, 1 => Lights1, 2 => Lights2, 3 => Lights3,
                4 => Lights4, 5 => Lights5, 6 => Lights6, 7 => Lights7,
                _ => throw new ArgumentOutOfRangeException(nameof(index)),
            };
            set
            {
                switch (index)
                {
                    case 0: Lights0 = value; break; case 1: Lights1 = value; break;
                    case 2: Lights2 = value; break; case 3: Lights3 = value; break;
                    case 4: Lights4 = value; break; case 5: Lights5 = value; break;
                    case 6: Lights6 = value; break; case 7: Lights7 = value; break;
                    default: throw new ArgumentOutOfRangeException(nameof(index));
                }
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LightConstants
    {
        public Vector4 DirectionType;
        public Vector4 PositionRange;
        public Vector4 ColorAttenuation;
        public Vector4 Cone;
    }
}
