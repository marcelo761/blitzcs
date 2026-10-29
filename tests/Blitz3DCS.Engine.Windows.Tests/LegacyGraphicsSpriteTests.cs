using System.Numerics;
using Blitz3DCS.Engine;
using Blitz3DCS.Engine.Windows;
using Xunit;

namespace Blitz3DCS.Engine.Windows.Tests;

public sealed class LegacyGraphicsSpriteTests
{
    [WindowsSpriteSmokeFact]
    public void Sprites_render_in_native_D3D11_for_each_view_mode_and_copies_are_independent() => RunSmoke();

    internal static void RunSmoke()
    {
        var previousScene = LegacyScene.Current;
        LegacyGraphics.ResetForTests();
        LegacyScene.Current = new SceneGraph();
        var sprite = 0;
        var copy = 0;
        try
        {
            LegacyGraphics.Graphics3D(128, 96);
            var camera = LegacyGraphics.CreateCamera();
            LegacyScene.PositionEntity(camera, 0, 0, -5);
            LegacyGraphics.CameraRange(camera, 0.1f, 100);
            LegacyGraphics.CameraClsColor(camera, 0, 0, 0);
            sprite = LegacyGraphics.CreateSprite();
            LegacyScene.PositionEntity(sprite, 0, 0, 0);

            for (var mode = 1; mode <= 4; mode++)
            {
                LegacyGraphics.SpriteViewMode(sprite, mode);
                LegacyGraphics.RenderWorld();
                var center = LegacyGraphics.ReadPixelForTesting(64, 48);
                Assert.True(center.R != 0 || center.G != 0 || center.B != 0,
                    $"Sprite view mode {mode} must produce a visible native pixel.");
            }

            copy = LegacyGraphics.CopyEntity(sprite);
            LegacyGraphics.ScaleSprite(copy, 0.5f, 0.5f);
            LegacyGraphics.RotateSprite(copy, 23);
            LegacyScene.PositionEntity(copy, 1.5f, 0, 0);
            LegacyGraphics.RenderWorld();
            Assert.Equal(LegacyGraphics.GetSurface(sprite, 1), LegacyGraphics.GetSurface(copy, 1));
            Assert.NotEqual(LegacyGraphics.GetSpriteSettingsForTesting(sprite), LegacyGraphics.GetSpriteSettingsForTesting(copy));
            LegacyGraphics.FreeEntity(sprite);
            sprite = 0;
            LegacyGraphics.FreeEntity(copy);
            copy = 0;
            sprite = LegacyGraphics.CreateSprite();
            Assert.Equal(4, LegacyGraphics.CountVertices(LegacyGraphics.GetSurface(sprite, 1)));
        }
        finally
        {
            if (sprite != 0) LegacyGraphics.FreeEntity(sprite);
            if (copy != 0) LegacyGraphics.FreeEntity(copy);
            LegacyGraphics.EndGraphics();
            LegacyGraphics.ResetForTests();
            LegacyScene.Current = previousScene;
        }
    }

