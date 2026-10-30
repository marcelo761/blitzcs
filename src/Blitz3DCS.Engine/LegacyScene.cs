namespace Blitz3DCS.Engine;

/// <summary>Static command-shaped facade for BASIC bindings that operate on the active scene.</summary>
public static partial class LegacyScene
{
    private static SceneGraph _current = new();
    private static SceneVector3 _tformed;

    public static SceneGraph Current
    {
        get => Volatile.Read(ref _current);
        set => Volatile.Write(ref _current, value ?? throw new ArgumentNullException(nameof(value)));
    }

    public static int CreatePivot(int parent = 0) => EngineTrace.Hooked ? CreatePivotTraced(parent) : Current.CreatePivot(parent);
    public static int CopyEntity(int entity, int parent = 0) => EngineTrace.Hooked ? CopyEntityTraced(entity, parent) : Current.CopyEntity(entity, parent);
    public static void FreeEntity(int entity) { if (EngineTrace.Hooked) FreeEntityTraced(entity); else Current.FreeEntity(entity); }
    public static void HideEntity(int entity) { if (EngineTrace.Hooked) VisibilityTraced(entity, false); else Current.HideEntity(entity); }
    public static void ShowEntity(int entity) { if (EngineTrace.Hooked) VisibilityTraced(entity, true); else Current.ShowEntity(entity); }
    public static void EntityType(int entity, int type, int recursive = 0) { if (EngineTrace.Hooked) EntityTypeTraced(entity, type, recursive); else Current.EntityType(entity, type, recursive != 0); }
    public static int GetEntityType(int entity) => Current.GetEntityType(entity);
    public static void ResetEntity(int entity) => Current.ResetEntity(entity);
    public static void EntityRadius(int entity, float xRadius, float yRadius = 0) { if (EngineTrace.Hooked) EntityRadiusTraced(entity, xRadius, yRadius); else Current.EntityRadius(entity, xRadius, yRadius); }
    public static void EntityBox(int entity, float x, float y, float z, float width, float height, float depth) { if (EngineTrace.Hooked) EntityBoxTraced(entity, x, y, z, width, height, depth); else Current.EntityBox(entity, x, y, z, width, height, depth); }
    public static void Collisions(int sourceType, int destinationType, int method, int response) => Current.Collisions(sourceType, destinationType, method, response);
    public static void ClearCollisions() => Current.ClearCollisions();
    public static void UpdateWorld(float elapsed = 1) => Current.UpdateWorld(elapsed);
    public static int EntityCollided(int entity, int collisionType) => Current.EntityCollided(entity, collisionType);
    public static int CountCollisions(int entity) => Current.CountCollisions(entity);
    public static int CollisionEntity(int entity, int index) => Current.CollisionEntity(entity, index);
    public static float CollisionX(int entity, int index) => Current.CollisionX(entity, index);
    public static float CollisionY(int entity, int index) => Current.CollisionY(entity, index);
    public static float CollisionZ(int entity, int index) => Current.CollisionZ(entity, index);
    public static float CollisionNX(int entity, int index) => Current.CollisionNX(entity, index);
    public static float CollisionNY(int entity, int index) => Current.CollisionNY(entity, index);
    public static float CollisionNZ(int entity, int index) => Current.CollisionNZ(entity, index);
    public static float CollisionTime(int entity, int index) => Current.CollisionTime(entity, index);
    public static int CollisionTriangle(int entity, int index) => Current.CollisionTriangle(entity, index);
    public static int CollisionSurface(int entity, int index) => Current.CollisionSurface(entity, index);
    public static void EntityPickMode(int entity, int mode, int obscurer = 1) => Current.EntityPickMode(entity, mode, obscurer != 0);
    public static int EntityPick(int entity, float range) => Current.EntityPick(entity, range);
    public static int LinePick(float x, float y, float z, float dx, float dy, float dz, float radius = 0) =>
        Current.LinePick(x, y, z, dx, dy, dz, radius);
    public static int EntityVisible(int source, int destination) => Current.EntityVisible(source, destination) ? 1 : 0;
    public static int PickedEntity() => Current.PickedEntity;
    public static float PickedX() => Current.PickedX;
    public static float PickedY() => Current.PickedY;
    public static float PickedZ() => Current.PickedZ;
    public static float PickedNX() => Current.PickedNX;
    public static float PickedNY() => Current.PickedNY;
    public static float PickedNZ() => Current.PickedNZ;
    public static void PositionEntity(int entity, float x, float y, float z, int global = 0) { if (EngineTrace.Hooked) PositionEntityTraced(entity, x, y, z, global); else Current.PositionEntity(entity, x, y, z, global != 0); }
    public static void TranslateEntity(int entity, float x, float y, float z, int global = 0) => Current.TranslateEntity(entity, x, y, z, global != 0);
    public static void ScaleEntity(int entity, float x, float y, float z, int global = 0) { if (EngineTrace.Hooked) ScaleEntityTraced(entity, x, y, z, global); else Current.ScaleEntity(entity, x, y, z, global != 0); }
    public static void RotateEntity(int entity, float pitch, float yaw, float roll, int global = 0) => Current.RotateEntity(entity, pitch, yaw, roll, global != 0);
    public static void MoveEntity(int entity, float x, float y, float z) => Current.MoveEntity(entity, x, y, z);
    public static void TurnEntity(int entity, float pitch, float yaw, float roll, int global = 0) => Current.TurnEntity(entity, pitch, yaw, roll, global != 0);
    public static float EntityX(int entity, int global = 0) => Current.EntityX(entity, global != 0);
    public static float EntityY(int entity, int global = 0) => Current.EntityY(entity, global != 0);
    public static float EntityZ(int entity, int global = 0) => Current.EntityZ(entity, global != 0);
    public static float EntityPitch(int entity, int global = 0) => Current.EntityPitch(entity, global != 0);
    public static float EntityYaw(int entity, int global = 0) => Current.EntityYaw(entity, global != 0);
    public static float EntityRoll(int entity, int global = 0) => Current.EntityRoll(entity, global != 0);
    public static float EntityScaleX(int entity, int global = 0) => Current.EntityScaleX(entity, global != 0);
    public static float EntityScaleY(int entity, int global = 0) => Current.EntityScaleY(entity, global != 0);
    public static float EntityScaleZ(int entity, int global = 0) => Current.EntityScaleZ(entity, global != 0);
    public static void EntityParent(int entity, int parent, int global = 1) => Current.EntityParent(entity, parent, global != 0);
    public static int GetParent(int entity) => Current.GetParent(entity);
    public static int CountChildren(int entity) => Current.CountChildren(entity);
    public static int GetChild(int entity, int index) => Current.GetChild(entity, index);
    public static void NameEntity(int entity, string name) => Current.NameEntity(entity, name);
    public static string EntityName(int entity) => Current.EntityName(entity);
    public static int FindChild(int entity, string name) => Current.FindChild(entity, name);
    public static float EntityDistance(int source, int destination) => Current.EntityDistance(source, destination);

