// Terrain shape family for the raymarcher.
//
// Included from Raymarcher.comp. This file is the complete, self-contained
// "terrain family": its data layout, uniforms, intersection and shading.
//
// The surface is an INFINITE FBM heightfield - the terrain analogue of the
// analytic plane family (Shapes/Plane.inc): no edges, no mesh. Every pixel
// marches the heightfield directly in the shader:
//
//     world y = Offset.y + Amplitude * HillShape(fbm((x,z - Offset.xz) * Frequency))
//
// It follows the same contract as the other shape families:
//
//     data layout + uniforms  ->  IntersectTerrain(ro, rd, out uv)
//                                 ->  ShadeTerrain(uv)
//
// uv is the hit's XZ in the terrain's LOCAL frame (world units, UNTILED);
// ShadeTerrain() does the tiling, the normal and the lighting.
//
// NOTE: no #version directive here - included files must never carry one;
// #version must remain the first line of the main (expanding) shader.

// Family ID latched by the driver's IntersectScene() so main() can dispatch
// shading to the family that won the closest-hit race (see Plane.inc).
const int FamilyTerrain = 1;

// Per-terrain uniform data lives in TerrainData (4 vec4):
//   [0] offset.xyz, 0
//   [1] amplitude, frequency, octaves, 0
//   [2] sunDir.xyz, sunIntensity
//   [3] uvScale.xy, 0, 0
// See TerrainPrimitive.WriteInto for the C# side.
uniform int   TerrainEnabled;   // 0/1: the scene has a terrain
uniform vec4  TerrainData[4];

// Bound to texture unit 1 by the C# side before the dispatch (PlaneTex owns
// unit 0; see the Raymarcher.comp header notes on why Raylib.SetShaderValueTexture
// must not be used for the compute stage).
uniform sampler2D TerrainTex;

#define MAX_TERRAIN_OCTAVES 6

// Per-octave rotation: keeps the stacked noise layers from aligning with the
// world axes (no grid-shaped hills).
const mat2 TerrainRot = mat2(0.80, 0.60, -0.60, 0.80);

// ---------------------------------------------------------------------------
// FBM noise (value noise, no external dependencies)
// ---------------------------------------------------------------------------

float Hash21(vec2 p)
{
    p = fract(p * vec2(123.34, 456.21));
    p += dot(p, p + 45.32);
    return fract(p.x * p.y);
}

float ValueNoise(vec2 p)
{
    vec2 i = floor(p);
    vec2 f = fract(p);
    // True quintic: 0 at f=0, 1 at f=1, zero slope at both ends (C2).
    // NOTE: the blend MUST reach 1.0 at f=1 - a curve that returns to 0 at
    // the cell boundary (e.g. 3f^2-6f+3) leaves every lattice cell showing
    // its own corner hash: a grid of flat squares instead of smooth noise.
    vec2 u = f * f * f * (f * (f * 6.0 - 15.0) + 10.0);
    float n00 = Hash21(i + vec2(0.0, 0.0));
    float n10 = Hash21(i + vec2(1.0, 0.0));
    float n01 = Hash21(i + vec2(0.0, 1.0));
    float n11 = Hash21(i + vec2(1.0, 1.0));
    return mix(mix(n00, n10, u.x), mix(n01, n11, u.x), u.y);
}

// Fractal Brownian motion: `octaves` layers (clamped to 1..MAX_TERRAIN_OCTAVES),
// normalized back to [0, 1) so the range is independent of the octave count.
float Fbm(vec2 p, float octaves)
{
    float v = 0.0;
    float amp = 0.5;
    float norm = 0.0;
    for (int i = 0; i < MAX_TERRAIN_OCTAVES; i++)
    {
        if (float(i) >= octaves) break;
        v += amp * ValueNoise(p);
        norm += amp;
        p = TerrainRot * p * 2.02;
        amp *= 0.5;
    }
    return v / norm;
}

// ---------------------------------------------------------------------------
// Heightfield
// ---------------------------------------------------------------------------

// Shape the [0,1) FBM into HILLS: squaring biases the field low, so most of
// the terrain is rolling ground with occasional taller peaks.
float HillShape(float h)
{
    return h * h;
}

// World Y of the surface above a point of the terrain's LOCAL frame
// (local = world - Offset).
float TerrainHeightLocal(vec2 localXz)
{
    vec4 o = TerrainData[0];
    vec4 p = TerrainData[1];
    float h = HillShape(Fbm(localXz * p.y, clamp(p.z, 1.0, float(MAX_TERRAIN_OCTAVES))));
    return o.y + p.x * h;
}

// World Y of the surface above a WORLD-space xz point.
float TerrainHeight(vec2 worldXz)
{
    return TerrainHeightLocal(worldXz - TerrainData[0].xz);
}

