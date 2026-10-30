using System.Numerics;
using System.Runtime.InteropServices;
using Blitz3DCS.Engine;
using Blitz3DCS.Graphics.D3D11;
using Blitz3DCS.Windowing;

namespace Blitz3DCS.Engine.Windows;

/// <summary>Windows BASIC graphics commands backed by SDL and the native D3D11 renderer.</summary>
public static partial class LegacyGraphics
{
    private static readonly Dictionary<int, MeshEntity> MeshEntities = [];
    private static readonly Dictionary<int, CameraState> Cameras = [];
    private static readonly Dictionary<int, SurfaceState> Surfaces = [];
    private static readonly D3D11TextureLayer[] RenderLayers = new D3D11TextureLayer[8];
    private static D3D11Renderer? _renderer;
    private static SceneGraph? _scene;
    private static int _nextSurfaceHandle = -1;
    private static int _threadId;
    private static int _width;
    private static int _height;
    private static (int R, int G, int B) _clsColor;

    /// <summary>Opens a windowed D3D11 display. Only 32-bit color and windowed mode are supported.</summary>
    public static void Graphics3D(int width, int height, int depth = 0, int mode = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        if (depth is not (0 or 32)) throw new NotSupportedException("The D3D11 backend currently supports 32-bit color only.");
        if (mode is not (0 or 2)) throw new NotSupportedException("The D3D11 backend supports only the default windowed mode and explicit windowed mode 2.");

        if (_renderer is not null) EndGraphics();
        _scene = LegacyScene.Current;
        _threadId = Environment.CurrentManagedThreadId;
        try
        {
            _renderer = new D3D11Renderer(width, height, InitialWindowTitle,
                ApplicationWindowBootstrap.EnsureApplicationWindow(InitialWindowTitle));
            _width = width;
            _height = height;
            foreach (var camera in Cameras.Values) camera.Viewport = null;
        }
        catch
        {
            _threadId = 0;
            throw;
        }
    }

    /// <summary>Closes the display and releases renderer resources; scene entities and CPU geometry remain alive.</summary>
    public static void EndGraphics()
    {
        if (EngineProfile.Enabled) EngineProfile.Flush();
        CheckThreadIfStarted();
        BlitzMovie_Close();
        ClosePlaneGpuMeshes();
        if (_renderer is null) return;
        CloseAnimatedGpuMeshes();
        CloseDrawing();
        CloseFonts();
        CloseMaterials();
        CloseTextureBuffers();
        CloseImages();
        foreach (var surface in Surfaces.Values)
        {
            surface.GpuMesh = null;
            surface.Dirty = true;
        }
        _renderer.Dispose();
        _renderer = null;
        _wireframe = false;
        _width = 0;
        _height = 0;
        _threadId = 0;
    }

    public static void ClsColor(int red, int green, int blue)
    {
        ValidateRgb(red, green, blue);
        _clsColor = (red, green, blue);
    }

    public static void Cls()
    {
        ClearSelectedBuffer(_clsColor.R / 255f, _clsColor.G / 255f, _clsColor.B / 255f);
    }

    public static void Flip(int sync = 1)
    {
        EnsureNoBufferLocked();
        var profileStart = EngineProfile.Enabled ? EngineProfile.Now() : 0;
        RequireRenderer().Present(sync != 0);
        if (EngineProfile.Enabled) { EngineProfile.Add(EngineProfile.Present, profileStart); EngineProfile.EndFrame(); }
        if (EngineTrace.Entities || EngineTrace.Collision) TraceFrame();
    }

    public static int GraphicsWidth() => _renderer is null ? 0 : _width;
    public static int GraphicsHeight() => _renderer is null ? 0 : _height;

    /// <summary>Creates a camera entity in the shared <see cref="LegacyScene.Current"/> graph.</summary>
    public static int CreateCamera(int parent = 0)
    {
        var scene = RequireScene();
        var id = scene.CreatePivot(parent);
        Cameras.Add(id, new CameraState(id, _clsColor));
        return id;
    }

    /// <summary>Creates a mesh entity in the shared <see cref="LegacyScene.Current"/> graph.</summary>
    private static int CreateMeshCore(int parent)
    {
        var scene = RequireScene();
        var id = scene.CreatePivot(parent);
        MeshEntities.Add(id, new MeshEntity(id));
        return id;
    }

