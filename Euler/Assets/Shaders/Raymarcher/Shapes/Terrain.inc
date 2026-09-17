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
//                                 ->  ShadeTerrain(uv)   (the ALBEDO)
//
// (The finite-difference shading normal is computed in the driver's
// IntersectScene, which latches it for the winning ray - the upscaler and
// the lighting system share one normal per hit. The LOD octave falloff for
// that normal and for the march is the TERRAIN_LOD_* mechanism below.)
//
// uv is the hit's XZ in the terrain's LOCAL frame (world units, UNTILED);
// ShadeTerrain() does the tiling only - the sun + hard shadows arrive from
// the lighting system (Raymarcher/Lighting.inc).
//
// The shadow-ray path (IntersectTerrainShadow) shares the march via
// MarchTerrain with a relaxed sub-surface guard - see there.
//
// NOTE: no #version directive here - included files must never carry one;
// #version must remain the first line of the main (expanding) shader.

// Family ID latched by the driver's IntersectScene() so main() can dispatch
// shading to the family that won the closest-hit race (see Plane.inc).
const int FamilyTerrain = 1;

// Per-terrain uniform data lives in TerrainData (3 vec4):
//   [0] offset.xyz, 0
//   [1] amplitude, frequency, octaves, 0
//   [2] uvScale.xy, 0, 0
// See TerrainPrimitive.WriteInto for the C# side. (The terrain carries no
// light of its own - the scene's sun is uniform, see Raymarcher/Lighting.inc.)
uniform int   TerrainEnabled;   // 0/1: the scene has a terrain
uniform vec4  TerrainData[3];

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

// ---------------------------------------------------------------------------
// Distance LOD: octave falloff (the terrain's main cost lever)
// ---------------------------------------------------------------------------
//
// The per-step FBM cost is dominated by the OCTAVE COUNT: each octave is a
// ValueNoise call = 4 Hash21 evaluations, so a full-detail step is the single
// most expensive thing the march does (up to 128 steps x ~24 hashes, plus the
// bisection and the normal). Full detail only matters NEAR the camera - past a
// few dozen units the high-frequency octaves are sub-pixel, and the driver's
// fog (1 - exp(-0.010 * t), see ApplyFog) is already ~0.9 dense by ~200 units,
// so the far surface is mostly washed out anyway. The octave count therefore
// falls from the scene's full count down to a floor as the sample point moves
// away from the camera. This divides the per-step cost of every far ray with
// no change to the surface that is actually resolvable on screen.
//
// The falloff is a function of the camera-to-sample distance, which within a
// frame is a fixed function of (x,z) (the camera does not move mid-frame), so
// the marched surface stays a well-defined, piecewise-smooth heightfield and
// the bisection below still converges to a real crossing.
const float TERRAIN_LOD_NEAR = 50.0;   // full octaves at/under this distance
const float TERRAIN_LOD_FAR  = 200.0;  // floor octaves at/over this distance
const float TERRAIN_LOD_MIN  = 2.0;    // never march with fewer octaves than this

// Octave count for a sample `dist` world units from the camera: the scene's
// full count at close range, lerping down to the floor at distance. A float
// count is fine - Fbm() includes the whole octaves below it (1..MAX).
float TerrainOctavesFor(float dist)
{
    float full   = clamp(TerrainData[1].z, 1.0, float(MAX_TERRAIN_OCTAVES));
    float minOct = min(full, TERRAIN_LOD_MIN);   // never ask for more than exists
    float f = clamp((dist - TERRAIN_LOD_NEAR) / (TERRAIN_LOD_FAR - TERRAIN_LOD_NEAR),
                    0.0, 1.0);
    return mix(full, minOct, f);
}

// World Y of the surface above a point of the terrain's LOCAL frame
// (local = world - Offset). `dist` is the camera-to-point distance, used for
// the octave LOD falloff (TERRAIN_LOD_*); pass 0 for a point at the camera.
float TerrainHeightLocal(vec2 localXz, float dist)
{
    vec4 o = TerrainData[0];
    vec4 p = TerrainData[1];
    float h = HillShape(Fbm(localXz * p.y, TerrainOctavesFor(dist)));
    return o.y + p.x * h;
}

