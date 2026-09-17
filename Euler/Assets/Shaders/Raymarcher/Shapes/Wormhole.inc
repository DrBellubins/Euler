// Traversable wormhole: a 4D spacetime modification for the raymarcher.
//
// Included from Raymarcher.comp. Port of the Shadertoy traversable wormhole
// (fork of "Simple wormhole" by A_Toaster): space is a 4D HYPERSURFACE made
// of two flat 3D sheets (w = +Rmin and w = -Rmin, the two "universes") joined
// by a torus tube around the rim circle of radius Rmaj at w = 0 (the throat).
// Light rays live ON this hypersurface - a ray that enters the mouth (the 3D
// ball of radius Rmaj) is carried around the throat and emerges on the other
// sheet, where it can see the other universe.
//
// This file owns the 4D space geometry and the 4D camera uniform. The
// per-pixel FLOW (gate, analytic pre-test, throat march, sheet handoff) is in
// the driver (Raymarcher.comp), which relies on this file's key property:
//
//   A 4D ray on the throat cannot reach a scene object before it leaves the
//   curved region. Objects sit on the flat sheets; a ray on the tube meets a
//   sheet level (|w| = Rmin) only at the rim (|xyz| = Rmaj). So the throat
//   march needs NO per-step scene SDF: it steps by the SPACE step limit only
//   (cheap - a handful of sqrt/dot per step, unlike an FBM march), and the
//   driver takes over with the exact analytic path at the exit point.
//
// The camera is 4D-AUTHORITATIVE (WorldCam in C#): a 3D camera degenerates
// when it points into the mouth (its view direction then has no 3D
// projection), so the camera state lives in 4D and the 3D HUD camera is
// derived from it (xyz of the 4D position/basis).
//
// Only ONE wormhole exists per scene: it is a per-pixel global effect (like
// the terrain), not an arrayed primitive.
//
// NOTE: no #version directive here - included files must never carry one;
// #version must remain the first line of the main (expanding) shader.

// Per-wormhole uniform data (2 vec4):
//   [0] center.xyz, rMaj (rim / mouth radius)
//   [1] rMin (tube radius; the sheets sit at w = +-rMin), 0, 0
// See WormholePrimitive.WriteInto for the C# side.
uniform int  WormholeEnabled;   // 0/1: the scene has a wormhole
uniform vec4 WormholeData[2];

// 4D camera: columns (Forward, Left, Up, Pos) in WORMHOLE-LOCAL space
// (xyz = world - center, w = the fourth axis). See WorldCam (C#) for the
// state machine that keeps this basis on the hypersurface.
uniform mat4 WormholeCam;

float WhRmaj()   { return WormholeData[0].w; }
float WhRmin()   { return WormholeData[1].x; }
vec3  WhCenter() { return WormholeData[0].xyz; }

// Max throat-march steps per pixel. A full sheet-to-sheet crossing around
// the inner wall is ~pi*(rMaj - rMin) + 2*rMin of arc; at the 0.1*rMin step
// floor that is ~50-55 steps for the scene's sizing (rMaj=2, rMin=1: ~51),
// and the flat-sheet steps only grow from there, so a single crossing needs
// well under 80. 96 still leaves ~60% headroom (a crossing plus a partial
// second loop); an exhausted ray falls back to the exact 3D path (the
// driver), so the cap costs only a thin band of pixels, not correctness.
// Kept modest on purpose: the per-step cost here is a few sqrt/dot (no scene
// SDF), and the real per-pixel cost is the terrain march at the exit, which
// the terrain's distance LOD (TERRAIN_LOD_*) keeps cheap.
// 384: the longest throat ride (a diameter chord plus the half-tube arc)
// is ~sqrt((2 rMaj)^2 + (pi rMin)^2) of 4D path - about 5 units for the
// default 2/1 radii - which at the 0.02 * rMin step below takes ~254
// steps, plus a few large flat-sheet approach steps. The cap only costs a
// thin band of looping pixels, and the per-step cost is a few sqrt/dot
// (no scene SDF).
#define WH_MAX_STEPS 384

// ---------------------------------------------------------------------------
// 4D space geometry (the hypersurface the camera and rays live on)
// ---------------------------------------------------------------------------

