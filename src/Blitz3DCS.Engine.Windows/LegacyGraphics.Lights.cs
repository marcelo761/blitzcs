using System.Numerics;
using Blitz3DCS.Engine;

namespace Blitz3DCS.Engine.Windows;

public static partial class LegacyGraphics
{
    private const int MaximumVisibleLights = 8;
    private static readonly Dictionary<int, LightState> LightEntities = [];
    private static readonly List<int> LightTraversal = [];
    private static Vector3 _ambientLight = new(127 / 255f);

    /// <summary>Sets scene ambient light from 0-255 RGB values, quantized like the legacy packed color.</summary>
    public static void AmbientLight(float red, float green, float blue)
    {
        CheckThreadIfStarted();
        ValidateFiniteColor(red, nameof(red));
        ValidateFiniteColor(green, nameof(green));
        ValidateFiniteColor(blue, nameof(blue));
        _ambientLight = new Vector3(QuantizeAmbient(red), QuantizeAmbient(green), QuantizeAmbient(blue));
    }

    /// <summary>Creates a directional (1), point (2), or spot (3) light entity.</summary>
    public static int CreateLight(int type = 1, int parent = 0)
    {
        CheckThreadIfStarted();
        if (type is < 1 or > 3) throw new ArgumentOutOfRangeException(nameof(type), "Light type must be directional (1), point (2), or spot (3).");
        var scene = RequireScene();
        var entity = scene.CreatePivot(parent);
        LightEntities.Add(entity, new LightState(type));
        return entity;
    }

    /// <summary>Sets the light diffuse color using legacy 0-255 RGB channel values.</summary>
    public static void LightColor(int light, float red, float green, float blue)
    {
        CheckThreadIfStarted();
        ValidateFiniteColor(red, nameof(red));
        ValidateFiniteColor(green, nameof(green));
        ValidateFiniteColor(blue, nameof(blue));
        var state = GetLightState(light);
        state.Color = new Vector3(red / 255f, green / 255f, blue / 255f);
    }

    /// <summary>Sets the source linear attenuation divisor for a point or spot light.</summary>
    public static void LightRange(int light, float range)
    {
        CheckThreadIfStarted();
        ValidateFiniteColor(range, nameof(range));
        if (range <= 0) throw new ArgumentOutOfRangeException(nameof(range), "Light range must be positive.");
        GetLightState(light).Range = range;
    }

    /// <summary>Sets spotlight cone angles in degrees, clamped to the source [inner, outer] limits.</summary>
    public static void LightConeAngles(int light, float innerAngle, float outerAngle)
    {
        CheckThreadIfStarted();
        ValidateFiniteColor(innerAngle, nameof(innerAngle));
        ValidateFiniteColor(outerAngle, nameof(outerAngle));
        var inner = DegreesToRadians(innerAngle);
        var outer = DegreesToRadians(outerAngle);
        if (inner < 0) inner = 0;
        else if (inner > MathF.PI) inner = MathF.PI;
        if (outer < inner) outer = inner;
        else if (outer > MathF.PI) outer = MathF.PI;
        var state = GetLightState(light);
        state.InnerConeRadians = inner;
        state.OuterConeRadians = outer;
    }

    /// <summary>Stores entity render flags for subsequent D3D11 lighting support.</summary>
    public static void EntityFX(int entity, int effects)
    {
        CheckThreadIfStarted();
        GetMesh(entity).Material.Effects = effects;
    }

    /// <summary>Stores one of the legacy blend modes (replace, alpha, multiply, add, DOT3, multiply2).</summary>
    public static void EntityBlend(int entity, int blend)
    {
        CheckThreadIfStarted();
        if (blend is < 0 or > 5) throw new ArgumentOutOfRangeException(nameof(blend));
        GetMesh(entity).Material.Blend = blend;
    }

    /// <summary>Stores a finite legacy shininess value for subsequent D3D11 lighting support.</summary>
    public static void EntityShininess(int entity, float shininess)
    {
        CheckThreadIfStarted();
        ValidateFiniteColor(shininess, nameof(shininess));
        GetMesh(entity).Material.Shininess = shininess;
    }

    internal static Vector3 AmbientLightForRendering => _ambientLight;

    /// <summary>Copies up to the first eight visible lights in scene hierarchy order into caller storage.</summary>
    internal static int CopyVisibleLightsForRendering(Span<LegacyLightSnapshot> destination)
    {
        CheckThreadIfStarted();
        var scene = RequireScene();
        var capacity = Math.Min(destination.Length, MaximumVisibleLights);
        var count = 0;
        if (LightEntities.Count == 0) return 0;
        scene.CopyVisibleEntityIds(LightTraversal);
        foreach (var entity in LightTraversal)
        {
            if (count == capacity) break;
            if (!LightEntities.TryGetValue(entity, out var state)) continue;
            var transform = scene.GetWorldTransform(entity);
            destination[count++] = new LegacyLightSnapshot(
                entity,
                state.Type,
                state.Color,
                state.Range,
                1f / state.Range,
                state.InnerConeRadians,
                state.OuterConeRadians,
                ToVector3(transform.Position),
                ToVector3(transform.BasisZ));
        }
        return count;
    }

    /// <summary>Copies per-light state after SceneGraph has created the copied entity ID.</summary>
    internal static void CopyLightState(int sourceEntity, int copiedEntity)
    {
        if (LightEntities.TryGetValue(sourceEntity, out var source))
            LightEntities.Add(copiedEntity, source.Copy());
    }

    /// <summary>Removes per-light state while freeing an entity subtree.</summary>
    internal static void ForgetLightState(int entity) => LightEntities.Remove(entity);

    internal static void ResetLightStateForTesting()
    {
        LightEntities.Clear();
        LightTraversal.Clear();
        _ambientLight = new Vector3(127 / 255f);
    }

    internal static LegacyLightSnapshot GetLightForTesting(int light)
    {
        var state = GetLightState(light);
        var transform = RequireScene().GetWorldTransform(light);
        return new LegacyLightSnapshot(light, state.Type, state.Color, state.Range, 1f / state.Range,
            state.InnerConeRadians, state.OuterConeRadians, ToVector3(transform.Position), ToVector3(transform.BasisZ));
    }

    internal static (int Effects, int Blend, float Shininess) GetEntityRenderSettingsForTesting(int entity)
    {
        var material = GetMesh(entity).Material;
        return (material.Effects, material.Blend, material.Shininess);
    }

    private static LightState GetLightState(int light) => LightEntities.TryGetValue(light, out var state)
        ? state
        : throw new ArgumentException($"Light entity {light} is invalid or has been freed.", nameof(light));

    private static float QuantizeAmbient(float value) =>
        MathF.Truncate((value * (1f / 255f)) * 255f) / 255f;

    private static Vector3 ToVector3(SceneVector3 value) => new(value.X, value.Y, value.Z);

    private sealed class LightState(int type)
    {
        public int Type { get; } = type;
        public Vector3 Color { get; set; } = Vector3.One;
        public float Range { get; set; } = 1000;
        public float InnerConeRadians { get; set; }
        public float OuterConeRadians { get; set; } = MathF.PI / 2;

        public LightState Copy() => new(Type)
        {
            Color = Color,
            Range = Range,
            InnerConeRadians = InnerConeRadians,
            OuterConeRadians = OuterConeRadians
        };
    }
}

internal readonly record struct LegacyLightSnapshot(
    int Entity,
    int Type,
    Vector3 Color,
    float Range,
    float LinearAttenuation,
    float InnerConeRadians,
    float OuterConeRadians,
    Vector3 Position,
    Vector3 Direction);
