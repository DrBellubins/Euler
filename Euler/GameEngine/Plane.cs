using System.Numerics;

namespace Euler.GameEngine;

/// <summary>
/// A flat plane primitive for the <see cref="Raymarcher"/>: an analytic
/// infinite (or finite) plane, shaded with a tiled texture.
/// No mesh is involved - the raymarcher intersects it analytically per pixel.
/// </summary>
public struct PlanePrimitive
{
    /// <summary>Any point on the plane (e.g. (0, -2, 0) for a floor at y = -2).</summary>
    public Vector3 Origin;

    /// <summary>Plane normal (normalized when used).</summary>
    public Vector3 Normal;

    /// <summary>
    /// UV tiling: texture repeats per world unit along each local axis of the
    /// plane. (1, 1) = one tile per world unit, (4, 2) = one tile every 0.25 x
    /// 0.5 world units.
    /// </summary>
    public Vector2 UvScale;

    /// <summary>Half-extent of a finite plane in its local axes; 0 = infinite.</summary>
    public float Size;

    /// <summary>Reserved: per-plane texture index (v1: all planes share the raymarcher's texture).</summary>
    public int TextureIndex;

    public PlanePrimitive(Vector3 origin, Vector3 normal, Vector2 uvScale, float size = 0f, int textureIndex = 0)
    {
        Origin = origin;
        Normal = normal;
        UvScale = uvScale;
        Size = size;
        TextureIndex = textureIndex;
    }

    /// <summary>
    /// Packs this plane into <paramref name="data"/> at <paramref name="planeIndex"/>
    /// as 3 consecutive vec4s matching the shader's PlaneData layout:
    /// <code>[origin.xyz, size] [normal.xyz, textureIndex] [uvScale.x, uvScale.y, 0, 0]</code>
    /// </summary>
    public void WriteInto(Span<float> data, int planeIndex)
    {
        int o = planeIndex * 12;
        data[o + 0] = Origin.X;
        data[o + 1] = Origin.Y;
        data[o + 2] = Origin.Z;
        data[o + 3] = Size;
        data[o + 4] = Normal.X;
        data[o + 5] = Normal.Y;
        data[o + 6] = Normal.Z;
        data[o + 7] = TextureIndex;
        data[o + 8] = UvScale.X;
        data[o + 9] = UvScale.Y;
        data[o + 10] = 0f;
        data[o + 11] = 0f;
    }
}
