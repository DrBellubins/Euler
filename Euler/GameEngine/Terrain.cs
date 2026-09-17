using System.Numerics;

namespace Euler.GameEngine;

/// <summary>
/// An infinite FBM-heightfield terrain primitive for the <see cref="Raymarcher"/>:
/// the surface y = Offset.y + Amplitude * hill(fbm((x, z - Offset.xz) * Frequency)),
/// where hill() is the shader's hill-shaping curve (squared FBM - see
/// <c>Assets/Shaders/Raymarcher/Shapes/Terrain.inc</c>). No mesh is involved -
/// the raymarcher marches the heightfield per pixel. Like the plane family it
/// is edgeless and infinite.
///
/// Only ONE terrain exists per scene: a heightfield already occupies the whole
/// XZ plane, so <see cref="Raymarcher.AddTerrain"/> replaces rather than
/// accumulates.
///
/// The terrain carries NO lighting of its own: it is lit by the scene's
/// <see cref="Sun"/> (directional diffuse + ray-traced hard shadows, see
/// <c>Assets/Shaders/Raymarcher/Lighting.inc</c>).
/// </summary>
public struct TerrainPrimitive
{
    /// <summary>
    /// World-space offset of the heightfield: the noise is sampled at
    /// (world - Offset).xz and the surface is lifted by Offset.y.
    /// </summary>
    public Vector3 Offset;

    /// <summary>Maximum hill height above the Offset.y plane, in world units.</summary>
    public float Amplitude;

    /// <summary>Base FBM frequency - higher = smaller, denser hills.</summary>
    public float Frequency;

    /// <summary>FBM octaves (the shader clamps to 1..MAX_TERRAIN_OCTAVES).</summary>
    public float Octaves;

    /// <summary>UV tiling: texture repeats per world unit along world X/Z.</summary>
    public Vector2 UvScale;

    public TerrainPrimitive(Vector3 offset, float amplitude, float frequency, float octaves,
        Vector2 uvScale)
    {
        Offset = offset;
        Amplitude = amplitude;
        Frequency = frequency;
        Octaves = octaves;
        UvScale = uvScale;
    }

    /// <summary>
    /// Packs this terrain into <paramref name="data"/> at <paramref name="index"/>
    /// as 3 consecutive vec4s matching the shader's TerrainData layout:
    /// <code>[offset.xyz, 0] [amplitude, frequency, octaves, 0] [uvScale.x, uvScale.y, 0, 0]</code>
    /// </summary>
    public void WriteInto(Span<float> data, int index)
    {
        int o = index * 12;
        data[o + 0] = Offset.X;
        data[o + 1] = Offset.Y;
        data[o + 2] = Offset.Z;
        data[o + 3] = 0f;
        data[o + 4] = Amplitude;
        data[o + 5] = Frequency;
        data[o + 6] = Octaves;
        data[o + 7] = 0f;
        data[o + 8] = UvScale.X;
        data[o + 9] = UvScale.Y;
        data[o + 10] = 0f;
        data[o + 11] = 0f;
    }
}
