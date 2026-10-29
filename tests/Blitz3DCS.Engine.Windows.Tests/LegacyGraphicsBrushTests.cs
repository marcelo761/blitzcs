using System.Numerics;
using Blitz3DCS.Engine;
using Blitz3DCS.Engine.Windows;
using Xunit;

namespace Blitz3DCS.Engine.Windows.Tests;

[Collection("LegacyGraphics static state")]
public sealed class LegacyGraphicsBrushTests
{
    [Fact]
    public void PaintSurface_captures_brush_state_and_getters_return_independent_brushes()
    {
        var previousScene = LegacyScene.Current;
        LegacyGraphics.ResetForTests();
        LegacyScene.Current = new SceneGraph();
        try
        {
            var mesh = LegacyGraphics.CreateMesh();
            var surface = LegacyGraphics.CreateSurface(mesh);
            var source = LegacyGraphics.CreateBrush(128, 64, 255);
            LegacyGraphics.BrushAlpha(source, 0.75f);
            LegacyGraphics.BrushShininess(source, 0.25f);
            LegacyGraphics.BrushBlend(source, 1);
            LegacyGraphics.BrushFX(source, 2);

            LegacyGraphics.PaintSurface(surface, source);
            LegacyGraphics.BrushColor(source, 0, 255, 0);
            LegacyGraphics.BrushAlpha(source, 0.1f);

            var snapshot = LegacyGraphics.GetSurfaceBrush(surface);
            Assert.Equal(new Vector4(128 / 255f, 64 / 255f, 1, 0.75f),
                LegacyGraphics.GetBrushColorForTesting(snapshot));
            Assert.Equal((1, 2, 0.25f), LegacyGraphics.GetBrushSettingsForTesting(snapshot));

            LegacyGraphics.BrushColor(snapshot, 255, 0, 0);
            var secondSnapshot = LegacyGraphics.GetSurfaceBrush(surface);
            Assert.Equal(new Vector4(128 / 255f, 64 / 255f, 1, 0.75f),
                LegacyGraphics.GetBrushColorForTesting(secondSnapshot));

            LegacyGraphics.FreeBrush(source);
            Assert.Throws<ArgumentException>(() => LegacyGraphics.BrushColor(source, 1, 2, 3));
            LegacyGraphics.FreeBrush(snapshot);
            LegacyGraphics.FreeBrush(secondSnapshot);
        }
        finally
        {
            LegacyGraphics.ResetForTests();
            LegacyScene.Current = previousScene;
        }
    }

    [Fact]
    public void PaintEntity_composes_with_surface_material_without_mutating_either_snapshot()
    {
        var previousScene = LegacyScene.Current;
        LegacyGraphics.ResetForTests();
        LegacyScene.Current = new SceneGraph();
        try
        {
            var mesh = LegacyGraphics.CreateMesh();
            var surface = LegacyGraphics.CreateSurface(mesh);
            var surfaceBrush = LegacyGraphics.CreateBrush(128, 64, 255);
            LegacyGraphics.BrushAlpha(surfaceBrush, 0.5f);
            LegacyGraphics.PaintSurface(surface, surfaceBrush);

            var entityBrush = LegacyGraphics.CreateBrush(128, 255, 64);
            LegacyGraphics.BrushAlpha(entityBrush, 0.8f);
            LegacyGraphics.PaintEntity(mesh, entityBrush);

            Assert.Equal(new Vector4(128 / 255f, 64 / 255f, 1, 0.5f),
                LegacyGraphics.GetSurfaceMaterialColorForTesting(surface));
            Assert.Equal(new Vector4(128 / 255f, 1, 64 / 255f, 0.8f),
                LegacyGraphics.GetEntityMaterialColorForTesting(mesh));
            Assert.Equal(new Vector4((128 / 255f) * (128 / 255f), 64 / 255f,
                    64 / 255f, 0.4f),
                LegacyGraphics.GetComposedMaterialColorForTesting(mesh, surface));

            var entitySnapshot = LegacyGraphics.GetEntityBrush(mesh);
            LegacyGraphics.BrushColor(entityBrush, 0, 0, 0);
            Assert.Equal(new Vector4(128 / 255f, 1, 64 / 255f, 0.8f),
                LegacyGraphics.GetBrushColorForTesting(entitySnapshot));
            LegacyGraphics.FreeBrush(entitySnapshot);
        }
        finally
        {
            LegacyGraphics.ResetForTests();
            LegacyScene.Current = previousScene;
        }
    }

    [Fact]
    public void CreateSurface_can_copy_a_brush_and_unsupported_render_settings_are_reported()
    {
        var previousScene = LegacyScene.Current;
        LegacyGraphics.ResetForTests();
        LegacyScene.Current = new SceneGraph();
        try
        {
            var mesh = LegacyGraphics.CreateMesh();
            var brush = LegacyGraphics.CreateBrush();
            LegacyGraphics.BrushShininess(brush, 0.5f);
            var surface = LegacyGraphics.CreateSurface(mesh, brush);
            LegacyGraphics.BrushShininess(brush, 0);

            Assert.Equal(0.5f, LegacyGraphics.GetSurfaceMaterialShininessForTesting(surface));
            LegacyGraphics.ValidateMaterialForTesting(surface);
            Assert.Equal(Vector4.One, LegacyGraphics.GetBrushColorForTesting(LegacyGraphics.GetSurfaceBrush(surface)));

            LegacyGraphics.BrushShininess(brush, 0);
            LegacyGraphics.BrushBlend(brush, 3);
            LegacyGraphics.PaintSurface(surface, brush);
            LegacyGraphics.ValidateMaterialForTesting(surface);
            LegacyGraphics.BrushBlend(brush, 4);
            LegacyGraphics.PaintSurface(surface, brush);
            Assert.Throws<NotSupportedException>(() => LegacyGraphics.ValidateMaterialForTesting(surface));
        }
        finally
        {
            LegacyGraphics.ResetForTests();
            LegacyScene.Current = previousScene;
        }
    }
}