    /// <summary>Creates a six-face colored cube with independent face normals and UVs.</summary>
    public static int CreateCube(int parent = 0)
    {
        var mesh = CreateMesh(parent);
        var surface = CreateSurface(mesh);
        var (vertices, indices) = BuildCubeGeometry();
        foreach (var vertex in vertices)
        {
            var index = AddVertex(surface, vertex.Position.X, vertex.Position.Y, vertex.Position.Z,
                vertex.TexCoord.X, vertex.TexCoord.Y);
            VertexColor(surface, index, vertex.Color.X * 255, vertex.Color.Y * 255, vertex.Color.Z * 255, vertex.Color.W);
            VertexNormal(surface, index, vertex.Normal.X, vertex.Normal.Y, vertex.Normal.Z);
        }
        for (var i = 0; i < indices.Length; i += 3)
            AddTriangle(surface, checked((int)indices[i]), checked((int)indices[i + 1]), checked((int)indices[i + 2]));
        return mesh;
    }

    /// <summary>Releases graphics-owned surfaces and then frees the entity subtree in the shared scene.</summary>
    private static void FreeEntityCore(int entity)
    {
        CheckThreadIfStarted();
        if (entity == 0) return;
        ClearSceneDraws();
        var scene = RequireScene();
        var pending = new Stack<int>();
        var subtree = new List<int>();
        pending.Push(entity);
        while (pending.TryPop(out var current))
        {
            subtree.Add(current);
            for (var index = 1; index <= scene.CountChildren(current); index++)
                pending.Push(scene.GetChild(current, index));
        }

        if (subtree.Any(MeshEntities.ContainsKey)) ClearCollisionGeometrySelectionCache();
        ReleaseAnimatedModels(subtree);
        foreach (var current in subtree)
        {
            ForgetSpriteState(current);
            ForgetLightState(current);
            Cameras.Remove(current);
            if (!MeshEntities.Remove(current, out var mesh)) continue;
            mesh.Plane?.GpuMesh?.Dispose();
            mesh.Material.Dispose();
            if (--mesh.Geometry.References != 0) continue;
            if (ReferenceEquals(mesh.Geometry, _spriteQuadGeometry)) _spriteQuadGeometry = null;
            foreach (var surfaceHandle in mesh.SurfaceHandles)
            {
                var surface = Surfaces[surfaceHandle];
                surface.GpuMesh?.Dispose();
                surface.Material.Dispose();
                Surfaces.Remove(surfaceHandle);
                SurfaceState.MutationEpoch++;
            }
        }
        scene.FreeEntity(entity);
    }

    /// <summary>Creates an empty surface attached to a graphics mesh entity.</summary>
    public static int CreateSurface(int mesh, int brush = 0)
    {
        CheckThreadIfStarted();
        if (!MeshEntities.TryGetValue(mesh, out var owner))
            throw new ArgumentException("The entity is not a graphics mesh created by this runtime.", nameof(mesh));
        if (_nextSurfaceHandle == int.MinValue) throw new InvalidOperationException("Surface handle space is exhausted.");
        var material = brush == 0 ? new EntityMaterial() : GetBrush(brush).Copy();
        var handle = _nextSurfaceHandle--;
        try
        {
            var surface = new SurfaceState(handle, mesh) { Material = material, Geometry = owner.Geometry };
            Surfaces.Add(handle, surface);
            owner.SurfaceHandles.Add(handle);
            SurfaceState.MutationEpoch++;
            MarkGeometryChanged(owner.Geometry);
            return handle;
        }
        catch
        {
            material.Dispose();
            throw;
        }
    }

    /// <summary>Adds a vertex and returns its zero-based surface index.</summary>
    public static int AddVertex(int surface, float x, float y, float z, float u = 0, float v = 0, float w = 1)
    {
        CheckThreadIfStarted();
        var state = GetSurface(surface);
        var index = state.Vertices.Count;
        // The legacy surface stores U/V in both sets and ignores the W argument.
        var uv = new Vector2(u, v);
        state.Vertices.Add(new D3D11Vertex(new Vector3(x, y, z), Vector4.One, Vector3.UnitZ, uv, uv));
        state.Dirty = true;
        return index;
    }

