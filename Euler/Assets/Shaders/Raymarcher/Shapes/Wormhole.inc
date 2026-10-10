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

// Hard ceiling on throat-march steps per pixel. The longest throat ride (a
// diameter chord plus the half-tube arc) is ~sqrt((2 rMaj)^2 + (pi rMin)^2)
// of 4D path - about 5 units for the default 2/1 radii - which at the
// WH_STEP_FINE floor below takes ~254 steps; a ray that loops the torus can
// need more. An exhausted ray falls back to the exact 3D path (the driver),
// so the cap costs only a thin band of pixels, not correctness.
//
// IMPORTANT - why the driver ALSO gives every ray its own (much smaller)
// budget, see WhThroatBudget in Raymarcher.comp: a GPU runs a warp in
// lockstep, so a workgroup pays its SLOWEST lane. With one global cap the
// inside-the-mouth cost tracks the CAP rather than the average ray - which
// is what collapsed the frame rate when the camera entered the mouth
// (neighbouring pixels differ enormously in throat path length: one exits in
// a few steps, its neighbour grazes the rim and runs to 384).
#define WH_MAX_STEPS 384

// Shadow-ray throat march: coarse floor, small budget. A shadow ray answers
// a binary question (is the light path blocked?), so the sub-pixel direction
// error a coarse step introduces cannot show up in the image - unlike the
// primary ray, where it flips grazing handoff pixels between "terrain" and
// "sky" at the rim. This is the cheapest large win inside the mouth: with
// hard shadows on, EVERY lit pixel traces a second full throat march.
#define WH_SHADOW_STEP 0.15
#define WH_SHADOW_MAX_STEPS 96

// Primary throat step, as a fraction of rMin. The 0.02 factor matters at the
// RIM: the snap march converges to a definite limit curve as step -> 0, and
// the exit 3D direction drifts by ~O(step) (up to ~0.05 rad at step 0.1).
// That drift is what flips grazing handoff rays between hit and sky in a
// narrow band at the rim - the 1-3px sky ring around the mouth that shifted
// with camera angle. So the fine floor is kept in the shell around the
// |xyz| = rMaj crossing (WH_RIM_BAND): that is the only place where the
// march's direction error is visible. Deep inside the ball the ray rides the
// tube and that is where the step COUNT lives (1-12 units of tube for a
// camera inside the mouth: 50-600 steps per pixel at the fine floor), so a
// coarse multiple of the fine step is used there. Set WH_STEP_COARSE to 1.0
// to go back to a uniform fine march.
#define WH_STEP_FINE 0.02
#define WH_STEP_COARSE 3.0
#define WH_RIM_BAND 0.15   // fine-step shell around the rim, fraction of rMaj

// ---------------------------------------------------------------------------
// 4D space geometry (the hypersurface the camera and rays live on)
// ---------------------------------------------------------------------------

// The space's 4D normal at p. On the flat sheets it is +-w; in the curved
// region it points from the nearest rim point AWAY from it, toward p (the
// tube is a torus around the rim circle, so its surface normal is the radial
// direction in the (radial, w) cross-section plane). The snap in
// SpaceNormalize is p -= s * n, which with THIS sign pulls a point onto the
// surface from either side (the Shadertoy original's exact convention).
// NOTE: SpaceNormal and SpaceSdf below are the REFERENCE form of the space
// geometry. The march no longer calls them (SpaceNormalize inlines both -
// see the fusion note there); they are kept because they state the geometry
// readably and GameEngine/WorldCam.cs mirrors them in C# (keep in sync).
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
// FUSED form of SpaceNormal + SpaceSdf + the tangent projection.
//
// The three-call version reads the same geometry three times: SpaceNormal
// computes length(p.xyz) and normalizes (p - rim), SpaceSdf recomputes the
// same length and the same rim point, and the projection normalizes again -
// ~4 sqrts and ~10 divides per throat step, paid by every pixel inside the
// mouth. Here the rim point and the distance to it are computed ONCE and a
// single sqrt feeds both the normal and the SDF. Identical results, roughly
// a third less per-step cost.
void SpaceNormalize(inout vec4 p, inout vec4 d)
{
    float rMaj = WhRmaj();
    float rMin = WhRmin();
    float rl2  = dot(p.xyz, p.xyz);

    vec4  n;
    float s;
    if (rl2 >= rMaj * rMaj)
    {
        // Flat sheet: the normal is +-w and the distance is to the sheet.
        n = vec4(0.0, 0.0, 0.0, (p.w >= 0.0) ? 1.0 : -1.0);
        s = abs(p.w) - rMin;
    }
    else
    {
        // Curved region: dv points from the nearest rim point to p, so its
        // length is the distance to the rim circle - the tube SDF is that
        // length minus rMin, and its direction is the (unit) normal.
        float rl   = sqrt(rl2);
        vec3  dir3 = (rl > 1e-5) ? p.xyz / rl : vec3(0.0, 1.0, 0.0);
        vec4  dv   = p - vec4(dir3 * rMaj, 0.0);
        float dl   = length(dv);
        n = dv / max(dl, 1e-6);
        s = dl - rMin;
    }

    p -= s * n;
    vec4 q = d - n * dot(d, n);
    float l = length(q);
    // Degenerate case (d parallel to the normal, e.g. looking straight
    // through the mouth from its exact center): keep d as-is. A pixel or
    // two, and the next step re-snaps.
    d = (l > 1e-6) ? q / l : d;
}

// Step limit for the 4D march, in three zones. stepFloor is the caller's
// per-ray floor: WH_STEP_FINE * rMin for primary rays, WH_SHADOW_STEP * rMin
// for shadow rays (see the constants above).
//
//   outside the mouth ball            - flat space: steps grow with the
//                                       distance from the rim, so the
//                                       approach costs a handful of steps
//                                       (this is why a sheet camera is
//                                       cheap),
//   inside the ball, within           - the FINE floor: the only place where
//   WH_RIM_BAND of rMaj                 the march's direction error is
//                                       visible (it sets the handoff
//                                       direction, and with it the rim ring),
//   inside the ball, elsewhere        - the COARSE floor: the ray is riding
//                                       the tube, and this is where the step
//                                       count actually lives.
//
// The coarse tier trades a little throat accuracy for a 3x shorter march
// inside the mouth. The drift it adds is bounded (the snap march's position
// error grows like pathLength * step / 8 - about 0.1 world units for a full
// tube ride at the default radii) and it is confined to the interior, where
// nothing is being handed off. If the rim ring or the throat view starts to
// wobble, set WH_STEP_COARSE to 1.0.
float SpaceStepLimit(vec4 p, float stepFloor)
{
    float rMaj = WhRmaj();
    float out3 = length(p.xyz) - rMaj;   // >= 0 on the sheets, < 0 inside
    if (out3 >= 0.0) return max(stepFloor, out3);
    if (-out3 < WH_RIM_BAND * rMaj) return stepFloor;
    return stepFloor * WH_STEP_COARSE;
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
