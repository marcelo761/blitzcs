using System.Numerics;
using Blitz3DCS.Engine;
using Blitz3DCS.Engine.Windows;
using Xunit;

namespace Blitz3DCS.Engine.Windows.Tests;

[Collection("LegacyGraphics static state")]
public sealed class LegacyGraphicsContractTests
{
    [Fact]
    public void Camera_viewport_is_checked_and_multiple_cameras_are_rejected()
    {
        var previousScene = LegacyScene.Current;
        LegacyGraphics.ResetForTests();
        LegacyScene.Current = new SceneGraph();
        try
        {
            var first = LegacyGraphics.CreateCamera();
            var second = LegacyGraphics.CreateCamera();
            Assert.Throws<NotSupportedException>(() => LegacyGraphics.EnsureSingleCameraForTesting());
            LegacyScene.HideEntity(first);
            LegacyGraphics.EnsureSingleCameraForTesting();
            Assert.Equal(second, LegacyGraphics.GetActiveCameraForTesting());
            LegacyScene.ShowEntity(first);
            Assert.Throws<NotSupportedException>(() => LegacyGraphics.EnsureSingleCameraForTesting());
            LegacyGraphics.CameraProjMode(first, 0);
            LegacyGraphics.EnsureSingleCameraForTesting();
            Assert.Equal(second, LegacyGraphics.GetActiveCameraForTesting());
            LegacyGraphics.CameraProjMode(second, 0);
            Assert.Equal(0, LegacyGraphics.GetActiveCameraForTesting());
            Assert.Throws<ArgumentOutOfRangeException>(() => LegacyGraphics.ValidateViewportForTesting(-1, 0, 10, 10, 100, 100));
            Assert.Throws<ArgumentOutOfRangeException>(() => LegacyGraphics.ValidateViewportForTesting(90, 0, 11, 10, 100, 100));
            Assert.True(first > 0);
            Assert.True(second > first);
        }
        finally
        {
            LegacyGraphics.ResetForTests();
            LegacyScene.Current = previousScene;
        }
    }

    [Fact]
    public void Left_handed_projection_maps_positive_z_near_and_far_planes()
    {
        var projection = LegacyGraphics.CreateLeftHandedPerspective(MathF.PI / 2, 4f / 3f, 0.5f, 40f);
        var near = Vector4.Transform(new Vector4(0, 0, 0.5f, 1), projection);
        var far = Vector4.Transform(new Vector4(0, 0, 40, 1), projection);

        Assert.Equal(0, near.Z / near.W, 5);
        Assert.Equal(1, far.Z / far.W, 5);
        Assert.True(near.W > 0);
        Assert.True(far.W > 0);
    }

    [Fact]
    public void Camera_projection_mode_and_zoom_follow_legacy_frustum_dimensions()
    {
        var previousScene = LegacyScene.Current;
        LegacyGraphics.ResetForTests();
        LegacyScene.Current = new SceneGraph();
        try
        {
            var camera = LegacyGraphics.CreateCamera();
            LegacyGraphics.CameraRange(camera, 0.5f, 40f);
            Assert.Equal(1, LegacyGraphics.GetCameraProjectionModeForTesting(camera));
            Assert.Equal(1f, LegacyGraphics.GetCameraZoomForTesting(camera));

            var perspective = LegacyGraphics.GetCameraProjectionForTesting(camera, 800, 600);
            Assert.Equal(1f, perspective.M11, 5);
            Assert.Equal(4f / 3f, perspective.M22, 5);
            Assert.Equal(1f, perspective.M34, 5);

            LegacyGraphics.CameraZoom(camera, 2f);
            var zoomedPerspective = LegacyGraphics.GetCameraProjectionForTesting(camera, 800, 600);
            Assert.Equal(2f, zoomedPerspective.M11, 5);
            Assert.Equal(8f / 3f, zoomedPerspective.M22, 5);

            LegacyGraphics.CameraZoom(camera, 0f);
            var zeroZoom = LegacyGraphics.GetCameraProjectionForTesting(camera, 800, 600);
            Assert.Equal(0f, zeroZoom.M11);
            Assert.Equal(0f, zeroZoom.M22);
            LegacyGraphics.CameraZoom(camera, 2f);

            LegacyGraphics.CameraProjMode(camera, 2);
            var orthographic = LegacyGraphics.GetCameraProjectionForTesting(camera, 800, 600);
            Assert.Equal(4f, orthographic.M11, 5);
            Assert.Equal(16f / 3f, orthographic.M22, 5);
            Assert.Equal(0f, orthographic.M34);
            Assert.Equal(1f, orthographic.M44);

            LegacyGraphics.CameraProjMode(camera, 0);
            Assert.False(LegacyGraphics.CameraEnabledForTesting(camera));
            Assert.Equal(0, LegacyGraphics.GetCameraProjectionModeForTesting(camera));
        }
        finally
        {
            LegacyGraphics.ResetForTests();
            LegacyScene.Current = previousScene;
        }
    }