    public static int AddTriangle(int surface, int vertex0, int vertex1, int vertex2)
    {
        CheckThreadIfStarted();
        var state = GetSurface(surface);
        ValidateVertexIndex(state, vertex0);
        ValidateVertexIndex(state, vertex1);
        ValidateVertexIndex(state, vertex2);
        state.Indices.Add((uint)vertex0);
        state.Indices.Add((uint)vertex1);
        state.Indices.Add((uint)vertex2);
        state.Dirty = true;
        return state.Indices.Count / 3 - 1;
    }

    public static void VertexCoords(int surface, int vertex, float x, float y, float z)
    {
        CheckThreadIfStarted();
        var state = GetSurface(surface);
        var old = GetVertex(state, vertex);
        state.Vertices[vertex] = new D3D11Vertex(new Vector3(x, y, z), old.Color, old.Normal, old.TexCoord, old.TexCoord2);
        state.Dirty = true;
    }

    public static void VertexColor(int surface, int vertex, float red, float green, float blue, float alpha = 1)
    {
        CheckThreadIfStarted();
        ValidateFiniteColor(red, nameof(red));
        ValidateFiniteColor(green, nameof(green));
        ValidateFiniteColor(blue, nameof(blue));
        ValidateFiniteColor(alpha, nameof(alpha));
        var state = GetSurface(surface);
        var old = GetVertex(state, vertex);
        state.Vertices[vertex] = new D3D11Vertex(old.Position,
            new Vector4(ToNormalizedByte(red), ToNormalizedByte(green), ToNormalizedByte(blue), ToNormalizedAlpha(alpha)),
            old.Normal, old.TexCoord, old.TexCoord2);
        state.Dirty = true;
    }

    public static void VertexTexCoords(int surface, int vertex, float u, float v, float w = 1, int coordSet = 0)
    {
        CheckThreadIfStarted();
        if ((uint)coordSet > 1) throw new ArgumentOutOfRangeException(nameof(coordSet));
        var state = GetSurface(surface);
        var old = GetVertex(state, vertex);
        state.Vertices[vertex] = new D3D11Vertex(old.Position, old.Color, old.Normal,
            coordSet == 0 ? new Vector2(u, v) : old.TexCoord,
            coordSet == 1 ? new Vector2(u, v) : old.TexCoord2);
        state.Dirty = true;
    }

    public static int CountVertices(int surface) => GetSurface(surface).Vertices.Count;
    public static int CountTriangles(int surface) => GetSurface(surface).Indices.Count / 3;

    public static int CountSurfaces(int mesh) => GetMesh(mesh).SurfaceHandles.Count;

    /// <summary>Gets the one-based surface at <paramref name="index"/>.</summary>
    public static int GetSurface(int mesh, int index)
    {
        var handles = GetMesh(mesh).SurfaceHandles;
        if (index < 1 || index > handles.Count) throw new ArgumentOutOfRangeException(nameof(index));
        return handles[index - 1];
    }

    public static void CameraClsColor(int camera, float red, float green, float blue)
    {
        CheckThreadIfStarted();
        ValidateFiniteColor(red, nameof(red));
        ValidateFiniteColor(green, nameof(green));
        ValidateFiniteColor(blue, nameof(blue));
        GetCamera(camera).ClearColor = (red / 255f, green / 255f, blue / 255f);
    }

    public static void CameraRange(int camera, float near, float far)
    {
        // Original Camera::setRange stores both values unvalidated; degenerate ranges (SCP:CB's first
        // frame uses far = 0) only yield a degenerate projection matrix, never an error.
        var state = GetCamera(camera);
        state.Near = near;
        state.Far = far;
    }

    /// <summary>Sets camera projection: 0 disables it, 1 uses perspective, 2 uses orthographic projection.</summary>
    public static void CameraProjMode(int camera, int mode) => GetCamera(camera).ProjectionMode = mode;

    /// <summary>Sets the camera zoom factor. The legacy default is 1.</summary>
    public static void CameraZoom(int camera, float zoom) => GetCamera(camera).Zoom = zoom;

    public static void CameraViewport(int camera, int x, int y, int width, int height)
    {
        CheckThreadIfStarted();
        var state = GetCamera(camera);
        if (_renderer is not null) D3D11Renderer.ValidateViewport(x, y, width, height, _width, _height);
        else
        {
            ArgumentOutOfRangeException.ThrowIfNegative(x);
            ArgumentOutOfRangeException.ThrowIfNegative(y);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        }
        state.Viewport = (x, y, width, height);
    }

