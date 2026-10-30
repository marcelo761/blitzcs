using System.Numerics;

namespace Blitz3DCS.Engine.Windows;

public static partial class LegacyGraphics
{
    private static readonly Dictionary<int, SpriteState> Sprites = [];
    private static MeshGeometry? _spriteQuadGeometry;

    /// <summary>Creates a world-space sprite with the legacy default free view mode.</summary>
    private static int CreateSpriteCore(int parent)
    {
        CheckThreadIfStarted();
        var scene = RequireScene();
        var id = scene.CreatePivot(parent);
        try
        {
            MeshEntity mesh;
            if (_spriteQuadGeometry is null)
            {
                mesh = new MeshEntity(id);
                MeshEntities.Add(id, mesh);
                var surface = CreateSurface(id);
                AddSpriteQuad(surface);
                _spriteQuadGeometry = mesh.Geometry;
            }
            else
            {
                _spriteQuadGeometry.References++;
                mesh = new MeshEntity(id, _spriteQuadGeometry);
                MeshEntities.Add(id, mesh);
            }

            mesh.Material.Effects = 1; // gxScene::FX_FULLBRIGHT
            Sprites.Add(id, new SpriteState());
            return id;
        }
        catch
        {
            if (MeshEntities.ContainsKey(id)) FreeEntity(id);
            else scene.FreeEntity(id);
            throw;
        }
    }

    /// <summary>Loads and binds a sprite texture, returning zero when the image cannot be loaded.</summary>
    public static int LoadSprite(string file, int textureFlags = 1, int parent = 0)
    {
        ArgumentException.ThrowIfNullOrEmpty(file);
        var texture = LoadTexture(file, textureFlags);
        if (texture == 0) return 0;
        var sprite = 0;
        try
        {
            sprite = CreateSprite(parent);
            EntityTexture(sprite, texture);
            EntityBlend(sprite, (textureFlags & 4) != 0 ? 0 : (textureFlags & 2) != 0 ? 1 : 3);
            return sprite;
        }
        catch
        {
            if (sprite != 0) FreeEntity(sprite);
            throw;
        }
        finally { FreeTexture(texture); }
    }

    public static void RotateSprite(int sprite, float angle)
    {
        CheckThreadIfStarted();
        ValidateFiniteColor(angle, nameof(angle));
        GetSprite(sprite).Rotation = angle;
    }

    public static void ScaleSprite(int sprite, float xScale, float yScale)
    {
        CheckThreadIfStarted();
        ValidateFiniteColor(xScale, nameof(xScale));
        ValidateFiniteColor(yScale, nameof(yScale));
        var state = GetSprite(sprite);
        state.XScale = xScale;
        state.YScale = yScale;
    }

    public static void HandleSprite(int sprite, float xHandle, float yHandle)
    {
        CheckThreadIfStarted();
        ValidateFiniteColor(xHandle, nameof(xHandle));
        ValidateFiniteColor(yHandle, nameof(yHandle));
        var state = GetSprite(sprite);
        state.XHandle = xHandle;
        state.YHandle = yHandle;
    }

    /// <summary>Selects the legacy billboard orientation mode (1 through 4).</summary>
    public static void SpriteViewMode(int sprite, int viewMode)
    {
        CheckThreadIfStarted();
        GetSprite(sprite).ViewMode = viewMode;
    }

    internal static void CopySpriteState(int source, int destination)
    {
        if (Sprites.TryGetValue(source, out var state)) Sprites.Add(destination, state.Copy());
    }

    internal static void ForgetSpriteState(int entity) => Sprites.Remove(entity);

    internal static void CloseSprites()
    {
        Sprites.Clear();
        _spriteQuadGeometry = null;
    }

    internal static Matrix4x4 BuildSpriteWorldMatrixForTesting(int sprite, Matrix4x4 entityWorld, Matrix4x4 cameraWorld) =>
        BuildSpriteWorldMatrix(GetSprite(sprite), entityWorld, cameraWorld);

    internal static (float XScale, float YScale, float Rotation, float XHandle, float YHandle, int ViewMode)
        GetSpriteSettingsForTesting(int sprite)
    {
        var state = GetSprite(sprite);
        return (state.XScale, state.YScale, state.Rotation, state.XHandle, state.YHandle, state.ViewMode);
    }

