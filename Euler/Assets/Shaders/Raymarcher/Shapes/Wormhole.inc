// Wormhole shape family for the raymarcher.
//
// Included from Raymarcher.comp. A wormhole mouth has two parts:
//
//   1. The EVENT HORIZON: a sphere of radius Rs (WormholeData.w) that
//      captures every light ray that reaches it (a pixel whose ray is
//      captured renders the black disc with a photon-ring rim, see
//      ShadeWormhole()). The horizon is added to the driver's closest-hit
//      race like any other family (IntersectWormhole below).
//
//   2. The GRAVITATIONAL FIELD: every ray that passes within
//      WORMHOLE_BEND_RADIUS of the mouth center has its direction bent.
//      The bending is NOT analytic - it is integrated per pixel: the
//      driver (main() in Raymarcher.comp) advances the ray in small steps
//      and, between IntersectScene() steps, rotates Dir toward the center
//      (the "bend ray.Dir between IntersectScene() steps" extension point
//      documented in the driver header). This file owns the field
//      parameters and the helpers the driver's loop uses:
//
//         RayBendsAroundWormhole()   - cheap gate: does this ray enter the field?
//         WormholeBendPreflight()    - skip straight to the field entrance
//         ShadeWormhole()            - black disc + photon-ring rim
//
// PHYSICS: the per-step deflection uses the Newtonian photon equation with
// gravitational parameter mu = Rs (acceleration a = -Rs * d / r^3). For a
// point mass this reproduces the general-relativistic deflection angle of a
// photon exactly: delta = 2 Rs / b (GR's light bending is twice the
// Newtonian value for massive particles; setting mu = Rs - the
// Schwarzschild radius - makes the two agree). Consequences the integration
// produces for free:
//
//   - rays with impact parameter b < 3*sqrt(3)/2 * Rs ~ 2.6 Rs spiral in
//     and cross the horizon (captured -> black disc),
//   - rays just above that critical value orbit the throat one or more
//     times before escaping -> the bright photon ring / Einstein ring,
//   - rays far out bend weakly (delta = 2 Rs / b) -> lensed sky/terrain.
//
// Only ONE wormhole exists per scene: the field is a per-pixel global
// effect (like the terrain), not an arrayed primitive.
//
// NOTE: no #version directive here - included files must never carry one;
// #version must remain the first line of the main (expanding) shader.

// Family ID latched by the driver's IntersectScene() so main() can dispatch
// shading (see Plane.inc).
const int FamilyWormhole = 2;

// Per-wormhole uniform data (one vec4):
//   [0] center.xyz, Rs (event horizon radius)
// See WormholePrimitive.WriteInto for the C# side.
uniform int   WormholeEnabled;
uniform vec4  WormholeData;

// The field extends to 7*Rs: at the edge a ray is deflected by
// delta = 2 Rs / (7 Rs) ~ 16 degrees - enough for the lensed halo to be
// visible, small enough that most of the screen stays on the exact
// analytic trace.
const float WORMHOLE_BEND_RADIUS_FACTOR = 7.0;

// Max deflection steps per pixel. Near-critical rays orbit the throat for
// several turns before escaping or being captured and would like more;
// typical bent rays need < 60 steps. The budget is kept modest on purpose:
// the ring pixels that exhaust it are a thin band that would otherwise
// dominate the compute dispatch (worst-case work per pixel), and an
// exhausted ray falls back to an exact straight tail trace, so the cost
// cap costs only a slightly softer photon ring, not correctness.
#define WORMHOLE_MAX_BEND_STEPS 160

vec3  WormholeCenter()     { return WormholeData.xyz; }
float WormholeRadius()     { return WormholeData.w; }
float WormholeBendRadius() { return WORMHOLE_BEND_RADIUS_FACTOR * WormholeRadius(); }
float WormholeBendRadius2(){ float b = WormholeBendRadius(); return b * b; }

// ---------------------------------------------------------------------------
// Event horizon (the family's "surface")
// ---------------------------------------------------------------------------

