using System.Numerics;
using Blitz3DCS.Engine;

namespace Blitz3DCS.Engine.Windows;

public static partial class LegacyGraphics
{
    private static readonly Dictionary<int, EntityMaterial> Brushes = [];
    private static int _nextBrushHandle = 1;

    public static int CreateBrush(float red = 255, float green = 255, float blue = 255)
    {
        CheckThreadIfStarted();
        ValidateFiniteColor(red, nameof(red));
        ValidateFiniteColor(green, nameof(green));
        ValidateFiniteColor(blue, nameof(blue));
        var brush = new EntityMaterial
        {
            Color = new Vector4(red / 255f, green / 255f, blue / 255f, 1)
        };
        return RegisterBrush(brush);
    }

    public static int LoadBrush(string file, int textureFlags = 1, float uScale = 1, float vScale = 1)
    {
        ArgumentException.ThrowIfNullOrEmpty(file);
        ValidateFiniteColor(uScale, nameof(uScale));
        ValidateFiniteColor(vScale, nameof(vScale));
        if (uScale == 0) throw new ArgumentOutOfRangeException(nameof(uScale));
        if (vScale == 0) throw new ArgumentOutOfRangeException(nameof(vScale));

        var texture = LoadTexture(file, textureFlags);
        if (texture == 0) return 0;
        var brush = 0;
        try
        {
            ScaleTexture(texture, uScale, vScale);
            brush = CreateBrush();
            BrushTexture(brush, texture, 0, 0);
            return brush;
        }
        catch
        {
            FreeBrush(brush);
            throw;
        }
        finally
        {
            FreeTexture(texture);
        }
    }

    /// <summary>
    /// Original bbFreeBrush: <c>if( !b ) return; debugBrush(b); if( brush_set.erase( b ) ) delete b;</c>. 0, unknown
    /// and already-freed handles are no-ops in a release build (<c>debugBrush</c> only throws under the debug flag).
    /// </summary>
    public static void FreeBrush(int brush)
    {
        CheckThreadIfStarted();
        if (brush == 0) return;
        if (Brushes.Remove(brush, out var material)) material.Dispose();
    }

    public static void BrushColor(int brush, float red, float green, float blue)
    {
        CheckThreadIfStarted();
        ValidateFiniteColor(red, nameof(red));
        ValidateFiniteColor(green, nameof(green));
        ValidateFiniteColor(blue, nameof(blue));
        var material = GetBrush(brush);
        material.Color = new Vector4(red / 255f, green / 255f, blue / 255f, material.Color.W);
    }

    public static void BrushAlpha(int brush, float alpha)
    {
        CheckThreadIfStarted();
        ValidateFiniteColor(alpha, nameof(alpha));
        var material = GetBrush(brush);
        material.Color = new Vector4(material.Color.X, material.Color.Y, material.Color.Z, alpha);
    }

    public static void BrushShininess(int brush, float shininess)
    {
        CheckThreadIfStarted();
        ValidateFiniteColor(shininess, nameof(shininess));
        GetBrush(brush).Shininess = shininess;
    }

    public static void BrushTexture(int brush, int texture, int frame = 0, int index = 0)
    {
        CheckThreadIfStarted();
        if ((uint)index >= 8) throw new ArgumentOutOfRangeException(nameof(index));
        var material = GetBrush(brush);
        var replacement = AcquireTextureBinding(texture, frame);
        var previous = material.Textures[index];
        material.Textures[index] = replacement;
        material.UpdateLayer(index);
        previous?.Dispose();
    }

    public static void BrushBlend(int brush, int blend) => GetBrushAfterThreadCheck(brush).Blend = blend;

    public static void BrushFX(int brush, int effects) => GetBrushAfterThreadCheck(brush).Effects = effects;

    public static void PaintSurface(int surface, int brush)
    {
        CheckThreadIfStarted();
        var replacement = GetBrush(brush).Copy();
        var state = GetSurface(surface);
        var previous = state.Material;
        state.Material = replacement;
        previous.Dispose();
    }

