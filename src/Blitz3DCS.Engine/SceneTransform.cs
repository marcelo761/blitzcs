namespace Blitz3DCS.Engine;

/// <summary>A 3D vector used by the engine's public transform snapshot.</summary>
public readonly record struct SceneVector3(float X, float Y, float Z);

/// <summary>
/// An affine transform whose three basis vectors are columns, matching the legacy Blitz3D
/// transform convention. The position is transformed as a point; basis vectors exclude it.
/// </summary>
public readonly record struct SceneTransform(
    SceneVector3 BasisX,
    SceneVector3 BasisY,
    SceneVector3 BasisZ,
    SceneVector3 Position)
{
    public SceneVector3 TransformPoint(SceneVector3 point) => new(
        BasisX.X * point.X + BasisY.X * point.Y + BasisZ.X * point.Z + Position.X,
        BasisX.Y * point.X + BasisY.Y * point.Y + BasisZ.Y * point.Z + Position.Y,
        BasisX.Z * point.X + BasisY.Z * point.Y + BasisZ.Z * point.Z + Position.Z);

    public SceneVector3 InverseTransformPoint(SceneVector3 point)
    {
        var value = new SceneVector3(point.X - Position.X, point.Y - Position.Y, point.Z - Position.Z);
        var crossYZ = Cross(BasisY, BasisZ);
        var crossZX = Cross(BasisZ, BasisX);
        var crossXY = Cross(BasisX, BasisY);
        var determinant = Dot(BasisX, crossYZ);
        return new(Dot(value, crossYZ) / determinant, Dot(value, crossZX) / determinant, Dot(value, crossXY) / determinant);
    }

    internal SceneTransform InverseTransform(SceneTransform transform) => new(
        InverseTransformVector(transform.BasisX),
        InverseTransformVector(transform.BasisY),
        InverseTransformVector(transform.BasisZ),
        InverseTransformPoint(transform.Position));

    private SceneVector3 InverseTransformVector(SceneVector3 value)
    {
        var crossYZ = Cross(BasisY, BasisZ);
        var crossZX = Cross(BasisZ, BasisX);
        var crossXY = Cross(BasisX, BasisY);
        var determinant = Dot(BasisX, crossYZ);
        return new(Dot(value, crossYZ) / determinant, Dot(value, crossZX) / determinant, Dot(value, crossXY) / determinant);
    }

    private static float Dot(SceneVector3 a, SceneVector3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
    private static SceneVector3 Cross(SceneVector3 a, SceneVector3 b) => new(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
}