    [Fact]
    public void Sprite_state_modes_transform_and_keep_copy_state_independent_until_final_free()
    {
        var previousScene = LegacyScene.Current;
        LegacyGraphics.ResetForTests();
        LegacyScene.Current = new SceneGraph();
        var sprite = 0;
        var copy = 0;
        try
        {
            sprite = LegacyGraphics.CreateSprite();
            LegacyGraphics.ScaleSprite(sprite, 2, 3);
            LegacyGraphics.RotateSprite(sprite, 0);
            LegacyGraphics.HandleSprite(sprite, 1, 1);
            Assert.Equal((2, 3, 0, 1, 1, 1), LegacyGraphics.GetSpriteSettingsForTesting(sprite));

            var entityWorld = Matrix4x4.CreateScale(2, 3, 4) * Matrix4x4.CreateRotationY(.4f) *
                              Matrix4x4.CreateTranslation(5, 6, 7);
            var cameraWorld = Matrix4x4.CreateRotationY(-.8f);
            LegacyGraphics.SpriteViewMode(sprite, 1);
            var free = LegacyGraphics.BuildSpriteWorldMatrixForTesting(sprite, entityWorld, cameraWorld);
            Assert.Equal(new Vector3(5, 6, 7), Vector3.Transform(new Vector3(1, 1, 0), free));
            Assert.InRange(Vector3.TransformNormal(Vector3.UnitX, free).Length(), 1.999f, 2.001f);
            Assert.InRange(Vector3.TransformNormal(Vector3.UnitY, free).Length(), 2.999f, 3.001f);

            LegacyGraphics.SpriteViewMode(sprite, 2);
            var fixedMode = LegacyGraphics.BuildSpriteWorldMatrixForTesting(sprite, entityWorld, cameraWorld);
            Assert.InRange(Vector3.TransformNormal(Vector3.UnitX, fixedMode).Length(), 3.999f, 4.001f);
            Assert.InRange(Vector3.TransformNormal(Vector3.UnitY, fixedMode).Length(), 8.999f, 9.001f);
            Assert.Equal(new Vector3(5, 6, 7), Vector3.Transform(new Vector3(1, 1, 0), fixedMode));

            LegacyGraphics.SpriteViewMode(sprite, 3);
            var upright = LegacyGraphics.BuildSpriteWorldMatrixForTesting(sprite, entityWorld, cameraWorld);
            Assert.InRange(Vector3.TransformNormal(Vector3.UnitX, upright).Length(), 1.999f, 2.001f);
            Assert.InRange(Vector3.TransformNormal(Vector3.UnitY, upright).Length(), 2.999f, 3.001f);

            LegacyGraphics.SpriteViewMode(sprite, 4);
            var upright2 = LegacyGraphics.BuildSpriteWorldMatrixForTesting(sprite, entityWorld, cameraWorld);
            Assert.InRange(Vector3.TransformNormal(Vector3.UnitX, upright2).Length(), 3.999f, 4.001f);
            Assert.InRange(Vector3.TransformNormal(Vector3.UnitY, upright2).Length(), 8.999f, 9.001f);

            copy = LegacyGraphics.CopyEntity(sprite);
            Assert.Equal(LegacyGraphics.GetSpriteSettingsForTesting(sprite), LegacyGraphics.GetSpriteSettingsForTesting(copy));
            LegacyGraphics.ScaleSprite(copy, 7, 8);
            LegacyGraphics.SpriteViewMode(copy, 3);
            Assert.Equal((2, 3, 0, 1, 1, 4), LegacyGraphics.GetSpriteSettingsForTesting(sprite));
            Assert.Equal((7, 8, 0, 1, 1, 3), LegacyGraphics.GetSpriteSettingsForTesting(copy));

            var surface = LegacyGraphics.GetSurface(sprite, 1);
            Assert.Equal(surface, LegacyGraphics.GetSurface(copy, 1));
            Assert.Equal(4, LegacyGraphics.CountVertices(surface));
            Assert.Equal(2, LegacyGraphics.CountTriangles(surface));
            LegacyGraphics.FreeEntity(sprite);
            sprite = 0;
            Assert.Equal(4, LegacyGraphics.CountVertices(LegacyGraphics.GetSurface(copy, 1)));
            LegacyGraphics.FreeEntity(copy);
            copy = 0;
            Assert.Throws<ArgumentException>(() => LegacyGraphics.GetSpriteSettingsForTesting(copy));

            sprite = LegacyGraphics.CreateSprite();
            Assert.Equal(4, LegacyGraphics.CountVertices(LegacyGraphics.GetSurface(sprite, 1)));
            Assert.Equal(2, LegacyGraphics.CountTriangles(LegacyGraphics.GetSurface(sprite, 1)));
        }
        finally
        {
            if (sprite != 0) LegacyGraphics.FreeEntity(sprite);
            if (copy != 0) LegacyGraphics.FreeEntity(copy);
            LegacyGraphics.ResetForTests();
            LegacyScene.Current = previousScene;
        }
    }

    private sealed class WindowsSpriteSmokeFactAttribute : FactAttribute
    {
        public WindowsSpriteSmokeFactAttribute()
        {
            if (!OperatingSystem.IsWindows()) Skip = "D3D11 is a Windows-only runtime.";
            else if (Environment.GetEnvironmentVariable("BLITZCS_RUN_D3D11_SMOKE") != "1")
                Skip = "Set BLITZCS_RUN_D3D11_SMOKE=1 to run the real D3D11 sprite smoke test.";
        }
    }
}