    internal static void EnsureSingleCameraForTesting() => EnsureSingleCamera();

    internal static void ValidateViewportForTesting(int x, int y, int width, int height, int targetWidth, int targetHeight) =>
        D3D11Renderer.ValidateViewport(x, y, width, height, targetWidth, targetHeight);

    /// <summary>Clears and draws every graphics mesh using the scene's sole camera.</summary>
    public static void RenderWorld(float tween = 1)
    {
        var profileStart = EngineProfile.Enabled ? EngineProfile.Now() : 0;
        try { RenderWorldCore(tween); }
        finally { if (EngineProfile.Enabled) EngineProfile.Add(EngineProfile.RenderWorld, profileStart); }
    }

    private static void RenderWorldCore(float tween)
    {
        CheckThreadIfStarted();
        EnsureNoBufferLocked();
        if (GraphicsBuffer() != BackBuffer())
            throw new NotSupportedException("Rendering a 3D world into a texture buffer is not implemented; select BackBuffer first.");
        var renderer = RequireRenderer();
        var scene = RequireScene();
        EnsureSingleCamera();
        var camera = Cameras.Values.FirstOrDefault(state => state.ProjectionMode != 0 && scene.IsVisibleInHierarchy(state.Entity));
        if (camera is null) return;
        if (EngineTrace.Entities) _lastRenderCamera = camera.Entity;
        var viewport = camera.Viewport ?? (0, 0, _width, _height);
        D3D11Renderer.ValidateViewport(viewport.X, viewport.Y, viewport.Width, viewport.Height, _width, _height);
        renderer.Clear(camera.ClearColor.R, camera.ClearColor.G, camera.ClearColor.B, camera.ClearColorEnabled, camera.ClearDepthEnabled);
        renderer.SetViewport(viewport.X, viewport.Y, viewport.Width, viewport.Height);

        var cameraWorld = ToWorldMatrix(scene.GetWorldTransform(camera.Entity));
        if (!Matrix4x4.Invert(cameraWorld, out var view))
            throw new InvalidOperationException("The camera transform is not invertible.");
        var projection = CreateCameraProjection(camera, viewport.Width, viewport.Height);
        var skinStart = EngineProfile.Enabled ? EngineProfile.Now() : 0;
        PrepareAnimatedGeometry();
        if (EngineProfile.Enabled) EngineProfile.Add(EngineProfile.Skinning, skinStart);
        if (EngineProfile.Enabled && EngineProfile.CensusDue()) EngineProfile.Emit(BuildSceneCensus(scene));
        var lightStart = EngineProfile.Enabled ? EngineProfile.Now() : 0;
        Span<LegacyLightSnapshot> sourceLights = stackalloc LegacyLightSnapshot[8];
        Span<D3D11Light> lights = stackalloc D3D11Light[8];
        var lightCount = CopyVisibleLightsForRendering(sourceLights);
        for (var index = 0; index < lightCount; index++)
        {
            var light = sourceLights[index];
            lights[index] = new D3D11Light(light.Type, light.Position, light.Direction, light.Color,
                light.Range, light.LinearAttenuation, light.InnerConeRadians, light.OuterConeRadians);
        }
        renderer.SetSceneLighting(AmbientLightForRendering, new Vector3(cameraWorld.M41, cameraWorld.M42, cameraWorld.M43), lights[..lightCount]);
        if (EngineProfile.Enabled)
        {
            EngineProfile.AddSince(EngineProfile.RtLights, lightStart);
            EngineProfile.Count(EngineProfile.RtLightCount, lightCount);
        }

        DrawSceneMeshes(renderer, scene, camera, cameraWorld, view, projection);
    }

    internal static Matrix4x4 ToWorldMatrix(SceneTransform transform)
    {
        return new Matrix4x4(
            transform.BasisX.X, transform.BasisX.Y, transform.BasisX.Z, 0,
            transform.BasisY.X, transform.BasisY.Y, transform.BasisY.Z, 0,
            transform.BasisZ.X, transform.BasisZ.Y, transform.BasisZ.Z, 0,
            transform.Position.X, transform.Position.Y, transform.Position.Z, 1);
    }

