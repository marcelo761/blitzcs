using System.Numerics;
using Blitz3DCS.Engine;
using Blitz3DCS.Engine.Windows;
using Xunit;

namespace Blitz3DCS.Engine.Windows.Tests;

[Collection("LegacyGraphics static state")]
public sealed class LegacyGraphicsLightsTests
{
    [Fact]
    public void Ambient_light_quantizes_channels_to_legacy_rgb_byte_precision()
    {
        WithFreshScene(() =>
        {
            LegacyGraphics.AmbientLight(42.9f, 128.8f, 255);

            Assert.Equal(new Vector3(42 / 255f, 128 / 255f, 1), LegacyGraphics.AmbientLightForRendering);
        });
    }

    [Fact]
    public void Create_light_stores_legacy_defaults_transforms_and_clamped_spot_angles()
    {
        WithFreshScene(() =>
        {
            var scene = LegacyScene.Current;
            var parent = scene.CreatePivot();
            scene.PositionEntity(parent, 4, 5, 6);
            var light = LegacyGraphics.CreateLight(3, parent);

            var defaults = LegacyGraphics.GetLightForTesting(light);
            Assert.Equal(3, defaults.Type);
            Assert.Equal(Vector3.One, defaults.Color);
            Assert.Equal(1000, defaults.Range);
            Assert.Equal(.001f, defaults.LinearAttenuation, 6);
            Assert.Equal(0, defaults.InnerConeRadians);
            Assert.Equal(MathF.PI / 2, defaults.OuterConeRadians);
            Assert.Equal(new Vector3(4, 5, 6), defaults.Position);
            Assert.Equal(Vector3.UnitZ, defaults.Direction);

            LegacyGraphics.LightColor(light, 255, 64, 0);
            LegacyGraphics.LightRange(light, 4);
            LegacyGraphics.LightConeAngles(light, -10, 360);
            var configured = LegacyGraphics.GetLightForTesting(light);
            Assert.Equal(new Vector3(1, 64 / 255f, 0), configured.Color);
            Assert.Equal(.25f, configured.LinearAttenuation);
            Assert.Equal(0, configured.InnerConeRadians);
            Assert.Equal(MathF.PI, configured.OuterConeRadians);

            LegacyGraphics.LightConeAngles(light, 120, 30);
            configured = LegacyGraphics.GetLightForTesting(light);
            Assert.Equal(120 * (MathF.PI / 180f), configured.InnerConeRadians, 5);
            Assert.Equal(configured.InnerConeRadians, configured.OuterConeRadians);
        });
    }

    [Fact]
    public void Visible_light_snapshots_skip_hidden_ancestors_and_keep_first_eight_in_hierarchy_order()
    {
        WithFreshScene(() =>
        {
            var scene = LegacyScene.Current;
            var parent = scene.CreatePivot();
            var first = LegacyGraphics.CreateLight(2, parent);
            scene.HideEntity(parent);
            for (var i = 0; i < 9; i++) LegacyGraphics.CreateLight(i % 2 == 0 ? 1 : 2);

            var hiddenLights = new LegacyLightSnapshot[8];
            Assert.Equal(8, LegacyGraphics.CopyVisibleLightsForRendering(hiddenLights));
            Assert.Equal(first + 1, hiddenLights[0].Entity);
            Assert.Equal(first + 8, hiddenLights[7].Entity);

            scene.ShowEntity(parent);
            var visibleLights = new LegacyLightSnapshot[8];
            Assert.Equal(8, LegacyGraphics.CopyVisibleLightsForRendering(visibleLights));
            Assert.Equal(first, visibleLights[0].Entity);
            Assert.Equal(first + 7, visibleLights[7].Entity);
        });
    }

    [Fact]
    public void Light_copy_and_free_helpers_preserve_independent_state()
    {
        WithFreshScene(() =>
        {
            var source = LegacyGraphics.CreateLight(2);
            LegacyGraphics.LightColor(source, 10, 20, 30);
            LegacyGraphics.LightRange(source, 8);
            LegacyGraphics.LightConeAngles(source, 20, 45);
            var copy = LegacyGraphics.CopyEntity(source);

            var copied = LegacyGraphics.GetLightForTesting(copy);
            Assert.Equal(2, copied.Type);
            Assert.Equal(new Vector3(10 / 255f, 20 / 255f, 30 / 255f), copied.Color);
            Assert.Equal(8, copied.Range);
            Assert.Equal(20 * (MathF.PI / 180f), copied.InnerConeRadians, 5);
            Assert.Equal(45 * (MathF.PI / 180f), copied.OuterConeRadians, 5);

            LegacyGraphics.LightColor(copy, 255, 0, 0);
            Assert.Equal(new Vector3(10 / 255f, 20 / 255f, 30 / 255f), LegacyGraphics.GetLightForTesting(source).Color);
            LegacyGraphics.FreeEntity(copy);
            Assert.Throws<ArgumentException>(() => LegacyGraphics.GetLightForTesting(copy));
        });
    }

    [Fact]
    public void Light_hierarchy_order_precedes_creation_order_without_warmed_allocations()
    {
        WithFreshScene(() =>
        {
            var parent = LegacyScene.CreatePivot();
            var rootLight = LegacyGraphics.CreateLight();
            var childLight = LegacyGraphics.CreateLight(2, parent);
            Span<LegacyLightSnapshot> lights = stackalloc LegacyLightSnapshot[8];
            Assert.Equal(2, LegacyGraphics.CopyVisibleLightsForRendering(lights));
            Assert.Equal(childLight, lights[0].Entity);
            Assert.Equal(rootLight, lights[1].Entity);
            _ = LegacyGraphics.CopyVisibleLightsForRendering(lights);
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < 256; i++) _ = LegacyGraphics.CopyVisibleLightsForRendering(lights);
            Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        });
    }

    [Fact]
    public void Entity_render_properties_are_stored_and_numeric_inputs_are_validated()
    {
        WithFreshScene(() =>
        {
            var mesh = LegacyGraphics.CreateMesh();
            LegacyGraphics.EntityFX(mesh, 1 | 2 | 8);
            LegacyGraphics.EntityBlend(mesh, 5);
            LegacyGraphics.EntityShininess(mesh, 1.25f);

            Assert.Equal((11, 5, 1.25f), LegacyGraphics.GetEntityRenderSettingsForTesting(mesh));
            Assert.Throws<ArgumentOutOfRangeException>(() => LegacyGraphics.EntityBlend(mesh, 6));
            Assert.Throws<ArgumentOutOfRangeException>(() => LegacyGraphics.EntityShininess(mesh, float.NaN));
            Assert.Throws<ArgumentOutOfRangeException>(() => LegacyGraphics.LightColor(999, float.PositiveInfinity, 1, 1));
            Assert.Throws<ArgumentOutOfRangeException>(() => LegacyGraphics.LightRange(999, 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => LegacyGraphics.LightConeAngles(999, float.NaN, 20));
        });
    }

    private static void WithFreshScene(Action action)
    {
        var previousScene = LegacyScene.Current;
        LegacyGraphics.ResetForTests();
        LegacyGraphics.ResetLightStateForTesting();
        LegacyScene.Current = new SceneGraph();
        try { action(); }
        finally
        {
            LegacyGraphics.ResetForTests();
            LegacyGraphics.ResetLightStateForTesting();
            LegacyScene.Current = previousScene;
        }
    }
}
