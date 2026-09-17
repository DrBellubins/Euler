using System.Numerics;
using Euler.Utils;
using Raymath = Raylib_cs.Raymath;

namespace Euler.GameEngine;

/// <summary>
/// The scene's directional light (the sun) - a 3D object that owns ALL the
/// scene's directional lighting. The <see cref="Raymarcher"/> uploads it per
/// frame (see <see cref="WriteInto"/>); the shader lights every shape family
/// with it (see <c>Assets/Shaders/Raymarcher/Lighting.inc</c>), including the
/// ray-traced hard-shadow pass (the shadow rays trace through the wormhole
/// space, so light can bend around the throat and geometry on the other
/// sheet casts shadows on this one).
///
/// The light's direction is its local -Y axis: at the identity rotation the
/// sun shines straight down. <see cref="Direction"/> is the direction the
/// light TRAVELS (world space); <see cref="ToSun"/> (its negation) is what
/// the shader's NdotL uses.
///
/// ROTATION MODEL: the rotation is stored and composed in QUATERNION space
/// (<see cref="System.Numerics.Quaternion"/>). Euler angles only enter and
/// exit at the API boundary through <c>Raymath.QuaternionFromEuler</c> and
/// <c>Raymath.QuaternionToEuler</c> (the raylib pitch/yaw/roll convention:
/// pitch = X, yaw = Y, roll = Z, radians internally - the API takes and
/// returns DEGREES). Every incremental operation (RotateBy,
/// RotateByAxisAngle, SetLightDirection) converts to a quaternion and does
/// the math there, so orientations are composed by quaternion multiplication
/// and can never gimbal-lock the way cumulative Euler storage would.
/// </summary>
public class Sun
{
    /// <summary>The local axis the light travels along (the sun's "forward").</summary>
    public static readonly Vector3 LocalLightAxis = new(0f, -1f, 0f);

    /// <summary>World-space position of the sun (directional lights are at
    /// infinity, but the sun is a 3D object: it can be moved in xyz).</summary>
    public Vector3 Position { get; private set; }

    /// <summary>Sun diffuse intensity (scales the sun's color contribution).</summary>
    public float Intensity { get; set; } = 1f;

    /// <summary>Sun color (linear RGB, white = neutral).</summary>
    public Vector3 Color { get; set; } = Vector3.One;

    private Quaternion _rotation = Quaternion.Identity;

    /// <summary>Creates a sun at <paramref name="position"/> shining straight
    /// down (identity rotation).</summary>
    public Sun(Vector3 position)
    {
        Position = position;
    }

    /// <summary>
    /// Creates a sun at <paramref name="position"/> with the given rotation
    /// as euler angles in DEGREES (pitch = X, yaw = Y, roll = Z, raylib
    /// convention). Converted to the internal quaternion immediately (see the
    /// class docs for the rotation model).
    /// </summary>
    public Sun(Vector3 position, Vector3 eulerDegrees)
    {
        Position = position;
        SetRotation(eulerDegrees);
    }

    // ------------------------------------------------------------------
    // Position (3D object: movable in xyz)
    // ------------------------------------------------------------------

    /// <summary>Moves the sun to <paramref name="position"/>.</summary>
    public void MoveTo(Vector3 position) => Position = position;

    /// <summary>Displaces the sun by <paramref name="delta"/>.</summary>
    public void MoveBy(Vector3 delta) => Position += delta;

    // ------------------------------------------------------------------
    // Rotation (quaternion space; Euler only at the API boundary)
    // ------------------------------------------------------------------

    /// <summary>The current rotation (quaternion).</summary>
    public Quaternion Rotation => _rotation;

    /// <summary>
    /// The current rotation as euler angles in DEGREES (pitch = X, yaw = Y,
    /// roll = Z) - the <c>QuaternionToEuler</c> conversion for consumers that
    /// need euler form (raylib, debug readouts).
    /// </summary>
    public Vector3 EulerAnglesDegrees
    {
        get
        {
            Vector3 euler = Raymath.QuaternionToEuler(_rotation);   // radians
            return new Vector3(
                GMath.ToDegrees(euler.X),
                GMath.ToDegrees(euler.Y),
                GMath.ToDegrees(euler.Z));
        }
    }