    public static void TFormPoint(float x, float y, float z, int source, int destination) =>
        _tformed = Current.TransformPoint(new SceneVector3(x, y, z), source, destination);
    public static void TFormVector(float x, float y, float z, int source, int destination) =>
        _tformed = Current.TransformVector(new SceneVector3(x, y, z), source, destination);
    public static void TFormNormal(float x, float y, float z, int source, int destination) =>
        _tformed = Current.TransformNormal(new SceneVector3(x, y, z), source, destination);
    public static float TFormedX() => _tformed.X;
    public static float TFormedY() => _tformed.Y;
    public static float TFormedZ() => _tformed.Z;
    public static float VectorYaw(float x, float y, float z) => Current.VectorYaw(x, y, z);
    public static float VectorPitch(float x, float y, float z) => Current.VectorPitch(x, y, z);
    public static float DeltaYaw(int source, int destination) => Current.DeltaYaw(source, destination);
    public static float DeltaPitch(int source, int destination) => Current.DeltaPitch(source, destination);
    public static void PointEntity(int entity, int target, float roll = 0) => Current.PointEntity(entity, target, roll);
    public static void AlignToVector(int entity, float x, float y, float z, int axis, float rate = 1) => Current.AlignToVector(entity, x, y, z, axis, rate);
}