    internal static Matrix4x4 CreateLeftHandedPerspective(float verticalFov, float aspect, float near, float far)
    {
        if (!float.IsFinite(verticalFov) || verticalFov <= 0 || verticalFov >= MathF.PI)
            throw new ArgumentOutOfRangeException(nameof(verticalFov));
        if (!float.IsFinite(aspect) || aspect <= 0) throw new ArgumentOutOfRangeException(nameof(aspect));
        if (!float.IsFinite(near) || near <= 0) throw new ArgumentOutOfRangeException(nameof(near));
        if (!float.IsFinite(far) || far <= near) throw new ArgumentOutOfRangeException(nameof(far));

        var yScale = 1f / MathF.Tan(verticalFov * 0.5f);
        var xScale = yScale / aspect;
        var depthScale = far / (far - near);
        return new Matrix4x4(
            xScale, 0, 0, 0,
            0, yScale, 0, 0,
            0, 0, depthScale, 1,
            0, 0, -near * depthScale, 0);
    }

    private static Matrix4x4 CreateCameraProjection(CameraState camera, int viewportWidth, int viewportHeight)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(viewportWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(viewportHeight);
        if (camera.ProjectionMode == 0) throw new InvalidOperationException("A disabled camera has no projection matrix.");
        if (!float.IsFinite(camera.Zoom)) throw new ArgumentOutOfRangeException(nameof(camera.Zoom));

        var aspect = (float)viewportWidth / viewportHeight;
        if (camera.ProjectionMode == 2)
        {
            return CreateLeftHandedOrthographicFromScales(
                camera.Zoom / camera.Near,
                camera.Zoom * aspect / camera.Near,
                camera.Near,
                camera.Far);
        }
        return CreateLeftHandedPerspectiveFromScales(camera.Zoom, camera.Zoom * aspect, camera.Near, camera.Far);
    }

    private static Matrix4x4 CreateLeftHandedPerspectiveFromScales(float xScale, float yScale, float near, float far)
    {
        var depthScale = far / (far - near);
        return new Matrix4x4(
            xScale, 0, 0, 0,
            0, yScale, 0, 0,
            0, 0, depthScale, 1,
            0, 0, -near * depthScale, 0);
    }

    private static Matrix4x4 CreateLeftHandedOrthographicFromScales(float xScale, float yScale, float near, float far)
    {
        var depthScale = 1 / (far - near);
        return new Matrix4x4(
            xScale, 0, 0, 0,
            0, yScale, 0, 0,
            0, 0, depthScale, 0,
            0, 0, -near * depthScale, 1);
    }

    internal static (D3D11Vertex[] Vertices, uint[] Indices) BuildCubeGeometry()
    {
        var vertices = new D3D11Vertex[24];
        var indices = new uint[36];
        var vertexOffset = 0;
        var indexOffset = 0;

        void AddFace(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 normal, Vector4 color)
        {
            var start = (uint)vertexOffset;
            vertices[vertexOffset++] = new D3D11Vertex(a, color, normal, new Vector2(0, 0));
            vertices[vertexOffset++] = new D3D11Vertex(b, color, normal, new Vector2(0, 1));
            vertices[vertexOffset++] = new D3D11Vertex(c, color, normal, new Vector2(1, 1));
            vertices[vertexOffset++] = new D3D11Vertex(d, color, normal, new Vector2(1, 0));
            indices[indexOffset++] = start;
            indices[indexOffset++] = start + 1;
            indices[indexOffset++] = start + 2;
            indices[indexOffset++] = start;
            indices[indexOffset++] = start + 2;
            indices[indexOffset++] = start + 3;
        }

        AddFace(new(-1, -1, -1), new(-1, 1, -1), new(1, 1, -1), new(1, -1, -1), new(0, 0, -1), new(1, 0, 0, 1));
        AddFace(new(1, -1, 1), new(1, 1, 1), new(-1, 1, 1), new(-1, -1, 1), new(0, 0, 1), new(0, 1, 0, 1));
        AddFace(new(-1, -1, 1), new(-1, 1, 1), new(-1, 1, -1), new(-1, -1, -1), new(-1, 0, 0), new(0, 0, 1, 1));
        AddFace(new(1, -1, -1), new(1, 1, -1), new(1, 1, 1), new(1, -1, 1), new(1, 0, 0), new(1, 1, 0, 1));
        AddFace(new(-1, -1, 1), new(-1, -1, -1), new(1, -1, -1), new(1, -1, 1), new(0, -1, 0), new(1, 0, 1, 1));
        AddFace(new(-1, 1, -1), new(-1, 1, 1), new(1, 1, 1), new(1, 1, -1), new(0, 1, 0), new(0, 1, 1, 1));
        return (vertices, indices);
    }