    public static void PaintEntity(int entity, int brush)
    {
        CheckThreadIfStarted();
        var replacement = GetBrush(brush).Copy();
        var mesh = GetMesh(entity);
        var previous = mesh.Material;
        mesh.Material = replacement;
        previous.Dispose();
    }

    public static int GetSurfaceBrush(int surface)
    {
        CheckThreadIfStarted();
        return RegisterBrush(GetSurface(surface).Material.Copy());
    }

    public static int GetEntityBrush(int entity)
    {
        CheckThreadIfStarted();
        return RegisterBrush(GetMesh(entity).Material.Copy());
    }

    internal static EntityMaterial BuildMaterial(B3dBrush brush, IReadOnlyList<int> textureHandles)
    {
        ArgumentNullException.ThrowIfNull(brush);
        ArgumentNullException.ThrowIfNull(textureHandles);
        var material = new EntityMaterial
        {
            Color = new Vector4(brush.Color.R, brush.Color.G, brush.Color.B, brush.Color.A),
            Shininess = brush.Shininess,
            Blend = brush.Blend,
            Effects = brush.Effects
        };
        try
        {
            for (var index = 0; index < Math.Min(8, brush.TextureIds.Count); index++)
            {
                var textureId = brush.TextureIds[index];
                if (textureId < 0) continue;
                if ((uint)textureId >= (uint)textureHandles.Count)
                    throw new ArgumentOutOfRangeException(nameof(brush), $"Brush texture id {textureId} is outside the loaded texture table.");
                var textureHandle = textureHandles[textureId];
                if (textureHandle == 0) continue;
                material.Textures[index] = AcquireTextureBinding(textureHandle);
                material.UpdateLayer(index);
            }
            return material;
        }
        catch
        {
            material.Dispose();
            throw;
        }
    }

    /// <summary>Transfers ownership of a newly built B3D material to an existing surface.</summary>
    internal static void SetSurfaceMaterialForLoading(int surface, EntityMaterial material)
    {
        ArgumentNullException.ThrowIfNull(material);
        var state = GetSurface(surface);
        var previous = state.Material;
        state.Material = material;
        previous.Dispose();
    }

    internal static Vector4 GetBrushColorForTesting(int brush) => GetBrush(brush).Color;

    internal static (int Blend, int Effects, float Shininess) GetBrushSettingsForTesting(int brush)
    {
        var material = GetBrush(brush);
        return (material.Blend, material.Effects, material.Shininess);
    }

    internal static Vector4 GetSurfaceMaterialColorForTesting(int surface) => GetSurface(surface).Material.Color;

    internal static float GetSurfaceMaterialShininessForTesting(int surface) => GetSurface(surface).Material.Shininess;

    internal static Vector4 GetComposedMaterialColorForTesting(int entity, int surface) =>
        ComposeColor(GetSurface(surface).Material, GetMesh(entity).Material);

    internal static void ValidateMaterialForTesting(int surface)
    {
        var material = GetSurface(surface).Material;
        ValidateRenderSupport(material);
    }

    internal static void CloseBrushes()
    {
        foreach (var brush in Brushes.Values) brush.Dispose();
        Brushes.Clear();
        _nextBrushHandle = 1;
    }

    private static int RegisterBrush(EntityMaterial material)
    {
        var handle = _nextBrushHandle;
        _nextBrushHandle = checked(handle + 1);
        try
        {
            Brushes.Add(handle, material);
            return handle;
        }
        catch
        {
            material.Dispose();
            throw;
        }
    }

    private static EntityMaterial GetBrush(int brush) => Brushes.TryGetValue(brush, out var material)
        ? material : throw new ArgumentException($"Brush handle {brush} is invalid or has been freed.", nameof(brush));

    private static EntityMaterial GetBrushAfterThreadCheck(int brush)
    {
        CheckThreadIfStarted();
        return GetBrush(brush);
    }
}