// World Y of the surface above a WORLD-space xz point, `dist` from the camera.
float TerrainHeight(vec2 worldXz, float dist)
{
    return TerrainHeightLocal(worldXz - TerrainData[0].xz, dist);
}

// Surface normal by central differences of the heightfield (the heightfield
// has no other analytic normal). `dist` is the camera-to-point distance for
// the octave LOD falloff, so far hits shade with cheaper normals.
vec3 TerrainNormal(vec2 worldXz, float dist)
{
    const float e = 0.5;    // difference step in world units
    float hL = TerrainHeight(worldXz - vec2(e, 0.0), dist);
    float hR = TerrainHeight(worldXz + vec2(e, 0.0), dist);
    float hD = TerrainHeight(worldXz - vec2(0.0, e), dist);
    float hU = TerrainHeight(worldXz + vec2(0.0, e), dist);
    return normalize(vec3(hL - hR, 2.0 * e, hD - hU));
}

// ---------------------------------------------------------------------------
// Intersection: raymarch the heightfield
// ---------------------------------------------------------------------------

// A shadow hit closer than this total path length from the origin is the
// point's OWN surface, not a separate occluder: the march folds a
// sub-surface / self-slope origin (the acne case) into a crossing within
// this tolerance - and then marches through the interior to the real
// occluder (see MarchTerrain) - and grazing re-crossings of the same
// surface do too. Lighting.inc compares the shadow hit distance against the
// SAME constant and folds a hit below it into "lit". (Declared here, not in
// Lighting.inc, because MarchTerrain uses it and GLSL requires the
// declaration before the function body - Lighting.inc is included later.)
const float SHADOW_HIT_TOL = 0.05;

