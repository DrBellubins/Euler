using System.Numerics;

namespace Euler.GameEngine;

/// <summary>
/// A wormhole mouth primitive for the <see cref="Raymarcher"/>: a sphere of
/// radius <see cref="Rs"/> (the event horizon) that captures every ray that
/// reaches it, surrounded by a gravitational field that bends every ray
/// passing within the field radius (see
/// <c>Assets/Shaders/Raymarcher/Shapes/Wormhole.inc</c>). The bending is
/// integrated per pixel in the compute stage between analytic
/// IntersectScene() steps: rays grazing the mouth orbit the throat, so the
/// mouth renders as a black disc with a photon ring and a lensed halo.
///
/// Only ONE wormhole exists per scene: the field is a per-pixel global
/// effect (like the terrain), not an arrayed primitive, so
/// <see cref="Raymarcher.AddWormhole"/> replaces rather than accumulates.
/// </summary>
public struct WormholePrimitive
{
    /// <summary>World-space center of the mouth.</summary>
    public Vector3 Center;

    /// <summary>Event horizon radius, in world units.</summary>
    public float Rs;

    public WormholePrimitive(Vector3 center, float rs)
    {
        Center = center;
        Rs = rs;
    }

    /// <summary>
    /// Packs this wormhole into <paramref name="data"/> at <paramref name="index"/>
    /// as one vec4 matching the shader's WormholeData layout:
    /// <code>[center.x, center.y, center.z, rs]</code>
    /// </summary>
    public void WriteInto(Span<float> data, int index)
    {
        int o = index * 4;
        data[o + 0] = Center.X;
        data[o + 1] = Center.Y;
        data[o + 2] = Center.Z;
        data[o + 3] = Rs;
    }
}
