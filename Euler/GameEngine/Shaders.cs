namespace Euler.GameEngine;

/// <summary>
/// Embedded shader sources for the full-screen raymarcher. Kept as C#
/// constants so the engine has no runtime shader-file dependency
/// (AOT / native-publish friendly).
///
/// NOTE: raylib 6.0 compiles custom shader code verbatim - it does NOT
/// inject attribute/uniform declarations or define VERTEX/FRAGMENT macros
/// (the old 5.x "#if defined(VERTEX)" single-file convention no longer
/// works). Shaders must be complete, versioned sources, per the
/// examples/shaders/resources/shaders/glsl100/*.vs|*.fs convention.
/// </summary>
public static class Shaders
{
    /// <summary>
    /// Vertex stage: maps the full-screen quad through raylib's 2D mvp.
    /// Nothing else - all work happens in the fragment stage.
    /// </summary>
    public const string RaymarcherVertexSource = """
#version 330

in vec3 vertexPosition;
uniform mat4 mvp;

void main()
{
    gl_Position = mvp * vec4(vertexPosition, 1.0);
}
""";

    /// <summary>
    /// Fragment stage: computes one ray per pixel, intersects all plane
    /// primitives analytically and shades the closest hit with a tiled
    /// texture (plus sky + distance fog on misses).
    ///
    /// Per-plane uniform data lives in PlaneData (3 vec4 per plane):
    ///   [0] origin.xyz, size
    ///   [1] normal.xyz, textureIndex
    ///   [2] uvScale.xy, 0, 0
    /// See PlanePrimitive.WriteInto for the C# side.
    /// </summary>
    public const string RaymarcherFragmentSource = """
#version 330

out vec4 fragColor;

#define MAX_PLANES 8

uniform vec2  Resolution;            // framebuffer size in pixels
uniform mat4  CamToWorld;            // camera space (y up, -z forward) -> world
uniform float Focal;                 // 1 / tan(fovY * 0.5), precomputed in C#
uniform int   PlaneCount;
uniform vec4  PlaneData[MAX_PLANES * 3];
uniform sampler2D PlaneTex;

// Analytic ray/plane intersection. Returns distance t (or -1 on miss) and
// the hit point's UV in *tile space* (already tiled by the plane's UvScale).
float IntersectPlane(vec3 ro, vec3 rd, int i, out vec2 uv)
{
    vec4 a = PlaneData[i * 3 + 0];
    vec4 b = PlaneData[i * 3 + 1];
    vec4 c = PlaneData[i * 3 + 2];

    vec3 origin = a.xyz;
    float size  = a.w;
    vec3 normal = normalize(b.xyz);

    float denom = dot(rd, normal);
    if (abs(denom) < 1e-6) return -1.0;   // (nearly) parallel to the plane

    float t = dot(origin - ro, normal) / denom;
    if (t <= 0.0) return -1.0;            // behind the camera

    vec3 hit = ro + rd * t;

    // Orthonormal basis for the plane's local frame, so UVs are defined in
    // plane space - this keeps tiling consistent even when the engine bends
    // space around the plane. For a +Y normal this gives xAxis = +Z, yAxis = +X.
    vec3 refAxis = (abs(normal.y) > 0.99) ? vec3(1.0, 0.0, 0.0) : vec3(0.0, 1.0, 0.0);
    vec3 xAxis = normalize(cross(refAxis, normal));
    vec3 yAxis = cross(normal, xAxis);

    vec3 rel = hit - origin;
    vec2 local = vec2(dot(rel, xAxis), dot(rel, yAxis));

    if (size > 0.0 && (abs(local.x) > size || abs(local.y) > size))
        return -1.0;                                  // finite plane, hit off the edge

    uv = local * c.xy;                                // <- UV tiling
    return t;
}

vec3 SkyColor(vec3 rd)
{
    float h = clamp(rd.y * 0.5 + 0.5, 0.0, 1.0);
    return mix(vec3(0.78, 0.87, 1.0), vec3(0.28, 0.47, 0.78), h);
}

// rows 0..15   = CamToWorld elements 0..15 (column-major, row-major read)
// rows 16..27  = PlaneData floats 0..11
// rows 28..30  = Focal, Resolution.x/1600, Resolution.y/900
void main()
{
    int row = int(gl_FragCoord.y - 1.0);
    float v = 0.0;
    if (row >= 0 && row < 16) {
        float e = 0.0;
        if (row == 0) e = CamToWorld[0].x; else if (row == 1) e = CamToWorld[0].y;
        else if (row == 2) e = CamToWorld[0].z; else if (row == 3) e = CamToWorld[0].w;
        else if (row == 4) e = CamToWorld[1].x; else if (row == 5) e = CamToWorld[1].y;
        else if (row == 6) e = CamToWorld[1].z; else if (row == 7) e = CamToWorld[1].w;
        else if (row == 8) e = CamToWorld[2].x; else if (row == 9) e = CamToWorld[2].y;
        else if (row == 10) e = CamToWorld[2].z; else if (row == 11) e = CamToWorld[2].w;
        else if (row == 12) e = CamToWorld[3].x; else if (row == 13) e = CamToWorld[3].y;
        else e = CamToWorld[3].w - 1.0;                   // skip the 1.0
        v = e;
    } else if (row >= 16 && row < 28) {
        float f = 0.0;
        if (row == 16) f = PlaneData[0].x; else if (row == 17) f = PlaneData[0].y;
        else if (row == 18) f = PlaneData[0].z; else if (row == 19) f = PlaneData[0].w;
        else if (row == 20) f = PlaneData[1].x; else if (row == 21) f = PlaneData[1].y;
        else if (row == 22) f = PlaneData[1].z; else if (row == 23) f = PlaneData[1].w;
        else if (row == 24) f = PlaneData[2].x; else if (row == 25) f = PlaneData[2].y;
        v = f;
    } else if (row == 28) v = Focal;
    else if (row == 29) v = Resolution.x / 1600.0;
    else if (row == 30) v = Resolution.y / 900.0;
    fragColor = vec4(clamp(v * 0.1 + 0.5, 0.0, 1.0), 0.0, 0.0, 1.0);
    return;
}

void RealMain()
{
    // Pixel center in normalized device coords (y up, -1..1, x aspect-corrected).
    vec2 ndc = (2.0 * gl_FragCoord.xy - Resolution) / Resolution.y;

    // Pixel ray in camera space, then world space.
    vec3 dirCam = normalize(vec3(ndc, -Focal));
    vec3 rd = normalize((CamToWorld * vec4(dirCam, 0.0)).xyz);
    vec3 ro = (CamToWorld * vec4(0.0, 0.0, 0.0, 1.0)).xyz;

    // Closest plane hit.
    float t = -1.0;
    vec2 uv = vec2(0.0);
    for (int i = 0; i < MAX_PLANES; i++)
    {
        if (i >= PlaneCount) break;
        float th = IntersectPlane(ro, rd, i, uv);
        if (th > 0.0 && (t < 0.0 || th < t)) t = th;
    }

    vec3 color;
    if (t > 0.0)
    {
        // Tiled texture sample (sampler wrap = REPEAT, so fract() is implicit).
        color = texture(PlaneTex, uv).rgb;

        // Gentle exponential distance fog toward the horizon.
        vec3 fogColor = SkyColor(normalize(vec3(rd.x, 0.0, rd.z)));
        float fog = 1.0 - exp(-0.010 * t);
        color = mix(color, fogColor, fog);
    }
    else
    {
        color = SkyColor(rd);
    }

    fragColor = vec4(color, 1.0);
}
""";
}
