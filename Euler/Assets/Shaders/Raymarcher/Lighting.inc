// Ray-traced lighting for the raymarcher.
//
// Included from Raymarcher.comp LATE - just before main, after the driver's
// trace machinery (IntersectScene / IntersectSceneShadow / WhMarch /
// TraceSheetRay). That placement is deliberate: the lighting passes trace
// SECONDARY rays through the SAME scene flow as the primary ray, so every
// pass sees the exact same bent (wormhole) space.
//
// THE PASS MODEL (the future-proofing): lighting is a set of passes over the
// closest hit. Each pass is one (or more) secondary rays traced through
// TraceSheetRay, plus a contribution term:
//
//   LIGHT_PASS_SHADOWS (implemented): HARD SHADOWS - one shadow ray per
//       light, aimed at the sun, through the full scene. A hit removes the
//       light's diffuse contribution entirely (single pass, no penumbra).
//   LIGHT_PASS_REFLECTIONS (reserved): trace a reflection ray
//       (reflect(-rayDir, nrm)) and mix its color into the surface color.
//   LIGHT_PASS_GI (reserved): hemisphere sample(s) around nrm for indirect
//       light (would replace the ambient floor below).
//
// ADDING A PASS: (1) add its bit to the LIGHT_PASS_* list here and the C#
// LightPasses enum (Raymarcher.cs), (2) write its trace + contribution
// function in this file, (3) add its term to ApplyLighting(). Nothing
// outside this file changes: families return an ALBEDO and the driver calls
// ApplyLighting() with the hit data - exactly where it does today.
//
// NOTE: no #version directive here - included files must never carry one;
// #version must remain the first line of the main (expanding) shader.

// ---------------------------------------------------------------------------
// Sun (directional light) uniforms
// ---------------------------------------------------------------------------
// The scene's sun is owned in C# (Sun.cs - a 3D object, movable in xyz and
// rotatable; its rotation math lives in quaternion space, see the class
// docs). The raymarcher uploads it per frame (Raymarcher.AddSun/Draw):
//   SunData[0] = toSun.xyz (unit dir surface -> sun), sunIntensity
//   SunData[1] = sunColor.rgb, 0
uniform int  SunEnabled;
uniform vec4 SunData[2];

// Enabled ray-traced lighting passes (bitmask, uploaded from the C#
// LightPasses enum).
#define LIGHT_PASS_SHADOWS     1
#define LIGHT_PASS_REFLECTIONS 2
#define LIGHT_PASS_GI          4
uniform int LightingPasses;

// The direction from a surface toward the sun (normalized; a zero vector
// falls back to +Y, matching the old per-terrain sun's behavior).
vec3 SunDir()
{
    vec3 d = SunData[0].xyz;
    float len = length(d);
    return (len > 1e-5) ? d / len : vec3(0.0, 1.0, 0.0);
}

// ---------------------------------------------------------------------------
// Pass: hard shadows (LIGHT_PASS_SHADOWS)
// ---------------------------------------------------------------------------

// Normal offset of the shadow ray origin, lifted off the hit surface: keeps
// the hit geometry from occluding itself. Single-sided planes cannot
// self-hit at all (the offset is along their normal, and the ray then
// LEAVES the plane), so the offset costs only a sub-pixel shadow detachment
// on flat surfaces (peter-panning), not acne.
const float SHADOW_NORMAL_BIAS = 0.05;

// A shadow hit closer than this total path length is the point's OWN
// surface, not a separate occluder: the heightfield march reports a
// sub-surface / self-slope origin (the acne case) as a near-zero crossing -
// and then marches through the interior to the real occluder (see
// MarchTerrain in Terrain.inc, which uses the same tolerance to decide when
// a crossing is the point's own surface) - and grazing re-crossings of the
// same surface do too. Treat it as lit.
// SHADOW_HIT_TOL itself is declared in Terrain.inc (included earlier):
// MarchTerrain needs it before Lighting.inc is expanded, and GLSL requires
// the declaration before its first use.