// Analytic ray/sphere intersection against the event horizon. Returns the
// hit distance t (or -1 on miss). The family's rim shading is computed by
// the driver from the hit POSITION (ShadeWormhole(normalize(hitPos - center),
// rayDir)), so *uv carries no family-specific data.
float IntersectWormhole(vec3 ro, vec3 rd, out vec2 uv)
{
    uv = vec2(0.0);

    vec3  oc = ro - WormholeCenter();
    float rs = WormholeRadius();
    float b  = dot(oc, rd);
    float c  = dot(oc, oc) - rs * rs;
    float disc = b * b - c;
    if (disc <= 0.0) return -1.0;

    float sq = sqrt(disc);
    float t  = -b - sq;              // near surface
    if (t <= 0.0) t = -b + sq;       // degenerate: camera inside the sphere
    if (t <= 0.0) return -1.0;

    return t;
}

// ---------------------------------------------------------------------------
// Bending field helpers (used by the driver's bent-ray loop)
// ---------------------------------------------------------------------------

// The driver's fog (ApplyFog in Raymarcher.comp) is 1 - exp(-0.010 * t),
// which is >= 99% past ~460 units. A mouth farther away than that is
// invisible through the fog, so the (expensive) bent pass is skipped
// entirely: lensing stops rendering exactly when the wormhole is past the
// fog distance. (The event horizon disc itself is still traced analytically
// on the straight path - it stays "eternally black" and unfogged.)
const float WORMHOLE_FOG_CULL_DIST  = 460.0;
const float WORMHOLE_FOG_CULL_DIST2 = WORMHOLE_FOG_CULL_DIST * WORMHOLE_FOG_CULL_DIST;

// Cheap gate for the driver's main(): true when the straight ray (ro, rd)
// ever comes within the bend radius of the mouth, and the camera is within
// the fog cull distance of it (past that the mouth is invisible anyway).
bool RayBendsAroundWormhole(vec3 ro, vec3 rd)
{
    vec3 oc = ro - WormholeCenter();
    if (dot(oc, oc) >= WORMHOLE_FOG_CULL_DIST2) return false;  // mouth lost in fog
    float b = dot(oc, rd);
    // Closest point on the ray (t >= 0) to the mouth center.
    vec3 closest = (b < 0.0) ? oc : (oc + rd * b);
    return dot(closest, closest) < WormholeBendRadius2();
}

// Advances ro straight along rd to the ENTRANCE of the bend sphere and
// returns the skipped distance (0 when the camera is already inside the
// field or the ray never enters it). Lets the driver's loop start where
// the bending actually matters.
float WormholeBendPreflight(vec3 ro, vec3 rd, out vec3 newRo)
{
    newRo = ro;

    vec3  oc = ro - WormholeCenter();
    float b  = dot(oc, rd);
    if (b >= 0.0) return 0.0;               // ray diverges from the field
    float c = dot(oc, oc) - WormholeBendRadius2();
    if (c < 0.0)  return 0.0;               // camera already inside the field
    float disc = b * b - c;
    if (disc <= 0.0) return 0.0;            // never enters (defensive)

    float tEnter = -b - sqrt(disc);
    newRo = ro + rd * tEnter;
    return tEnter;
}

// ---------------------------------------------------------------------------
// Shading
// ---------------------------------------------------------------------------

// Wormhole shading: the disc is black (nothing escapes the horizon) with a
// hot photon-ring rim. *localDir is the outward local direction at the hit
// (normalize(hitPos - center)); *rayDir is the (bent) ray direction at the
// hit - grazing rays light up the rim.
vec3 ShadeWormhole(vec3 localDir, vec3 rayDir)
{
    vec3  n = normalize(localDir);
    float grazing = 1.0 - max(dot(n, -rayDir), 0.0);   // 0 head-on, 1 grazing

    vec3 hot  = vec3(1.00, 0.80, 0.50);   // incandescent thin rim
    vec3 cool = vec3(0.30, 0.50, 1.00);   // wide cool halo
    return hot * pow(grazing, 8.0) * 1.30
         + cool * pow(grazing, 3.0) * 0.30;
}