// The space's 4D normal at p. On the flat sheets it is +-w; in the curved
// region it points from the nearest rim point AWAY from it, toward p (the
// tube is a torus around the rim circle, so its surface normal is the radial
// direction in the (radial, w) cross-section plane). The snap in
// SpaceNormalize is p -= s * n, which with THIS sign pulls a point onto the
// surface from either side (the Shadertoy original's exact convention).
vec4 SpaceNormal(vec4 p)
{
    if (dot(p.xyz, p.xyz) >= WhRmaj() * WhRmaj())
        return vec4(0.0, 0.0, 0.0, (p.w >= 0.0) ? 1.0 : -1.0);

    float rl = length(p.xyz);
    // Guard the w axis: at p.xyz = 0 every rim point is equidistant, so any
    // radial direction is a valid normal (the surface is symmetric there).
    vec3 dir3 = (rl > 1e-5) ? p.xyz / rl : vec3(0.0, 1.0, 0.0);
    return normalize(p - vec4(dir3 * WhRmaj(), 0.0));
}

// Signed distance to the hypersurface: sheet distance (|w| - rMin) in the
// flat region, torus distance (distance to the rim circle - rMin) in the
// curved region. Used ONLY for the snap in SpaceNormalize (the march's step
// limit is SpaceStepLimit), so the branch's small discontinuity at the rim
// is harmless - the two branches agree there (the tube meets the sheets at
// |xyz| = Rmaj, w = +-Rmin).
float SpaceSdf(vec4 p)
{
    if (dot(p.xyz, p.xyz) >= WhRmaj() * WhRmaj())
        return abs(p.w) - WhRmin();

    float rl = length(p.xyz);
    vec3 dir3 = (rl > 1e-5) ? p.xyz / rl : vec3(0.0, 1.0, 0.0);
    return length(vec4(dir3 * WhRmaj(), 0.0) - p) - WhRmin();
}

// Snaps p ONTO the hypersurface and constrains d to be TANGENT to it (one
// Gram-Schmidt step against the space normal). This is what keeps the 4D
// march on the surface of space - the non-Euclidean heart of the wormhole:
// the ray is advanced in a straight 4D line, then re-projected, so between
// steps it "curves" around the throat.
void SpaceNormalize(inout vec4 p, inout vec4 d)
{
    vec4 n = SpaceNormal(p);
    p -= SpaceSdf(p) * n;
    vec4 q = d - n * dot(d, n);
    float l = length(q);
    // Degenerate case (d parallel to the normal, e.g. looking straight
    // through the mouth from its exact center): keep d as-is. A pixel or
    // two, and the next step re-snaps.
    d = (l > 1e-6) ? q / l : d;
}

// Step limit for the 4D march: constant (0.02 * rMin) in the curved region
// and growing with distance from the rim on the sheets, where the space is
// flat and big steps are safe.
//
// The 0.02 factor matters: the snap march converges to a definite limit
// curve as step -> 0, and the tube ride accumulates a direction error of
// ~O(step) (the exit 3D direction tilts by up to ~0.05 rad at step 0.1).
// That tilt is what flips grazing handoff rays between hit and sky in a
// narrow band at the rim - the 1-3px sky ring around the mouth that
// shifted with camera angle. At 0.02 the march matches the converged limit
// closely and the rim band is stable.
float SpaceStepLimit(vec4 p)
{
    return max(0.02 * WhRmin(), length(p.xyz) - WhRmaj());
}

// True when p sits on a flat sheet (|w| = rMin after the snap) OUTSIDE the
// mouth ball: the space there is a flat 3D hyperplane, so a straight exact
// 3D path is valid for a ray from p (and its 3D projection is the 4D ray
// itself - d.w = 0 on the sheet).
//
// The ball guard is essential: a camera on the throat's inner wall
// (|xyz| < rMaj, |w| ~= rMin) satisfies the w test but is NOT on a flat
// sheet - the 3D path there is wrong (the straight ray never "enters"
// the ball, so the wormhole disappears when the player crosses the rim).
// Such a camera must be marched from the inside instead.
bool WhOnSheet(vec4 p)
{
    return abs(p.w) >= WhRmin() - 1e-4
        && dot(p.xyz, p.xyz) >= WhRmaj() * WhRmaj();
}