// The hard-shadow pass for the sun: ONE ray from the hit point toward the
// sun, traced through the full scene flow (TraceSheetRay). Because the
// shadow ray bends through the wormhole throat exactly like the primary
// ray, light can travel around the throat and geometry on the OTHER sheet
// casts shadows on this one - the shadows are visually correct in the
// non-Euclidean space, not just in flat 3D.
//
// Returns 1.0 when the sun is unoccluded, 0.0 when shadowed (hard cut).
float ShadowPass(vec3 pos, vec3 nrm, int universe)
{
    vec3 toSun = SunDir();

    // Lift the ray origin off the surface (acne guard, see the constants).
    vec3 ro = pos + nrm * SHADOW_NORMAL_BIAS;

    float t;
    if (WormholeEnabled > 0)
    {
        // 4D origin on the hit's sheet: the sheet is flat, so the shadow
        // ray's 4D direction is its 3D direction with w = 0 (it only gains
        // a w component by entering the mouth, which WhMarch handles).
        vec4 ro4 = vec4(ro - WhCenter(), float(universe) * WhRmin());
        vec4 rd4 = vec4(toSun, 0.0);

        vec2  uv;
        int   family;
        vec3  nrm2, hitPos;
        int   endUniverse;
        vec3  skyDir;
        if (TraceSheetRay(ro4, rd4, true, universe,
                          uv, t, family, nrm2, hitPos, endUniverse, skyDir))
            return (t >= SHADOW_HIT_TOL) ? 0.0 : 1.0;
    }
    else if (IntersectSceneShadow(Ray(ro, toSun), universe, t))
        return (t >= SHADOW_HIT_TOL) ? 0.0 : 1.0;

    return 1.0;
}

// ---------------------------------------------------------------------------
// Lighting entry point (the driver calls this per hit, after the family
// albedo and before fog)
// ---------------------------------------------------------------------------

// Ambient floor: a uniform sky-light stand-in so shadowed surfaces don't go
// to pure black. A future LIGHT_PASS_GI replaces it with real indirect
// light; until then it keeps shadow contrast readable (it also matches the
// ambient floor the terrain carried before lighting moved here).
const vec3 AMBIENT = vec3(0.35);

// Applies the lighting to a hit: ambient + the sun's diffuse (Lambert)
// contribution modulated by the enabled ray-traced passes. `rayDir` is the
// final 3D direction of the primary ray at the hit (used by future passes -
// reflections need it; diffuse does not). `pos` / `nrm` / `universe` are
// the hit's world-space position, shading normal and sheet (the shadow ray
// starts on the hit's sheet, so the light can cross universes correctly).
vec3 ApplyLighting(vec3 albedo, vec3 rayDir, vec3 pos, vec3 nrm, int universe)
{
    vec3 light = vec3(0.0);

    // --- Sun: diffuse + the enabled ray-traced passes ---
    if (SunEnabled > 0)
    {
        vec3  toSun     = SunDir();
        float intensity = SunData[0].w;
        vec3  sunColor  = SunData[1].xyz;

        float ndl = max(dot(nrm, toSun), 0.0);

        // Hard shadow pass: 1.0 lit / 0.0 shadowed. Skipped when the light
        // contributes nothing (ndl = 0) - the shadow ray would be wasted.
        float shadow = ((LightingPasses & LIGHT_PASS_SHADOWS) != 0 && ndl > 0.0)
                       ? ShadowPass(pos, nrm, universe)
                       : 1.0;

        light += sunColor * intensity * ndl * shadow;

        // FUTURE PASSES ADD HERE - each is a secondary-ray trace through
        // TraceSheetRay (see the file header for the pass model), e.g.:
        //   reflections: trace reflect(-rayDir, nrm) and mix its color,
        //   GI:          hemisphere sample(s) toward nrm, add the indirect.
    }

    return albedo * (AMBIENT + light);
}
