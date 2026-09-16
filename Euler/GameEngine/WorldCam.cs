using System.Numerics;
using Raylib_cs;

namespace Euler.GameEngine;

/// <summary>
/// The 4D-authoritative camera for the traversable wormhole (see
/// <c>Assets/Shaders/Raymarcher/Shapes/Wormhole.inc</c>).
///
/// A 3D camera cannot look into the mouth: a ray aimed through the throat
/// points purely along the 4th axis and has NO 3D direction to integrate.
/// So the camera state lives in 4D - a position and an orthonormal
/// Forward/Left/Up basis in WORMHOLE-LOCAL space (xyz = world - center, w =
/// the fourth axis) - and the 3D camera (HUD, derived view) is derived from
/// it.
///
/// Every frame <see cref="Update"/> applies yaw/pitch/roll as 2D rotations in
/// the (Forward, Left), (Forward, Up) and (Left, Up) planes (w untouched),
/// integrates local movement along the basis, and re-projects the whole state ONTO the
/// wormhole hypersurface (point snapped, basis Gram-Schmidt'ed against the
/// space normal) - the C# twin of the GLSL <c>SpaceNormalize</c> the per-
/// pixel march uses. The C# and GLSL space SDF/normal below are deliberate
/// duplicates of Wormhole.inc: both sides must agree on where "the surface
/// of space" is.
/// </summary>
public class WorldCam
{
    // 4D camera basis in wormhole-LOCAL space (columns of the WormholeCam
    // uniform: Forward, Left, Up, Pos).
    public Vector4 Forward;
    public Vector4 Left;
    public Vector4 Up;
    public Vector4 Pos;

    /// <summary>World-space center of the wormhole (Pos.xyz + Center = world position).</summary>
    public Vector3 Center;

    private float _rmaj;
    private float _rmaj2;
    private float _rmin;

    // The 3D view direction and up used by the derived camera while the 4D
    // forward/up point into the throat (their xyz projections degenerate to
    // zero).
    private Vector3 _lastForward3 = new(0f, 0f, 1f);
    private Vector3 _lastUp3 = Vector3.UnitY;

    public WorldCam(Vector3 center, Vector4 pos, Vector4 forward, Vector4 left, Vector4 up)
    {
        Center = center;
        Pos = pos;
        Forward = forward;
        Left = left;
        Up = up;
    }

    /// <summary>+1 on the upper sheet, -1 on the lower one, 0 on the throat.</summary>
    public int Universe
    {
        get
        {
            if (_rmin <= 0f) return 1;
            if (Pos.W > 0.5f * _rmin) return 1;
            if (Pos.W < -0.5f * _rmin) return -1;
            return 0;
        }
    }

    public Vector3 PosWorld => new Vector3(Pos.X, Pos.Y, Pos.Z) + Center;

    /// <summary>
    /// Advances the 4D camera one frame. <paramref name="yawDelta"/> /
    /// <paramref name="pitchDelta"/> / <paramref name="rollDelta"/> are
    /// radians (screen-drag / Q-E key deltas, positive roll = left), and
    /// <paramref name="move"/> is local displacement in (Forward, Left, Up)
    /// components, world units. <paramref name="wormhole"/> may have moved
    /// (the basis is re-projected onto its hypersurface).
    /// </summary>
    public void Update(float yawDelta, float pitchDelta, float rollDelta, Vector3 move, WormholePrimitive wormhole)
    {
        Center = wormhole.Center;
        _rmaj = wormhole.Rmaj;
        _rmaj2 = _rmaj * _rmaj;
        _rmin = wormhole.Rmin;

        // Yaw: 2D rotation in the (Forward, Left) plane (the w components are
        // untouched - exactly the Shadertoy original's camera * yaw).
        float cy = MathF.Cos(yawDelta);
        float sy = MathF.Sin(yawDelta);
        Vector4 F0 = Forward, L0 = Left;
        Forward = F0 * cy + L0 * sy;
        Left = -F0 * sy + L0 * cy;

        // Pitch: 2D rotation in the (Forward, Up) plane, applied AFTER the
        // yaw (camera * yaw * pitch order), so pitching over the top can
        // aim the view straight along w - through the throat - which no 3D
        // camera can do.
        float cp = MathF.Cos(pitchDelta);
        float sp = MathF.Sin(pitchDelta);
        Vector4 F1 = Forward, U0 = Up;
        Forward = F1 * cp + U0 * sp;
        Up = -F1 * sp + U0 * cp;

        // Roll: 2D rotation in the (Left, Up) plane, applied AFTER yaw/pitch
        // (camera * yaw * pitch * roll order) so it spins the already-aimed
        // view around Forward (Q/E). Forward is untouched; orthonormality is
        // preserved exactly as with the yaw/pitch rotations.
        float cr = MathF.Cos(rollDelta);
        float sr = MathF.Sin(rollDelta);
        Vector4 L2 = Left, U2 = Up;
        Left = L2 * cr - U2 * sr;
        Up = L2 * sr + U2 * cr;

        Pos += Forward * move.X + Left * move.Y + Up * move.Z;

        SnapToSpace();
    }