    /// <summary>
    /// Sets the rotation from euler angles in DEGREES (pitch = X, yaw = Y,
    /// roll = Z) via <c>Raymath.QuaternionFromEuler</c>.
    /// </summary>
    public void SetRotation(Vector3 eulerDegrees)
    {
        Vector3 euler = ToRadians(eulerDegrees);
        _rotation = Raymath.QuaternionNormalize(
            Raymath.QuaternionFromEuler(euler.X, euler.Y, euler.Z));
    }

    /// <summary>
    /// Applies an incremental euler rotation in DEGREES (pitch = X, yaw = Y,
    /// roll = Z) IN WORLD SPACE: the delta is converted to a quaternion with
    /// <c>Raymath.QuaternionFromEuler</c> and multiplied onto the current
    /// rotation (quaternion math - no euler accumulation, no gimbal lock).
    /// </summary>
    public void RotateBy(Vector3 eulerDeltaDegrees)
    {
        Vector3 euler = ToRadians(eulerDeltaDegrees);
        Quaternion delta = Raymath.QuaternionFromEuler(euler.X, euler.Y, euler.Z);
        _rotation = Raymath.QuaternionNormalize(Raymath.QuaternionMultiply(delta, _rotation));
    }

    /// <summary>
    /// Applies a rotation of <paramref name="angleDegrees"/> around
    /// <paramref name="axis"/> IN WORLD SPACE (quaternion math).
    /// </summary>
    public void RotateByAxisAngle(Vector3 axis, float angleDegrees)
    {
        Quaternion delta = Raymath.QuaternionFromAxisAngle(axis, GMath.ToRadians(angleDegrees));
        _rotation = Raymath.QuaternionNormalize(Raymath.QuaternionMultiply(delta, _rotation));
    }

    /// <summary>
    /// Orients the sun so its light travels along <paramref name="direction"/>
    /// (world space): the quaternion that rotates the local light axis (local
    /// -Y) onto the requested direction.
    /// </summary>
    public void SetLightDirection(Vector3 direction)
    {
        direction = Vector3.Normalize(direction);
        _rotation = Raymath.QuaternionNormalize(
            Raymath.QuaternionFromVector3ToVector3(LocalLightAxis, direction));
    }

    // ------------------------------------------------------------------
    // Light properties
    // ------------------------------------------------------------------

    /// <summary>
    /// The direction the light TRAVELS (unit vector, world space): the local
    /// light axis (local -Y) rotated by <see cref="Rotation"/>.
    /// </summary>
    public Vector3 Direction =>
        Raymath.Vector3RotateByQuaternion(LocalLightAxis, _rotation);

    /// <summary>
    /// Direction from a surface TOWARD the sun (unit vector) - what the
    /// shader's NdotL and shadow rays use.
    /// </summary>
    public Vector3 ToSun => Vector3.Normalize(-Direction);

    // ------------------------------------------------------------------
    // Shader upload
    // ------------------------------------------------------------------

    /// <summary>
    /// Packs this sun into <paramref name="data"/> as two vec4s matching the
    /// shader's SunData layout (Raymarcher/Lighting.inc):
    /// <code>[toSun.xyz, intensity] [color.rgb, 0]</code>
    /// </summary>
    public void WriteInto(Span<float> data)
    {
        Vector3 toSun = ToSun;
        data[0] = toSun.X;
        data[1] = toSun.Y;
        data[2] = toSun.Z;
        data[3] = Intensity;
        data[4] = Color.X;
        data[5] = Color.Y;
        data[6] = Color.Z;
        data[7] = 0f;
    }

    private static Vector3 ToRadians(Vector3 degrees) => new(
        GMath.ToRadians(degrees.X),
        GMath.ToRadians(degrees.Y),
        GMath.ToRadians(degrees.Z));
}
