using System.Numerics;
using Blitz3DCS.Graphics.D3D11;

namespace Blitz3DCS.Engine.Windows;

public static partial class LegacyGraphics
{
    public static void EntityTexture(int entity, int texture, int frame = 0, int index = 0)
    {
        CheckThreadIfStarted();
        if ((uint)index >= 8) throw new ArgumentOutOfRangeException(nameof(index));
        var material = GetMesh(entity).Material;
        var binding = AcquireTextureBinding(texture, frame);
        var previous = material.Textures[index];
        material.Textures[index] = binding;
        material.UpdateLayer(index);
        previous?.Dispose();
    }

    public static void EntityColor(int entity, float red, float green, float blue)
    {
        CheckThreadIfStarted();
        ValidateFiniteColor(red, nameof(red));
        ValidateFiniteColor(green, nameof(green));
        ValidateFiniteColor(blue, nameof(blue));
        var material = GetMesh(entity).Material;
        material.Color = new Vector4(red / 255f, green / 255f, blue / 255f, material.Color.W);
    }

    public static void EntityAlpha(int entity, float alpha)
    {
        CheckThreadIfStarted();
        ValidateFiniteColor(alpha, nameof(alpha));
        var material = GetMesh(entity).Material;
        material.Color = new Vector4(material.Color.X, material.Color.Y, material.Color.Z, alpha);
    }

    private static void CloseMaterials()
    {
        foreach (var mesh in MeshEntities.Values) mesh.Material.Dispose();
        foreach (var surface in Surfaces.Values) surface.Material.Dispose();
        CloseBrushes();
    }

    internal static Vector4 GetEntityMaterialColorForTesting(int entity) => GetMesh(entity).Material.Color;

    private static Vector4 ComposeColor(EntityMaterial surface, EntityMaterial entity) =>
        new(surface.Color.X * entity.Color.X, surface.Color.Y * entity.Color.Y,
            surface.Color.Z * entity.Color.Z, surface.Color.W * entity.Color.W);

    private static void ValidateRenderSupport(EntityMaterial material)
    {
        ValidateRenderSettings(material.Shininess, material.Effects, material.Blend);
    }

    private static void ValidateRenderSettings(float shininess, int effects, int blend)
    {
        if (!float.IsFinite(shininess)) throw new ArgumentOutOfRangeException(nameof(shininess));
        if ((effects & ~0x403f) != 0)
            throw new NotSupportedException($"Brush effects 0x{effects & ~0x403f:X} are not implemented by this renderer.");
        if (blend is < 0 or > 3)
            throw new NotSupportedException($"Brush blend mode {blend} is not implemented by this renderer.");
    }

    private static int ComposeBlend(EntityMaterial surface, EntityMaterial entity) => entity.Blend != 0 ? entity.Blend : surface.Blend;

    internal static int ResolveMaterialBlend(int requested, int textureStages, bool transparentTexture, float alpha, int effects)
    {
        if (requested is 2 or 3) return requested;
        if (transparentTexture && (requested == 1 || textureStages == 1)) return 1;
        return alpha < 1 || (effects & 32) != 0 ? 1 : 0;
    }

    private static void ValidateComposedMaterial(EntityMaterial surface, EntityMaterial entity)
    {
        ValidateRenderSettings(surface.Shininess + entity.Shininess, surface.Effects | entity.Effects, ComposeBlend(surface, entity));
    }

    internal sealed class EntityMaterial : IDisposable
    {
        internal Vector4 Color = Vector4.One;
        internal float Shininess;
        internal int Blend;
        internal int Effects;
        internal readonly TextureBinding?[] Textures = new TextureBinding?[8];
        internal readonly D3D11TextureLayer[] Layers = new D3D11TextureLayer[8];
        internal bool UsesAlpha => Color.W < 1 || Textures.Any(static texture => texture is { UseAlpha: true, Blend: not 0 });

        internal void UpdateLayer(int index)
        {
            var texture = Textures[index];
            Layers[index] = texture is null ? default : new D3D11TextureLayer(
                texture.Resource, texture.Blend, texture.ClampU, texture.ClampV, texture.UseAlpha,
                texture.UvTransform, texture.CoordinateSet, texture.Masked,
                texture.BumpMatrix, texture.BumpScale, texture.BumpOffset);
        }

        internal EntityMaterial Copy()
        {
            var copy = new EntityMaterial { Color = Color, Shininess = Shininess, Blend = Blend, Effects = Effects };
            try
            {
                for (var index = 0; index < Textures.Length; index++)
                {
                    copy.Textures[index] = Textures[index]?.Retain();
                    copy.UpdateLayer(index);
                }
                return copy;
            }
            catch { copy.Dispose(); throw; }
        }

        public void Dispose()
        {
            for (var index = 0; index < Textures.Length; index++)
            {
                Textures[index]?.Dispose();
                Textures[index] = null;
                Layers[index] = default;
            }
        }
    }
}
