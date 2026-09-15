// Plane shape family for the raymarcher.
//
// Included from Raymarcher.comp. This file is the complete, self-contained
// "plane family": its data layout, uniforms, intersection test and shading.
// The driver (Raymarcher.comp) races all families for the closest hit in
// IntersectScene() and calls the family's shade function in main().
//
// New shape families (SDF solids, 4D objects, ...) get their own file in
// Raymarcher/Shapes/ following this same pattern:
//
//     data layout + uniforms  ->  Intersect<Family>(ro, rd, out uv)
//                                 ->  Shade<Family>(uv)
//
// NOTE: no #version directive here - included files must never carry one;
// #version must remain the first line of the main (expanding) shader.

#define MAX_PLANES 8

// Family ID latched by the driver's IntersectScene() so main() can dispatch
// shading to the family that won the closest-hit race. Every shape family
// declares its own ID here in its own .inc (terrain's is FamilyTerrain in
// Shapes/Terrain.inc) - new families pick the next number.
const int FamilyPlane = 0;

// Per-plane uniform data lives in PlaneData (3 vec4 per plane):
//   [0] origin.xyz, size
//   [1] normal.xyz, textureIndex
//   [2] uvScale.xy, 0, 0
// See PlanePrimitive.WriteInto for the C# side.
uniform int   PlaneCount;
uniform vec4  PlaneData[MAX_PLANES * 3];

// Bound to texture unit 0 by the C# side before the dispatch (see the
// Raymarcher.comp header notes on why Raylib.SetShaderValueTexture must not
// be used for the compute stage).
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

// Plane shading: sample the family's tiled texture (sampler wrap = REPEAT,
// so fract() is implicit).
vec3 ShadePlane(vec2 uv)
{
    return texture(PlaneTex, uv).rgb;
}