    /// <summary>
    /// Snaps Pos onto the wormhole hypersurface and Gram-Schmidt'ed the
    /// basis against the space normal (Wormhole.inc's space_orthonorm_gs).
    /// </summary>
    private void SnapToSpace()
    {
        Vector4 n = SpaceNormal(Pos);
        Pos -= SpaceSdf(Pos) * n;

        Forward = ProjectTangent(Forward, n, null, null);
        Left = ProjectTangent(Left, n, Forward, null);
        Up = ProjectTangent(Up, n, Forward, Left);
    }

    // One Gram-Schmidt stage against the space normal (and the already-
    // orthonormal basis vectors). Degenerate input (v parallel to the
    // normal, e.g. Forward aimed straight through the mouth from its exact
    // center) keeps v unchanged - the next frame's rotation re-tangents it.
    private static Vector4 ProjectTangent(Vector4 v, Vector4 n, Vector4? a, Vector4? b)
    {
        Vector4 q = v - n * Vector4.Dot(v, n);
        if (a.HasValue) q -= a.Value * Vector4.Dot(v, a.Value);
        if (b.HasValue) q -= b.Value * Vector4.Dot(v, b.Value);
        float l = q.Length();
        return l > 1e-5f ? Vector4.Normalize(q) : v;
    }

    // ------------------------------------------------------------------
    // Space SDF / normal - MUST mirror SpaceSdf/SpaceNormal in Wormhole.inc
    // ------------------------------------------------------------------

    private Vector4 SpaceNormal(Vector4 p)
    {
        float r2 = p.X * p.X + p.Y * p.Y + p.Z * p.Z;
        if (r2 >= _rmaj2)
            return new Vector4(0f, 0f, 0f, p.W >= 0f ? 1f : -1f);

        float rl = MathF.Sqrt(r2);
        // Guard the w axis: at p.xyz = 0 every rim point is equidistant.
        Vector3 dir3 = rl > 1e-5f ? new Vector3(p.X / rl, p.Y / rl, p.Z / rl) : Vector3.UnitY;
        // Toward p from the nearest rim point (the original's convention):
        // the snap is Pos -= SpaceSdf * n, which with this sign pulls the
        // point onto the surface from either side.
        Vector4 rim = new Vector4(dir3.X * _rmaj, dir3.Y * _rmaj, dir3.Z * _rmaj, 0f);
        return Vector4.Normalize(p - rim);
    }

    private float SpaceSdf(Vector4 p)
    {
        float r2 = p.X * p.X + p.Y * p.Y + p.Z * p.Z;
        if (r2 >= _rmaj2)
            return MathF.Abs(p.W) - _rmin;

        float rl = MathF.Sqrt(r2);
        Vector3 dir3 = rl > 1e-5f ? new Vector3(p.X / rl, p.Y / rl, p.Z / rl) : Vector3.UnitY;
        Vector4 rim = new(dir3.X * _rmaj, dir3.Y * _rmaj, dir3.Z * _rmaj, 0f);
        return Vector4.Distance(rim, p) - _rmin;
    }

    /// <summary>
    /// The WormholeCam uniform: columns (Forward, Left, Up, Pos),
    /// wormhole-local 4D (matches the GLSL ray generation
    /// <c>WormholeCam * vec4(Focal, -ndc.x, ndc.y, 0)</c>).
    /// Constructor layout follows the verified SetCameraMatrix convention
    /// (Raymarcher.cs): intended matrix COLUMNS written row by row.
    /// </summary>
    public Matrix4x4 ToMatrix()
    {
        return new Matrix4x4(
            Forward.X, Left.X, Up.X, Pos.X,
            Forward.Y, Left.Y, Up.Y, Pos.Y,
            Forward.Z, Left.Z, Up.Z, Pos.Z,
            Forward.W, Left.W, Up.W, Pos.W);
    }

    /// <summary>
    /// The derived 3D camera (HUD / display). Position is the 4D position's
    /// xyz; the view direction is Forward's xyz and the up is Up's xyz, both
    /// remembered while they point into the throat (zero 3D projection). The
    /// up is what carries Q/E roll into the 3D view.
    /// </summary>
    public Camera3D ToCamera3D(float fovY = 90f)
    {
        Vector3 pos = PosWorld;
        Vector3 f3 = new(Forward.X, Forward.Y, Forward.Z);
        float l = f3.Length();
        if (l > 0.01f)
        {
            f3 /= l;
            _lastForward3 = f3;
        }

        Vector3 u3 = new(Up.X, Up.Y, Up.Z);
        float lu = u3.Length();
        if (lu > 0.01f)
        {
            u3 /= lu;
            _lastUp3 = u3;
        }

        return new Camera3D
        {
            Position = pos,
            Target = pos + _lastForward3,
            Up = _lastUp3,
            FovY = fovY,
            Projection = CameraProjection.Perspective
        };
    }
}
