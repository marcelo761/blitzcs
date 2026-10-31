using Blitz3DCS.Engine;
using Blitz3DCS.Engine.Windows;
using Xunit;

namespace Blitz3DCS.Engine.Windows.Tests;

[Collection("LegacyGraphics static state")]
public sealed class LegacyGraphicsLifecycleTests
{
    [Fact]
    public void Free_zero_handles_are_no_ops_and_unknown_textures_too_but_unknown_entities_still_fail()
    {
        WithCleanState(() =>
        {
            var entity = LegacyScene.CreatePivot();

            LegacyGraphics.FreeEntity(0);
            LegacyGraphics.FreeTexture(0);

            Assert.Equal(string.Empty, LegacyScene.EntityName(entity));
            // bbFreeEntity: `if( !e ) return; if( debug ){ debugEntity(e); erase(e); } delete e;` - only 0 is defined,
            // a dangling entity is "Entity does not exist" (debug) or undefined behaviour (release), so it stays an error.
            Assert.Throws<KeyNotFoundException>(() => LegacyGraphics.FreeEntity(int.MaxValue));
            // bbFreeTexture: `if( !t ) return; debugTexture(t); if( texture_set.erase( t ) ) delete t;` - debugTexture only
            // throws under `if( debug )`, so a release build ignores a never-valid handle (this used to assert a throw).
            LegacyGraphics.FreeTexture(int.MaxValue);
        });
    }

    [Fact]
    public void ClearWorld_flags_free_entities_and_brushes_selectively_without_reusing_handles()
    {
        WithCleanState(() =>
        {
            var brush = LegacyGraphics.CreateBrush(20, 40, 60);
            var mesh = LegacyGraphics.CreateMesh();
            var child = LegacyScene.CreatePivot(mesh);
            var surface = LegacyGraphics.CreateSurface(mesh, brush);

            LegacyGraphics.ClearWorld(entities: 0, brushes: 1, textures: 0);
            Assert.Equal(1, LegacyGraphics.CountSurfaces(mesh));
            Assert.Throws<ArgumentException>(() => LegacyGraphics.GetBrushSettingsForTesting(brush));
            var nextBrush = LegacyGraphics.CreateBrush();
            Assert.True(nextBrush > brush);

            LegacyGraphics.ClearWorld(entities: 1, brushes: 0, textures: 0);
            Assert.Throws<ArgumentException>(() => LegacyGraphics.CountSurfaces(mesh));
            Assert.Throws<KeyNotFoundException>(() => LegacyScene.EntityName(child));
            Assert.Equal((0, 0, 0), LegacyGraphics.GetBrushSettingsForTesting(nextBrush));
            Assert.Throws<ArgumentException>(() => LegacyGraphics.GetVertexForTesting(surface, 0));
        });
    }

    private static void WithCleanState(Action action)
    {
        var previousScene = LegacyScene.Current;
        LegacyGraphics.ResetForTests();
        LegacyScene.Current = new SceneGraph();
        try { action(); }
        finally
        {
            LegacyGraphics.ResetForTests();
            LegacyScene.Current = previousScene;
        }
    }
}