// Surface normal by central differences of the heightfield (the heightfield
// has no other analytic normal).
vec3 TerrainNormal(vec2 worldXz)
{
    const float e = 0.5;    // difference step in world units
    float hL = TerrainHeight(worldXz - vec2(e, 0.0));
    float hR = TerrainHeight(worldXz + vec2(e, 0.0));
    float hD = TerrainHeight(worldXz - vec2(0.0, e));
    float hU = TerrainHeight(worldXz + vec2(0.0, e));
    return normalize(vec3(hL - hR, 2.0 * e, hD - hU));
}

// ---------------------------------------------------------------------------
// Intersection: raymarch the heightfield
// ---------------------------------------------------------------------------

// Marches the infinite heightfield. Returns distance t (or -1 on miss) and
// latches the hit's LOCAL xz into *uv.
//
// The plane family is analytic-infinite; a march needs a horizon. The
// driver's fog (1 - exp(-0.010 * t), see ApplyFog) is ~1.0 beyond ~800
// units, so a miss past the cap renders pure fog - the cap is invisible.
float IntersectTerrain(vec3 ro, vec3 rd, out vec2 uv)
{
    uv = vec2(0.0);

    // A heightfield has PEAKS, so a ray can hit the surface from ANY
    // direction: when the camera sits below a peak's summit, the part of
    // that peak above the camera's horizon is only reachable by rays with
    // rd.y > 0. Culling those directions clipped the terrain exactly along
    // the world plane y = ro.y - a "cutoff at the horizon" that stayed fixed
    // in place while looking the camera around.
    //
    // The only rays guaranteed to miss are horizontal/upward ones that
    // start at or above the terrain's global maximum (Offset.y + Amplitude;
    // the true max is slightly lower because Fbm < 1 and HillShape < 1):
    // such a ray can only rise, so it can never cross the surface.
    if (rd.y >= 0.0 && ro.y >= TerrainData[0].y + TerrainData[1].x)
        return -1.0;

    // Camera at/below the surface: a heightfield has no interior, so the
    // marched distance would be inconsistent - report a miss (the scene
    // degrades to sky) instead of marching garbage.
    if (TerrainHeight(ro.xz) >= ro.y) return -1.0;

    const float MaxDist  = 1000.0;
    const int   MaxSteps = 128;
    // Safety shortens the step so most rays land on the surface directly;
    // a step that still overshoots a steep slope is recovered by the
    // bisection below (it only costs convergence speed, not correctness).
    const float Safety   = 0.75;

    // d is the height ABOVE the surface: positive when the sample point is
    // above the terrain (the normal state), ~0 at the surface, negative once
    // the march has overshot it. (h - p.y) would be negative in the normal
    // state - do not flip the sign when reading this loop.
    float t     = 0.0;
    float tPrev = 0.0;   // last distance known to be above the surface
    for (int i = 0; i < MaxSteps; i++)
    {
        vec3 p = ro + rd * t;
        float d = p.y - TerrainHeight(p.xz);      // height above the surface
        if (d < 0.0)
        {
            // Overshot the surface on a steep slope: the surface lies
            // between tPrev (above) and t (below). The heightfield is
            // continuous in t, so bisect to the crossing instead of
            // reporting a miss (misses would show as sky holes).
            float lo = tPrev, hi = t;
            for (int j = 0; j < 16; j++)
            {
                float mid = 0.5 * (lo + hi);
                vec3 pm = ro + rd * mid;
                if (pm.y - TerrainHeight(pm.xz) > 0.0) lo = mid; else hi = mid;
            }
            vec3 ph = ro + rd * hi;
            uv = ph.xz - TerrainData[0].xz;
            return hi;
        }
        float eps = 0.02 + 0.005 * t;             // tight up close, loose far away
        if (d <= eps)
        {
            uv = p.xz - TerrainData[0].xz;
            return (t > 0.0) ? t : 1e-3;          // the driver treats t=0 as a miss
        }
        tPrev = t;
        t += d * Safety;
        if (t >= MaxDist) return -1.0;            // beyond the (fog-hidden) horizon
    }
    return -1.0;
}

// ---------------------------------------------------------------------------
// Shading
// ---------------------------------------------------------------------------

// Terrain shading: tiled texture + lambertian sun, using the finite-
// difference normal so the hills read as 3D. *uv is the LOCAL xz, world
// units, untiled (see IntersectTerrain).
vec3 ShadeTerrain(vec2 localXz)
{
    vec3 n = TerrainNormal(localXz + TerrainData[0].xz);

    vec4 sd = TerrainData[2];
    float len = length(sd.xyz);
    vec3 sunDir = (len > 1e-5) ? sd.xyz / len : vec3(0.0, 1.0, 0.0);
    float diff = max(dot(n, sunDir), 0.0) * sd.w;

    vec3 tex = texture(TerrainTex, localXz * TerrainData[3].xy).rgb;
    return tex * (0.35 + diff);   // 0.35 ambient floor
}
