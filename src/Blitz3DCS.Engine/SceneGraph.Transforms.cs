namespace Blitz3DCS.Engine;

public sealed partial class SceneGraph
{
    /// <summary>Disables and hides this entity, matching the legacy HideEntity command.</summary>
    public void HideEntity(int entity)
    {
        var node = GetNode(entity);
        node.Enabled = false;
        node.Visible = false;
        ResetCollisionState(entity);
    }

    /// <summary>Enables and shows this entity, matching the legacy ShowEntity command.</summary>
    public void ShowEntity(int entity)
    {
        var node = GetNode(entity);
        node.Visible = true;
        node.Enabled = true;
        ResetCollisionState(entity);
    }

    /// <summary>Returns whether the entity and every ancestor are enabled and visible.</summary>
    public bool IsVisibleInHierarchy(int entity)
    {
        for (Node? node = GetNode(entity); node is not null; node = node.Parent)
            if (!node.Visible || !node.Enabled) return false;
        return true;
    }

    /// <summary>Points an entity's forward axis at a target while applying the requested roll.</summary>
    public void PointEntity(int entity, int target, float roll = 0)
    {
        var node = GetNode(entity);
        var targetPosition = GetWorldTransform(GetNode(target)).Position;
        var position = GetWorldTransform(node).Position;
        var direction = Subtract(targetPosition, position);
        var rotation = Rotation.FromEulerDegrees(VectorPitch(direction.X, direction.Y, direction.Z),
            VectorYaw(direction.X, direction.Y, direction.Z), roll);
        SetWorldRotation(node, rotation);
    }

    /// <summary>Rotates an entity so the selected local axis moves toward a world-space vector.</summary>
    public void AlignToVector(int entity, float x, float y, float z, int axis, float rate = 1)
    {
        const float epsilon = 0.000001f;
        var desired = new SceneVector3(x, y, z);
        var length = Length(desired);
        if (length <= epsilon) return;
        desired = Multiply(desired, 1 / length);

        var node = GetNode(entity);
        var rotation = GetWorldRotation(node);
        var matrix = rotation.ToMatrix();
        var current = axis == 1 ? matrix.I : axis == 2 ? matrix.J : matrix.K;
        var dot = Dot(desired, current);
        if (dot >= 1 - epsilon) return;

        SceneVector3 cross;
        float angle;
        if (dot <= -1 + epsilon)
        {
            angle = MathF.PI * rate / 2;
            cross = axis == 1 ? matrix.J : axis == 2 ? matrix.K : matrix.I;
        }
        else
        {
            angle = MathF.Acos(dot) * rate / 2;
            cross = Normalize(Cross(desired, current));
        }

        var sine = MathF.Sin(angle);
        var adjustment = new Rotation(MathF.Cos(angle), cross.X * sine, cross.Y * sine, cross.Z * sine);
        SetWorldRotation(node, adjustment * rotation);
    }

    /// <summary>Transforms a point from source entity space to destination entity space; zero is identity.</summary>
    public SceneVector3 TransformPoint(SceneVector3 point, int source = 0, int destination = 0)
    {
        if (source != 0) point = GetWorldTransform(GetNode(source)).TransformPoint(point);
        if (destination != 0) point = GetWorldTransform(GetNode(destination)).InverseTransformPoint(point);
        return point;
    }

    /// <summary>Transforms a vector from source entity space to destination entity space; zero is identity.</summary>
    public SceneVector3 TransformVector(SceneVector3 vector, int source = 0, int destination = 0)
    {
        if (source != 0)
        {
            var transform = GetWorldTransform(GetNode(source));
            vector = new Matrix3(transform.BasisX, transform.BasisY, transform.BasisZ).Transform(vector);
        }
        if (destination != 0)
        {
            var transform = GetWorldTransform(GetNode(destination));
            vector = new Matrix3(transform.BasisX, transform.BasisY, transform.BasisZ).InverseTransform(vector);
        }
        return vector;
    }

    /// <summary>Transforms and normalizes a normal using the cofactor matrix convention.</summary>
    public SceneVector3 TransformNormal(SceneVector3 normal, int source = 0, int destination = 0)
    {
        if (source != 0) normal = CofactorTransform(GetWorldTransform(GetNode(source)), normal);
        if (destination != 0)
        {
            var world = GetWorldTransform(GetNode(destination));
            var matrix = new Matrix3(world.BasisX, world.BasisY, world.BasisZ);
            var inverse = new SceneTransform(
                matrix.InverseTransform(new SceneVector3(1, 0, 0)),
                matrix.InverseTransform(new SceneVector3(0, 1, 0)),
                matrix.InverseTransform(new SceneVector3(0, 0, 1)),
                default);
            normal = CofactorTransform(inverse, normal);
        }
        return Normalize(normal);
    }

    public float VectorYaw(float x, float y, float z) => -MathF.Atan2(x, z) * (180f / MathF.PI);
    public float VectorPitch(float x, float y, float z) => -MathF.Atan2(y, MathF.Sqrt(x * x + z * z)) * (180f / MathF.PI);

    public float DeltaYaw(int source, int destination) => DeltaAngle(
        VectorYaw(GetWorldTransform(GetNode(source)).BasisZ.X, GetWorldTransform(GetNode(source)).BasisZ.Y, GetWorldTransform(GetNode(source)).BasisZ.Z),
        VectorYaw(GetWorldTransform(GetNode(destination)).Position.X - GetWorldTransform(GetNode(source)).Position.X,
            GetWorldTransform(GetNode(destination)).Position.Y - GetWorldTransform(GetNode(source)).Position.Y,
            GetWorldTransform(GetNode(destination)).Position.Z - GetWorldTransform(GetNode(source)).Position.Z));

    public float DeltaPitch(int source, int destination) => DeltaAngle(
        VectorPitch(GetWorldTransform(GetNode(source)).BasisZ.X, GetWorldTransform(GetNode(source)).BasisZ.Y, GetWorldTransform(GetNode(source)).BasisZ.Z),
        VectorPitch(GetWorldTransform(GetNode(destination)).Position.X - GetWorldTransform(GetNode(source)).Position.X,
            GetWorldTransform(GetNode(destination)).Position.Y - GetWorldTransform(GetNode(source)).Position.Y,
            GetWorldTransform(GetNode(destination)).Position.Z - GetWorldTransform(GetNode(source)).Position.Z));

    private static SceneVector3 CofactorTransform(SceneTransform transform, SceneVector3 value)
    {
        var x = Cross(transform.BasisY, transform.BasisZ);
        var y = Cross(transform.BasisZ, transform.BasisX);
        var z = Cross(transform.BasisX, transform.BasisY);
        return Add(Add(Multiply(x, value.X), Multiply(y, value.Y)), Multiply(z, value.Z));
    }

    private static float DeltaAngle(float source, float destination)
    {
        var delta = destination - source;
        if (delta < -180f) delta += 360f;
        else if (delta >= 180f) delta -= 360f;
        return delta;
    }
}
