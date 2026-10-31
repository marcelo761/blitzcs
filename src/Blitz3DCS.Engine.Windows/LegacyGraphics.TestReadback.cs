namespace Blitz3DCS.Engine.Windows;

public static partial class LegacyGraphics
{
    internal static byte[] ReadBackbufferPixelsForTesting() => RequireRenderer().ReadTexturePixels();

    internal static int GetAnimatedTextureBindingCountForTesting(int entity)
    {
        if (!AnimatedModels.TryGetValue(entity, out var model)) throw new ArgumentException("The entity is not an animated model root.", nameof(entity));
        var count = 0;
        foreach (var mesh in model.Meshes)
            foreach (var texture in GetMesh(mesh.Entity).Material.Textures)
                if (texture is not null) count++;
        foreach (var mesh in model.Meshes)
            foreach (var surface in mesh.Surfaces)
                foreach (var texture in GetSurface(surface.Handle).Material.Textures)
                    if (texture is not null) count++;
        return count;
    }
}