    internal static Rgba32 ReadPixelForTesting(int x, int y) => RequireRenderer().ReadPixelForTesting(x, y);

    internal static D3D11Vertex GetVertexForTesting(int surface, int vertex) => GetVertex(GetSurface(surface), vertex);

    internal static (float R, float G, float B) GetCameraColorForTesting(int camera) => GetCamera(camera).ClearColor;

    internal static Matrix4x4 GetCameraProjectionForTesting(int camera, int width, int height) => CreateCameraProjection(GetCamera(camera), width, height);

    internal static int GetCameraProjectionModeForTesting(int camera) => GetCamera(camera).ProjectionMode;

    internal static float GetCameraZoomForTesting(int camera) => GetCamera(camera).Zoom;

    internal static bool CameraEnabledForTesting(int camera) => GetCamera(camera).ProjectionMode != 0;

    internal static int GetActiveCameraForTesting() =>
        Cameras.Values.SingleOrDefault(state => state.ProjectionMode != 0 && RequireScene().IsVisibleInHierarchy(state.Entity))?.Entity ?? 0;

    internal static bool IsSurfaceDirtyForTesting(int surface) => GetSurface(surface).Dirty;

    internal static void ResetForTests()
    {
        ClearCollisionGeometrySelectionCache();
        ClearSceneDraws();
        RenderTraversal.Clear();
        _trianglesRendered = 0;
        EndGraphics();
        CloseSprites();
        CloseDrawing();
        CloseFonts();
        CloseMaterials();
        CloseTextureBuffers();
        CloseImages();
        CloseAnimatedGpuMeshes();
        AnimatedModels.Clear();
        ResetLightStateForTesting();
        MeshEntities.Clear();
        Cameras.Clear();
        Surfaces.Clear();
        _scene = null;
        _nextSurfaceHandle = -1;
        _clsColor = default;
        _projectedPoint = default;
    }

    private static void EnsureGpuMesh(D3D11Renderer renderer, SurfaceState surface)
    {
        if (!surface.Dirty && surface.GpuMesh is not null) return;
        var replacement = renderer.CreateMesh(CollectionsMarshal.AsSpan(surface.Vertices), CollectionsMarshal.AsSpan(surface.Indices));
        var previous = surface.GpuMesh;
        surface.GpuMesh = replacement;
        surface.Dirty = false;
        previous?.Dispose();
    }

    private static void EnsureSingleCamera()
    {
        if (Cameras.Values.Count(state => state.ProjectionMode != 0 && RequireScene().IsVisibleInHierarchy(state.Entity)) > 1)
            throw new NotSupportedException("This graphics slice supports one active camera.");
    }

    private static SceneGraph RequireScene()
    {
        var active = LegacyScene.Current;
        if (_scene is null) _scene = active;
        else if (!ReferenceEquals(_scene, active))
            throw new InvalidOperationException("LegacyScene.Current changed while graphics entities were alive; free the graphics entities before replacing the scene.");
        return active;
    }

    private static void CheckThreadIfStarted()
    {
        if (_threadId != 0 && Environment.CurrentManagedThreadId != _threadId)
            throw new InvalidOperationException("Graphics commands must stay on the display's creating thread.");
    }

    private static D3D11Renderer RequireRenderer() => _renderer ?? throw new InvalidOperationException("Call Graphics3D before using the display.");
    private static MeshEntity GetMesh(int mesh) => MeshEntities.TryGetValue(mesh, out var state)
        ? state
        : throw new ArgumentException("The entity is not a graphics mesh created by this runtime.", nameof(mesh));
    private static SurfaceState GetSurface(int surface) => Surfaces.TryGetValue(surface, out var state)
        ? state
        : throw new ArgumentException("The handle is not a graphics surface.", nameof(surface));
    private static CameraState GetCamera(int camera) => Cameras.TryGetValue(camera, out var state)
        ? state
        : throw new ArgumentException("The entity is not a camera created by this runtime.", nameof(camera));

    private static D3D11Vertex GetVertex(SurfaceState surface, int vertex)
    {
        if ((uint)vertex >= (uint)surface.Vertices.Count) throw new ArgumentOutOfRangeException(nameof(vertex));
        return surface.Vertices[vertex];
    }

