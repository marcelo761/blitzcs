using Blitz3DCS.Engine;
using Blitz3DCS.Engine.Windows;
using Xunit;
using Xunit.Sdk;

namespace Blitz3DCS.Engine.Windows.Tests;

[Collection("LegacyGraphics static state")]
public sealed class LegacyGraphicsAnimatedModelWindowsSmokeTests
{
    [WindowsModelSmokeFact]
    public void Animated_b3d_models_render_frame_changes_and_independent_copies()
    {
        RunSmoke();
    }

    internal static void RunSmoke()
    {
        var previousScene = LegacyScene.Current;
        var fixture = Path.Combine(Path.GetTempPath(), $"blitz-model-smoke-{Guid.NewGuid():N}.b3d");
        LegacyGraphics.ResetForTests();
        LegacyScene.Current = new SceneGraph();
        try
        {
            File.WriteAllBytes(fixture, LegacyGraphicsAnimatedModelTests.AnimatedModelBytes());
            LegacyGraphics.Graphics3D(320, 240);
            var camera = LegacyGraphics.CreateCamera();
            LegacyScene.PositionEntity(camera, 0, 0, 5);
            LegacyScene.RotateEntity(camera, 0, 180, 0);
            LegacyGraphics.CameraRange(camera, 0.1f, 100);
            LegacyGraphics.CameraClsColor(camera, 0, 0, 0);

            var parent = LegacyScene.CreatePivot();
            LegacyScene.PositionEntity(parent, -3, 0, 0);
            LegacyScene.ScaleEntity(parent, 0.1f, 0.1f, 0.1f);
            var root = LegacyGraphics.LoadAnimMesh(fixture, parent);
            Assert.NotEqual(0, root);
            Assert.True(LegacyGraphics.AnimLength(root) >= 3, "The synthetic B3D fixture must contain multiple animation frames.");

            LegacyGraphics.SetAnimTime(root, 0);
            LegacyGraphics.RenderWorld();
            var frameZero = LegacyGraphics.ReadBackbufferPixelsForTesting();
            Assert.True(CountNonBlack(frameZero) > 0, "The frame-zero mesh must produce native pixels.");

            LegacyGraphics.SetAnimTime(root, 0.5f);
            LegacyGraphics.RenderWorld();
            var middleFrame = LegacyGraphics.ReadBackbufferPixelsForTesting();
            Assert.True(CountNonBlack(middleFrame) > 0, "The midpoint mesh must produce native pixels.");
            Assert.False(frameZero.AsSpan().SequenceEqual(middleFrame), "Changing animation time must change the rendered image.");

            var copy = LegacyGraphics.CopyEntity(root, parent);
            LegacyGraphics.SetAnimTime(root, 0);
            LegacyGraphics.SetAnimTime(copy, 0.5f);
            LegacyScene.PositionEntity(copy, LegacyScene.EntityX(root, 1) + 0.5f, 0, 0, global: 1);
            LegacyGraphics.RenderWorld();
            var twoActors = LegacyGraphics.ReadBackbufferPixelsForTesting();
            Assert.True(CountNonBlack(twoActors) > CountNonBlack(frameZero), "The independently animated copy must add visible pixels.");

            var sourceSurface = Assert.Single(Assert.Single(LegacyGraphics.AnimatedModels[root].Meshes).Surfaces);
            var copySurface = Assert.Single(Assert.Single(LegacyGraphics.AnimatedModels[copy].Meshes).Surfaces);
            Assert.NotNull(sourceSurface.GpuMesh);
            Assert.NotNull(copySurface.GpuMesh);
            Assert.NotSame(sourceSurface.GpuMesh, copySurface.GpuMesh);

            if (Environment.GetEnvironmentVariable("BLITZ3D_B3D_VALIDATION_ASSET") is { Length: > 0 } actualAsset)
            {
                LegacyGraphics.FreeEntity(parent);
                LegacyScene.PositionEntity(camera, 0, 0, -5);
                LegacyScene.RotateEntity(camera, 0, 0, 0);
                RunActualAssetSmoke(actualAsset);
            }
        }
        finally
        {
            LegacyGraphics.EndGraphics();
            LegacyGraphics.ResetForTests();
            LegacyScene.Current = previousScene;
            File.Delete(fixture);
        }
    }

