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

// TEMP-DEBUG: write uniform state into pixels
void main()
{
    fragColor = vec4(
        clamp((PlaneData[0].y + 3.0) / 2.0, 0.0, 1.0),   // plane origin.y: -2 -> 0.5
        clamp(Focal, 0.0, 1.0),                          // Focal: 90deg fov -> 1.0
        clamp((CamToWorld[3].z + 11.0) / 2.0, 0.0, 1.0), // cam pos z: -10 -> 0.5
        clamp(texture(PlaneTex, vec2(0.0, 0.0)).r, 0.0, 1.0));
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
