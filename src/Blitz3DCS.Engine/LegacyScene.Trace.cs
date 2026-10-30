using System.Globalization;

namespace Blitz3DCS.Engine;

// Diagnostic wrappers used only when EngineTrace.Hooked (BLITZCS_TRACE_ENTITIES / BLITZCS_ENGINE_PROFILE).
// They never swallow exceptions: the filter logs and returns false so the original exception propagates unchanged.
public static partial class LegacyScene
{
    private static int CreatePivotTraced(int parent)
    {
        var outer = EngineTrace.Enter(TraceCall.Other);
        try { var id = Current.CreatePivot(parent); if (outer) EngineTrace.Created("CreatePivot", id, "pivot", null, parent); return id; }
        catch (Exception e) when (EngineTrace.Failed("CreatePivot", e, $"parent={parent}")) { throw; }
        finally { EngineTrace.Exit(); }
    }

    private static int CopyEntityTraced(int entity, int parent)
    {
        var outer = EngineTrace.Enter(TraceCall.CopyEntity);
        try { var id = Current.CopyEntity(entity, parent); if (outer) EngineTrace.Copied(entity, id, parent); return id; }
        catch (Exception e) when (EngineTrace.Failed("CopyEntity", e, $"entity={entity} parent={parent}")) { throw; }
        finally { EngineTrace.Exit(); }
    }

    private static void FreeEntityTraced(int entity)
    {
        var outer = EngineTrace.Enter(TraceCall.FreeEntity);
        try { Current.FreeEntity(entity); if (outer) EngineTrace.Freed(entity); }
        catch (Exception e) when (EngineTrace.Failed("FreeEntity", e, $"entity={entity}")) { throw; }
        finally { EngineTrace.Exit(); }
    }

    private static void VisibilityTraced(int entity, bool show)
    {
        var name = show ? "ShowEntity" : "HideEntity";
        try { if (show) Current.ShowEntity(entity); else Current.HideEntity(entity); EngineTrace.Detail(name, entity, $"visible={(show ? 1 : 0)}"); }
        catch (Exception e) when (EngineTrace.Failed(name, e, $"entity={entity}")) { throw; }
    }

    private static void EntityTypeTraced(int entity, int type, int recursive)
    {
        try { Current.EntityType(entity, type, recursive != 0); EngineTrace.Detail("EntityType", entity, $"type={type} recursive={recursive}"); }
        catch (Exception e) when (EngineTrace.Failed("EntityType", e, $"entity={entity} type={type}")) { throw; }
    }

    private static void EntityRadiusTraced(int entity, float xRadius, float yRadius)
    {
        try { Current.EntityRadius(entity, xRadius, yRadius); EngineTrace.Detail("EntityRadius", entity, string.Create(CultureInfo.InvariantCulture, $"x={xRadius} y={yRadius}")); }
        catch (Exception e) when (EngineTrace.Failed("EntityRadius", e, $"entity={entity}")) { throw; }
    }

    private static void EntityBoxTraced(int entity, float x, float y, float z, float width, float height, float depth)
    {
        try { Current.EntityBox(entity, x, y, z, width, height, depth); EngineTrace.Detail("EntityBox", entity, string.Create(CultureInfo.InvariantCulture, $"box=({x},{y},{z}) size=({width},{height},{depth})")); }
        catch (Exception e) when (EngineTrace.Failed("EntityBox", e, $"entity={entity}")) { throw; }
    }

    private static void PositionEntityTraced(int entity, float x, float y, float z, int global)
    {
        try { Current.PositionEntity(entity, x, y, z, global != 0); EngineTrace.Positioned(entity, x, y, z); }
        catch (Exception e) when (EngineTrace.Failed("PositionEntity", e, $"entity={entity}")) { throw; }
    }

    private static void ScaleEntityTraced(int entity, float x, float y, float z, int global)
    {
        try { Current.ScaleEntity(entity, x, y, z, global != 0); EngineTrace.Detail("ScaleEntity", entity, string.Create(CultureInfo.InvariantCulture, $"scale=({x},{y},{z})")); }
        catch (Exception e) when (EngineTrace.Failed("ScaleEntity", e, $"entity={entity}")) { throw; }
    }
}
