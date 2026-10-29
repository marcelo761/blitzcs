namespace Blitz3DCS.Engine;

/// <summary>A snapshot of an entity's local position, scale, and scalar-first rotation quaternion.</summary>
public readonly record struct SceneLocalPose(
    SceneVector3 Position,
    SceneVector3 Scale,
    float RotationW,
    float RotationX,
    float RotationY,
    float RotationZ);

public sealed partial class SceneGraph
{
    /// <summary>Returns the local pose without converting its quaternion through Euler angles.</summary>
    public SceneLocalPose GetLocalPose(int entity)
    {
        var node = GetNode(entity);
        var rotation = node.LocalRotation;
        return new SceneLocalPose(node.LocalPosition, node.LocalScale, rotation.W, rotation.X, rotation.Y, rotation.Z);
    }

    /// <summary>
    /// Sets position, scale, and a scalar-first quaternion in local space, then invalidates the
    /// entity's cached transforms and those of its descendants.
    /// </summary>
    public void SetLocalPose(
        int entity,
        SceneVector3 position,
        SceneVector3 scale,
        float rotationW,
        float rotationX,
        float rotationY,
        float rotationZ)
    {
        var node = GetNode(entity);
        node.LocalPosition = position;
        node.LocalScale = scale;
        node.LocalRotation = new Rotation(rotationW, rotationX, rotationY, rotationZ).Normalized();
        InvalidateLocal(node);
    }

    /// <summary>Applies only animation channels that have keys in the active sequence.</summary>
    public void ApplyAnimationPose(int entity, B3dPoseNode pose)
    {
        var node = GetNode(entity);
        var changed = false;
        if (pose.HasPositionKeys)
        {
            node.LocalPosition = new SceneVector3(pose.LocalPosition.X, pose.LocalPosition.Y, pose.LocalPosition.Z);
            changed = true;
        }
        if (pose.HasScaleKeys)
        {
            node.LocalScale = new SceneVector3(pose.LocalScale.X, pose.LocalScale.Y, pose.LocalScale.Z);
            changed = true;
        }
        if (pose.HasRotationKeys)
        {
            node.LocalRotation = new Rotation(pose.LocalRotation.W, pose.LocalRotation.X,
                pose.LocalRotation.Y, pose.LocalRotation.Z).Normalized();
            changed = true;
        }
        if (changed) InvalidateLocal(node);
    }
}
