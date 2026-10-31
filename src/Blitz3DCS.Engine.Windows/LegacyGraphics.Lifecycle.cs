namespace Blitz3DCS.Engine.Windows;

public static partial class LegacyGraphics
{
    /// <summary>Frees selected world entities, brush handles, and texture caller handles.</summary>
    public static void ClearWorld(int entities = 1, int brushes = 1, int textures = 1)
    {
        CheckThreadIfStarted();
        if (entities != 0)
        {
            var scene = RequireScene();
            foreach (var root in scene.GetRootEntityIds()) FreeEntity(root);
        }

        if (brushes != 0)
            foreach (var brush in Brushes.Keys.ToArray()) FreeBrush(brush);

        if (textures != 0)
            foreach (var texture in Textures.Keys.ToArray()) FreeTexture(texture);
    }
}