// The infinite heightfield march (shared by the primary and the shadow
// rays). Returns distance t (or -1 on miss) and latches the hit's LOCAL xz
// into *uv.
//
// The plane family is analytic-infinite; a march needs a horizon. The
// driver's fog (1 - exp(-0.010 * t), see ApplyFog) is ~1.0 beyond ~800
// units, so a miss past the cap renders pure fog - the cap is invisible.
//
// `shadow` selects the shadow-ray variant. A shadow ray's normal-biased
// origin can sit INSIDE the surface (a steep slope pulls it below), and the
// sub-surface early-out must NOT fire there. More to the point for terrain
// SELF-shadowing: on a slope steeper than the sun's elevation the terrain
// rises faster than the shadow ray, so the ray crosses its OWN local surface
// within SHADOW_HIT_TOL of the origin and is INSIDE the solid - this is
// exactly the case where the point SHOULD be shadowed (it looks up at the
// peak that blocks the sun), so the march must NOT stop at that crossing.
// It steps through the interior (fixed InteriorStep - d * Safety is
// negative inside) until the ray exits the surface. The interior span IS the
// occlusion: if it stretches beyond SHADOW_HIT_TOL from the origin, the ray
// was inside the solid for the part of its path past the point (the peak
// between it and the sun) and the point is shadowed; a span that ends
// within the tolerance is genuine acne and the march just continues. A
// primary ray below the surface is a degenerate case (no interior) and
// stays a miss.
float MarchTerrain(vec3 ro, vec3 rd, bool shadow, out vec2 uv)
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
    // degrades to sky) instead of marching garbage. (Shadow rays exempt -
    // see the function header.)
    if (!shadow && TerrainHeight(ro.xz, 0.0) >= ro.y) return -1.0;   // dist 0 -> full octaves

    const float MaxDist  = 1000.0;
    const int   MaxSteps = 128;
    // Safety shortens the step so most rays land on the surface directly;
    // a step that still overshoots a steep slope is recovered by the
    // bisection below (it only costs convergence speed, not correctness).
    const float Safety   = 0.75;
    // Shadow only: the fixed step while the ray is inside the solid (d *
    // Safety is negative there and would step backwards). Small enough to
    // stay under the eps hit tolerance at the exit (no false "grazing" hit),
    // large enough to bound the step count - the solid is at most the
    // amplitude deep, and a shadow ray always has an upward component
    // (ShadowPass skips ndl <= 0) so it exits.
    const float InteriorStep = 0.5;

    // d is the height ABOVE the surface: positive when the sample point is
    // above the terrain (the normal state), ~0 at the surface, negative once
    // the march has overshot it. (h - p.y) would be negative in the normal
    // state - do not flip the sign when reading this loop.
    float  t     = 0.0;
    float  tPrev = 0.0;    // last distance known to be above the surface
    bool   inside = false; // shadow only: the ray is inside the solid (see header)
    for (int i = 0; i < MaxSteps; i++)
    {
        vec3 p = ro + rd * t;
        // LOD: the ray is unit-length, so t IS the camera-to-sample distance;
        // far samples march with fewer octaves (TERRAIN_LOD_*).
        float d = p.y - TerrainHeight(p.xz, t);   // height above the surface
        if (d < 0.0)
        {
            if (inside)
            {
                // Still inside the solid: fixed step toward the exit (d *
                // Safety is negative here and would step backwards). If the
                // horizon runs out inside, the ray spent the whole march in
                // the terrain - report the (large) t as an occluder, not a
                // miss: the light path was blocked.
                t += InteriorStep;
                if (t >= MaxDist) return t;
                continue;
            }

            // Above -> inside: a downward surface crossing at ~t.
            if (shadow && t <= SHADOW_HIT_TOL)
            {
                // ACNE: the crossing is the point's OWN local surface within
                // the tolerance (see the function header) - not an
                // occluder. Enter the interior and keep marching; the NEXT
                // crossing (after the ray exits) is the real one.
                inside = true;
                t += InteriorStep;
                if (t >= MaxDist) return t;
                continue;
            }

            // Overshot the surface on a steep slope: the surface lies
            // between tPrev (above) and t (below). The heightfield is
            // continuous in t, so bisect to the crossing instead of
            // reporting a miss (misses would show as sky holes).
            // 8 iterations refine the overshoot well below the eps hit
            // tolerance (0.02 + 0.005*t) at every range, so the landing
            // point stays consistent with the hit test - while the old 16
            // paid 2x the full-Fbm calls (each iteration is a TerrainHeight
            // eval) for precision the tiled texture cannot show.
            float lo = tPrev, hi = t;
            for (int j = 0; j < 8; j++)
            {
                float mid = 0.5 * (lo + hi);
                vec3 pm = ro + rd * mid;
                if (pm.y - TerrainHeight(pm.xz, mid) > 0.0) lo = mid; else hi = mid;
            }
            vec3 ph = ro + rd * hi;
            uv = ph.xz - TerrainData[0].xz;
            return hi;
        }

        // Above the surface: a shadow ray (re)entered open space here - the
        // exit of the interior. If the interior span stretched beyond the
        // acne tolerance, the ray was inside the solid for the part of its
        // path past the origin (the peak the point looks up at) - the light
        // path is blocked: occluded. A shallow span that ends within the
        // tolerance is genuine acne - keep marching normally (this sample
        // becomes the new "known above" (tPrev) for any later bisection).
        if (inside && t > SHADOW_HIT_TOL)
            return t;
        inside = false;

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

// Primary-ray intersection: full detail, sub-surface origins are a miss.
float IntersectTerrain(vec3 ro, vec3 rd, out vec2 uv)
{
    return MarchTerrain(ro, rd, false, uv);
}

// Shadow-ray intersection (the driver's IntersectSceneShadow): hit distance
// only - same march, relaxed sub-surface guard, self-crossings are marched
// through to the real occluder (see MarchTerrain), the uv latch is unused.
float IntersectTerrainShadow(vec3 ro, vec3 rd)
{
    vec2 uv;
    return MarchTerrain(ro, rd, true, uv);
}

// ---------------------------------------------------------------------------
// Shading
// ---------------------------------------------------------------------------

// Terrain material: the ALBEDO (tiled texture). Lighting (sun diffuse +
// ray-traced hard shadows + the ambient floor) is applied by the driver's
// ApplyLighting call (Raymarcher/Lighting.inc), so every family - terrain
// and planes - lights identically. *uv is the LOCAL xz, world units,
// untiled (see IntersectTerrain).
vec3 ShadeTerrain(vec2 localXz)
{
    return texture(TerrainTex, localXz * TerrainData[2].xy).rgb;
}