    private static void RunActualAssetSmoke(string path)
    {
        var root = LegacyGraphics.LoadAnimMesh(path);
        Assert.NotEqual(0, root);
        Assert.Equal(2, LegacyGraphics.AnimatedModels[root].Model.Textures.Count);
        Assert.True(LegacyGraphics.GetAnimatedTextureBindingCountForTesting(root) > 0,
            "At least one embedded SCP939 texture must load from the model directory.");
        var length = LegacyGraphics.AnimLength(root);
        Assert.True(length > 1, $"The validation asset must have an animated sequence; reported length {length}.");

        LegacyGraphics.SetAnimTime(root, 0);
        LegacyGraphics.RenderWorld();
        var firstFrame = LegacyGraphics.ReadBackbufferPixelsForTesting();
        LegacyGraphics.SetAnimTime(root, length / 2f);
        LegacyGraphics.RenderWorld();
        var middleFrame = LegacyGraphics.ReadBackbufferPixelsForTesting();
        Assert.True(CountNonBlack(firstFrame) > 0, "SCP939 frame zero must produce native pixels.");
        Assert.True(CountNonBlack(middleFrame) > 0, "SCP939 midpoint must produce native pixels.");
        Assert.False(firstFrame.AsSpan().SequenceEqual(middleFrame), "SCP939 animation must change the rendered image.");

        var copy = LegacyGraphics.CopyEntity(root);
        LegacyGraphics.SetAnimTime(root, 0);
        LegacyGraphics.SetAnimTime(copy, length / 2f);
        LegacyGraphics.RenderWorld();
        var actors = LegacyGraphics.ReadBackbufferPixelsForTesting();
        Assert.True(CountNonBlack(actors) > CountNonBlack(firstFrame), "The SCP939 copy must contribute an independently animated actor.");
        var sourceMeshes = LegacyGraphics.AnimatedModels[root].Meshes;
        var copyMeshes = LegacyGraphics.AnimatedModels[copy].Meshes;
        Assert.Equal(sourceMeshes.Count, copyMeshes.Count);
        for (var meshIndex = 0; meshIndex < sourceMeshes.Count; meshIndex++)
        {
            var sourceSurfaces = sourceMeshes[meshIndex].Surfaces;
            var copySurfaces = copyMeshes[meshIndex].Surfaces;
            Assert.Equal(sourceSurfaces.Count, copySurfaces.Count);
            for (var surfaceIndex = 0; surfaceIndex < sourceSurfaces.Count; surfaceIndex++)
            {
                Assert.NotNull(sourceSurfaces[surfaceIndex].GpuMesh);
                Assert.NotNull(copySurfaces[surfaceIndex].GpuMesh);
                Assert.NotSame(sourceSurfaces[surfaceIndex].GpuMesh, copySurfaces[surfaceIndex].GpuMesh);
            }
        }
    }

    private static int CountNonBlack(ReadOnlySpan<byte> rgba)
    {
        var count = 0;
        for (var offset = 0; offset < rgba.Length; offset += 4)
            if (rgba[offset] != 0 || rgba[offset + 1] != 0 || rgba[offset + 2] != 0)
                count++;
        return count;
    }

    private sealed class WindowsModelSmokeFactAttribute : FactAttribute
    {
        public WindowsModelSmokeFactAttribute()
        {
            if (!OperatingSystem.IsWindows()) Skip = "D3D11 is a Windows-only runtime.";
            else if (Environment.GetEnvironmentVariable("BLITZCS_RUN_D3D11_SMOKE") != "1")
                Skip = "Set BLITZCS_RUN_D3D11_SMOKE=1 to run the real device/window smoke test.";
        }
    }
}
