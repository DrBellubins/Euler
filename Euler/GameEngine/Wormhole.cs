using System.Numerics;

namespace Euler.GameEngine;

/// <summary>
/// A traversable wormhole for the <see cref="Raymarcher"/>: a 4D spacetime
/// modification, not an intersectable object. Space is a 4D hypersurface of
/// two flat 3D sheets (w = +-Rmin, the two "universes") joined by a torus
/// tube around the rim circle of radius <see cref="Rmaj"/> at w = 0 - see
/// <c>Assets/Shaders/Raymarcher/Shapes/Wormhole.inc</c> for the geometry.
/// Rays that enter the mouth (the 3D ball of radius <see cref="Rmaj"/>) are
/// marched in 4D through the throat and emerge on the other sheet, so the
/// other universe is visible through the mouth.
///
/// The camera is 4D-authoritative when a wormhole is active
/// (<see cref="WorldCam"/>), because a 3D camera degenerates when it points
/// into the mouth (its view direction then has no 3D projection).
///
/// Only ONE wormhole exists per scene: it is a per-pixel global effect (like
/// the terrain), not an arrayed primitive, so
/// <see cref="Raymarcher.AddWormhole"/> replaces rather than accumulates.
/// </summary>
public struct WormholePrimitive
{
    /// <summary>World-space center of the mouth.</summary>
    public Vector3 Center;

    /// <summary>
    /// Rim / mouth radius: radius of the rim circle (the throat's center
    /// line) and of the 3D ball a ray must enter to go through the throat.
    /// </summary>
    public float Rmaj;

    /// <summary>
    /// Tube radius: radius of the torus cross-section; the flat sheets sit
    /// at w = +-Rmin.
    /// </summary>
    public float Rmin;

    public WormholePrimitive(Vector3 center, float rmaj, float rmin)
    {
        Center = center;
        Rmaj = rmaj;
        Rmin = rmin;
    }

    /// <summary>
    /// Packs this wormhole into <paramref name="data"/> at <paramref name="index"/>
    /// as two vec4s matching the shader's WormholeData layout:
    /// <code>[0] center.x, center.y, center.z, rMaj; [1] rMin, 0, 0, 0</code>
    /// </summary>
    public void WriteInto(Span<float> data, int index)
    {
        int o = index * 8;
        data[o + 0] = Center.X;
        data[o + 1] = Center.Y;
        data[o + 2] = Center.Z;
        data[o + 3] = Rmaj;
        data[o + 4] = Rmin;
        data[o + 5] = 0f;
        data[o + 6] = 0f;
        data[o + 7] = 0f;
    }
}