    [Fact]
    public void Camera_fog_defaults_and_linear_settings_match_legacy_contract()
    {
        var previousScene = LegacyScene.Current;
        LegacyGraphics.ResetForTests();
        LegacyScene.Current = new SceneGraph();
        try
        {
            var camera = LegacyGraphics.CreateCamera();
            Assert.Equal((0, Vector3.Zero, 1f, 1000f), LegacyGraphics.GetCameraFogForTesting(camera));

            LegacyGraphics.CameraFogColor(camera, 64, 128, 255);
            LegacyGraphics.CameraFogRange(camera, 12, 12);
            LegacyGraphics.CameraFogMode(camera, 1);
            var fog = LegacyGraphics.GetCameraFogForTesting(camera);
            Assert.Equal(1, fog.Mode);
            Assert.Equal(new Vector3(64, 128, 255) / 255f, fog.Color);
            Assert.Equal(12f, fog.Start);
            Assert.Equal(12f, fog.End);
            Assert.NotNull(LegacyGraphics.GetCameraFogForRendering(camera));

            LegacyGraphics.CameraFogMode(camera, 2);
            Assert.Null(LegacyGraphics.GetCameraFogForRendering(camera));
            Assert.Throws<ArgumentOutOfRangeException>(() => LegacyGraphics.CameraFogColor(camera, -1, 0, 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => LegacyGraphics.CameraFogRange(camera, float.NaN, 1));
        }
        finally
        {
            LegacyGraphics.ResetForTests();
            LegacyScene.Current = previousScene;
        }
    }

    [Fact]
    public void Scene_transform_basis_maps_points_through_system_numerics_world_matrix()
    {
        var scene = new SceneGraph();
        var entity = scene.CreatePivot();
        scene.PositionEntity(entity, 4, 5, 6);
        scene.RotateEntity(entity, 0, 90, 0);
        scene.ScaleEntity(entity, 2, 3, 4);

        var matrix = LegacyGraphics.ToWorldMatrix(scene.GetWorldTransform(entity));
        var transform = scene.GetWorldTransform(entity);
        var transformedOrigin = Vector3.Transform(Vector3.Zero, matrix);
        var transformedX = Vector3.Transform(Vector3.UnitX, matrix);

        Assert.Equal(new Vector3(4, 5, 6), transformedOrigin);
        var expectedX = new Vector3(
            transform.BasisX.X + transform.Position.X,
            transform.BasisX.Y + transform.Position.Y,
            transform.BasisX.Z + transform.Position.Z);
        Assert.True(Vector3.Distance(expectedX, transformedX) < 0.0001f);
    }

    [Fact]
    public void Cube_geometry_is_a_six_face_colored_mesh()
    {
        var (vertices, indices) = LegacyGraphics.BuildCubeGeometry();

        Assert.Equal(24, vertices.Length);
        Assert.Equal(36, indices.Length);
        Assert.Equal(6, vertices.Select(v => v.Color).Distinct().Count());
        Assert.All(indices, index => Assert.InRange(index, 0u, 23u));
    }

    [Fact]
    public void Surface_handles_are_distinct_from_positive_scene_entity_ids_and_cache_mutations()
    {
        var previousScene = LegacyScene.Current;
        LegacyGraphics.ResetForTests();
        LegacyScene.Current = new SceneGraph();
        try
        {
            var mesh = LegacyGraphics.CreateMesh();
            var surface = LegacyGraphics.CreateSurface(mesh);
            Assert.True(mesh > 0);
            Assert.True(surface < 0);
            Assert.Equal(1, LegacyGraphics.CountSurfaces(mesh));
            Assert.Equal(surface, LegacyGraphics.GetSurface(mesh, 1));
            Assert.Equal(0, LegacyGraphics.AddVertex(surface, 0, 0, 0));
            Assert.Equal(1, LegacyGraphics.AddVertex(surface, 1, 0, 0));
            Assert.Equal(2, LegacyGraphics.AddVertex(surface, 0, 1, 0));
            Assert.Equal(0, LegacyGraphics.AddTriangle(surface, 0, 1, 2));
            Assert.Throws<ArgumentException>(() => LegacyGraphics.CreateSurface(mesh, brush: 1));
            Assert.Equal(3, LegacyGraphics.CountVertices(surface));
            Assert.Equal(1, LegacyGraphics.CountTriangles(surface));
            Assert.True(LegacyGraphics.IsSurfaceDirtyForTesting(surface));
        }
        finally
        {
            LegacyGraphics.ResetForTests();
            LegacyScene.Current = previousScene;
        }
    }

    [Fact]
    public void Vertex_color_uses_legacy_byte_rgb_and_normalized_alpha_semantics()
    {
        var previousScene = LegacyScene.Current;
        LegacyGraphics.ResetForTests();
        LegacyScene.Current = new SceneGraph();
        try
        {
            var mesh = LegacyGraphics.CreateMesh();
            var surface = LegacyGraphics.CreateSurface(mesh);
            var vertex = LegacyGraphics.AddVertex(surface, 0, 0, 0);

            LegacyGraphics.VertexColor(surface, vertex, 128, 64, 300, 0.5f);

            var color = LegacyGraphics.GetVertexForTesting(surface, vertex).Color;
            Assert.Equal(128f / 255, color.X, 5);
            Assert.Equal(64f / 255, color.Y, 5);
            Assert.Equal(1, color.Z);
            Assert.Equal(127f / 255, color.W, 5);
        }
        finally
        {
            LegacyGraphics.ResetForTests();
            LegacyScene.Current = previousScene;
        }
    }

    [Fact]
    public void Legacy_command_defaults_and_camera_clear_color_scale_are_preserved()
    {
        Assert.Equal(0, typeof(LegacyGraphics).GetMethod(nameof(LegacyGraphics.Graphics3D))!.GetParameters()[2].DefaultValue);
        Assert.Equal(0, typeof(LegacyGraphics).GetMethod(nameof(LegacyGraphics.CreateSurface))!.GetParameters()[1].DefaultValue);
        Assert.Equal(1f, typeof(LegacyGraphics).GetMethod(nameof(LegacyGraphics.AddVertex))!.GetParameters()[6].DefaultValue);

        var previousScene = LegacyScene.Current;
        LegacyGraphics.ResetForTests();
        LegacyScene.Current = new SceneGraph();
        try
        {
            var camera = LegacyGraphics.CreateCamera();
            LegacyGraphics.CameraClsColor(camera, 128.5f, 64, 255);

            Assert.Equal((128.5f / 255, 64f / 255, 1f), LegacyGraphics.GetCameraColorForTesting(camera));
        }
        finally
        {
            LegacyGraphics.ResetForTests();
            LegacyScene.Current = previousScene;
        }
    }
}