    private static void ValidateVertexIndex(SurfaceState surface, int vertex)
    {
        if ((uint)vertex >= (uint)surface.Vertices.Count) throw new ArgumentOutOfRangeException(nameof(vertex));
    }

    private static void ValidateRgb(int red, int green, int blue)
    {
        if (red is < 0 or > 255) throw new ArgumentOutOfRangeException(nameof(red));
        if (green is < 0 or > 255) throw new ArgumentOutOfRangeException(nameof(green));
        if (blue is < 0 or > 255) throw new ArgumentOutOfRangeException(nameof(blue));
    }

    private static void ValidateFiniteColor(float value, string parameter)
    {
        if (!float.IsFinite(value)) throw new ArgumentOutOfRangeException(parameter);
    }

    private static float ToNormalizedByte(float value) => MathF.Truncate(Math.Clamp(value, 0, 255)) / 255f;

    private static float ToNormalizedAlpha(float value) => MathF.Truncate(Math.Clamp(value * 255, 0, 255)) / 255f;

    private sealed class MeshEntity(int entity, MeshGeometry? geometry = null)
    {
        public int Entity { get; } = entity;
        public MeshGeometry Geometry { get; } = geometry ?? new MeshGeometry();
        public EntityMaterial Material { get; set; } = new();
        public AnimatedMesh? Animated { get; set; }
        public PlaneState? Plane { get; set; }
        public List<int> SurfaceHandles => Geometry.SurfaceHandles;
        public long CollisionVersion { get; set; } = -1;
        public int Order { get; set; }
        public (float Near, float Far)? AutoFade { get; set; }
    }

    private sealed class MeshGeometry
    {
        public (Vector3 Minimum, Vector3 Maximum)? CullBox { get; set; }
        public int References { get; set; } = 1;
        public List<int> SurfaceHandles { get; } = [];
        public long CollisionVersion { get; set; } = -1;
        /// <summary>Member of the maintained collision-relevant set (owned by PrepareCollisionGeometry).</summary>
        public bool InCollisionSet { get; set; }
        /// <summary>Queued for re-preparation because one of its surfaces changed since the last prepare.</summary>
        public bool CollisionPending { get; set; }
        public long CachedVersion { get; set; }
        public long CachedVersionEpoch { get; set; } = -1;
        public SceneCollisionTriangle[] CollisionTriangles { get; set; } = [];
        public long BoundsVersion { get; set; } = -1;
        public Vector3 BoundsMinimum { get; set; }
        public Vector3 BoundsMaximum { get; set; }
        public bool HasBounds { get; set; }
    }

    private sealed class SurfaceState(int handle, int ownerMesh)
    {
        public int Handle { get; } = handle;
        public int OwnerMesh { get; } = ownerMesh;
        /// <summary>Owning geometry; surface edits notify only this geometry's collision state.</summary>
        public MeshGeometry? Geometry { get; init; }
        public List<D3D11Vertex> Vertices { get; } = [];
        public List<uint> Indices { get; } = [];
        public EntityMaterial Material { get; set; } = new();
        public D3D11Mesh? GpuMesh { get; set; }
        private bool _dirty = true;
        public long Version { get; private set; }
        /// <summary>Bumped on any surface content change or surface add/remove; lets per-frame version scans be skipped.</summary>
        internal static long MutationEpoch;
        public bool Dirty
        {
            get => _dirty;
            set { _dirty = value; if (value) { Version++; MutationEpoch++; if (Geometry is { InCollisionSet: true } g) MarkGeometryChanged(g); } }
        }
    }

    private sealed class CameraState(int entity, (int R, int G, int B) clearColor)
    {
        public int Entity { get; } = entity;
        public (float R, float G, float B) ClearColor { get; set; } =
            (clearColor.R / 255f, clearColor.G / 255f, clearColor.B / 255f);
        public float Near { get; set; } = 1f;
        public float Far { get; set; } = 1000f;
        public int ProjectionMode { get; set; } = 1;
        public float Zoom { get; set; } = 1;
        public bool ClearColorEnabled { get; set; } = true;
        public bool ClearDepthEnabled { get; set; } = true;
        public (int X, int Y, int Width, int Height)? Viewport { get; set; }
    }
}