    private static Matrix4x4 BuildSpriteWorldMatrix(SpriteState sprite, Matrix4x4 entityWorld, Matrix4x4 cameraWorld)
    {
        var position = new Vector3(entityWorld.M41, entityWorld.M42, entityWorld.M43);
        var entityBasis = WithoutTranslation(entityWorld);
        Matrix4x4 orientation;
        switch (sprite.ViewMode)
        {
            case 1: // VIEW_MODE_FREE: camera-facing, ignoring entity rotation and scale.
                orientation = WithoutTranslation(cameraWorld);
                break;
            case 3: // VIEW_MODE_UPRIGHT: preserve world up and face the camera's forward axis.
            {
                var j = NormalizeOr(new Vector3(entityWorld.M21, entityWorld.M22, entityWorld.M23), Vector3.UnitY);
                var k = NormalizeOr(new Vector3(cameraWorld.M31, cameraWorld.M32, cameraWorld.M33), Vector3.UnitZ);
                var i = Vector3.Cross(j, k);
                if (i.LengthSquared() < 1e-12f)
                    i = Vector3.Cross(new Vector3(entityWorld.M11, entityWorld.M12, entityWorld.M13), k);
                i = NormalizeOr(i, Vector3.UnitX);
                j = Vector3.Cross(k, i);
                orientation = Basis(i, j, k);
                break;
            }
            case 4: // VIEW_MODE_UPRIGHT2: apply camera yaw while retaining the entity basis.
            {
                var forward = NormalizeOr(new Vector3(cameraWorld.M31, 0, cameraWorld.M33), Vector3.UnitZ);
                var yaw = Basis(new Vector3(forward.Z, 0, -forward.X), Vector3.UnitY, forward);
                orientation = entityBasis * yaw;
                break;
            }
            default: // VIEW_MODE_FIXED and unknown legacy values preserve the entity basis.
                orientation = entityBasis;
                break;
        }

        return Matrix4x4.CreateTranslation(-sprite.XHandle, -sprite.YHandle, 0) *
               Matrix4x4.CreateScale(sprite.XScale, sprite.YScale, 1) *
               Matrix4x4.CreateRotationZ(sprite.Rotation * (MathF.PI / 180f)) *
               orientation * Matrix4x4.CreateTranslation(position);
    }

    private static Matrix4x4 WithoutTranslation(Matrix4x4 matrix)
    {
        matrix.M41 = matrix.M42 = matrix.M43 = 0;
        return matrix;
    }

    private static Matrix4x4 Basis(Vector3 i, Vector3 j, Vector3 k) => new(
        i.X, i.Y, i.Z, 0,
        j.X, j.Y, j.Z, 0,
        k.X, k.Y, k.Z, 0,
        0, 0, 0, 1);

    private static Vector3 NormalizeOr(Vector3 value, Vector3 fallback) =>
        value.LengthSquared() < 1e-12f ? fallback : Vector3.Normalize(value);

    private static SpriteState GetSprite(int sprite) => Sprites.TryGetValue(sprite, out var state)
        ? state : throw new ArgumentException("The entity is not a sprite created by this runtime.", nameof(sprite));

    private static void AddSpriteQuad(int surface)
    {
        AddVertex(surface, -1, 1, 0, 0, 0);
        AddVertex(surface, 1, 1, 0, 1, 0);
        AddVertex(surface, 1, -1, 0, 1, 1);
        AddVertex(surface, -1, -1, 0, 0, 1);
        AddTriangle(surface, 0, 1, 2);
        AddTriangle(surface, 0, 2, 3);
    }

    private sealed class SpriteState
    {
        public float XScale { get; set; } = 1;
        public float YScale { get; set; } = 1;
        public float Rotation { get; set; }
        public float XHandle { get; set; }
        public float YHandle { get; set; }
        public int ViewMode { get; set; } = 1;

        public SpriteState Copy() => new()
        {
            XScale = XScale,
            YScale = YScale,
            Rotation = Rotation,
            XHandle = XHandle,
            YHandle = YHandle,
            ViewMode = ViewMode
        };
    }
}
