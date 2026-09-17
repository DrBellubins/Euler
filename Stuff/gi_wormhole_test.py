#!/usr/bin/env python3
"""
Does ray-traced GI play nicely with the Euler wormhole?

Theory under test (from the user):
  "GI through the wormhole will look screen-space: it would only ever emit
   towards the camera and change based on whatever is visible to the camera,
   instead of light being emitted from behind it realistically."

Test design
-----------
The engine's wormhole ray flow (Wormhole.inc + the wormhole branch of
Raymarcher.comp main()) is ported EXACTLY into this script, including:
  - WhOnSheet (with the mouth-ball guard), the tEnter pre-test gate,
  - WhMarch (WH_MAX_STEPS = 384, step floor 0.02*rMin, budget-exhaustion
    3D-projection fallback),
  - the universe flag on scene intersection (the 'orange cube' exists ONLY
    on the lower sheet, universe = -1, like the plane family gate in the
    shader).

Scene: the real TestScene wormhole (local-space origin, rMaj=2, rMin=1,
sheets at w=+/-1) plus one box ('the bright orange cube') on the LOWER sheet.

EXPERIMENT A (the decisive one): fix a receiver point P on the upper sheet.
Irradiance(P) = fraction of a stratified hemisphere of bounce rays from P
that hit the cube (each ray traced through the full wormhole pipeline).
Then place the CAMERA at three very different positions/angles (where the
cube's apparent image through the mouth differs a lot) and recompute
Irradiance(P) every time.
  * Irradiance identical across camera configs  -> GI is world-space, the
    screen-space theory is REFUTED.
  * Irradiance changes with the camera           -> theory CONFIRMED.

EXPERIMENT B (the user's secondary observation): for several receiver points
- one where the cube's image through the mouth is distorted/smeared, one
where it looks normal - render what a virtual camera sitting at that point
sees, and compare the apparent cube area with the GI fraction there.
  * If 'distorted/larger image' correlates with 'more GI' at that point,
    the effect is real receiver-position-dependent lensing physics
    (a magnifying glass concentrates light) - NOT a screen-space artifact.
"""
import math

# ----------------------------------------------------------------------
# Wormhole space (Wormhole.inc - EXACT replica of the current shader)
# ----------------------------------------------------------------------
RMaj, RMin = 2.0, 1.0          # TestScene: WormholePrimitive(rmaj: 2.0, rmin: 1.0)
WH_MAX_STEPS = 384             # Wormhole.inc: #define WH_MAX_STEPS 384

def V(*c): return c
def vadd(a, b):  return (a[0]+b[0], a[1]+b[1], a[2]+b[2], a[3]+b[3])
def vsub(a, b):  return (a[0]-b[0], a[1]-b[1], a[2]-b[2], a[3]-b[3])
def vmul(a, s):  return (a[0]*s, a[1]*s, a[2]*s, a[3]*s)
def vdot(a, b):  return a[0]*b[0] + a[1]*b[1] + a[2]*b[2] + a[3]*b[3]
def vnorm(a):    return math.sqrt(vdot(a, a))

def space_normal(p):
    x, y, z, w = p
    if x*x + y*y + z*z >= RMaj*RMaj:
        return (0.0, 0.0, 0.0, 1.0 if w >= 0.0 else -1.0)
    rl = math.sqrt(x*x + y*y + z*z)
    dx, dy, dz = (x/rl, y/rl, z/rl) if rl > 1e-5 else (0.0, 1.0, 0.0)
    v = (x - dx*RMaj, y - dy*RMaj, z - dz*RMaj, w)
    l = vnorm(v)
    return (v[0]/l, v[1]/l, v[2]/l, v[3]/l)

def space_sdf(p):
    x, y, z, w = p
    if x*x + y*y + z*z >= RMaj*RMaj:
        return abs(w) - RMin
    rl = math.sqrt(x*x + y*y + z*z)
    dx, dy, dz = (x/rl, y/rl, z/rl) if rl > 1e-5 else (0.0, 1.0, 0.0)
    v = (x - dx*RMaj, y - dy*RMaj, z - dz*RMaj, w)
    return math.sqrt(v[0]*v[0] + v[1]*v[1] + v[2]*v[2] + v[3]*v[3]) - RMin

def space_normalize(p, d):
    n = space_normal(p)
    sd = space_sdf(p)
    p = (p[0] - sd*n[0], p[1] - sd*n[1], p[2] - sd*n[2], p[3] - sd*n[3])
    dd = vdot(d, n)
    q = (d[0] - n[0]*dd, d[1] - n[1]*dd, d[2] - n[2]*dd, d[3] - n[3]*dd)
    l = vnorm(q)
    d = (q[0]/l, q[1]/l, q[2]/l, q[3]/l) if l > 1e-6 else d
    return p, d

def space_step_limit(p):
    return max(0.02 * RMin, math.sqrt(p[0]**2 + p[1]**2 + p[2]**2) - RMaj)

def wh_on_sheet(p):
    # abs(w) >= rMin - eps  AND  the mouth-ball guard (|xyz| >= rMaj)
    return abs(p[3]) >= RMin - 1e-4 and (p[0]**2 + p[1]**2 + p[2]**2) >= RMaj*RMaj

# ----------------------------------------------------------------------
# Scene: one box, LOWER SHEET ONLY (universe == -1), like the plane
# family's `if (universe >= 0)` gate in IntersectScene.
# ----------------------------------------------------------------------
CUBE_CENTER = (1.6, 1.0, 4.6)    # local space, on the FAR side of the mouth
CUBE_HS     = 1.2

def intersect_cube(ro, rd):
    tmin, tmax = -1e30, 1e30
    for i in range(3):
        if abs(rd[i]) < 1e-12:
            if abs(ro[i] - CUBE_CENTER[i]) > CUBE_HS:
                return None
        else:
            t1 = (CUBE_CENTER[i] - CUBE_HS - ro[i]) / rd[i]
            t2 = (CUBE_CENTER[i] + CUBE_HS - ro[i]) / rd[i]
            if t1 > t2: t1, t2 = t2, t1
            tmin = max(tmin, t1); tmax = min(tmax, t2)
            if tmin > tmax:
                return None
    if tmin > 0.0:
        return tmin
    if tmax > 0.0:
        return 1e-4      # origin inside the box
    return None

def intersect_scene(ro3, rd3, universe):
    """The cube lives only on the lower sheet."""
    if universe == -1:
        return intersect_cube(ro3, rd3)
    return None

# ----------------------------------------------------------------------
# WhMarch (Raymarcher.comp - EXACT replica)
# ----------------------------------------------------------------------
def wh_march(ro4, rd4):
    p4, d4 = ro4, rd4
    pathLen = 0.0
    rMaj2 = RMaj*RMaj
    inBall = (p4[0]**2 + p4[1]**2 + p4[2]**2) < rMaj2
    steps = 0
    for i in range(WH_MAX_STEPS):
        d = space_step_limit(p4)
        p4 = (p4[0] + d4[0]*d, p4[1] + d4[1]*d, p4[2] + d4[2]*d, p4[3] + d4[3]*d)
        pathLen += d
        p4, d4 = space_normalize(p4, d4)
        steps = i + 1
        nowInBall = (p4[0]**2 + p4[1]**2 + p4[2]**2) < rMaj2
        if inBall and not nowInBall:
            universe = 1 if p4[3] >= 0.0 else -1
            hit = intersect_scene(p4[:3], d4[:3], universe)
            return dict(kind='handoff', universe=universe, hit=hit,
                        pathLen=pathLen, steps=steps)
        inBall = nowInBall
    # Step budget exhausted: exact 3D path from the current point.
    universe = 1 if p4[3] >= 0.0 else -1
    l3 = math.sqrt(d4[0]**2 + d4[1]**2 + d4[2]**2)
    if l3 > 1e-4:
        dir3 = (d4[0]/l3, d4[1]/l3, d4[2]/l3)
        hit = intersect_scene(p4[:3], dir3, universe)
    else:
        hit = None
    return dict(kind='budget', universe=universe, hit=hit,
                pathLen=pathLen, steps=steps)

# ----------------------------------------------------------------------
# Full driver flow (main() wormhole branch) - a pure function of (ro4, rd4).
# NOTE: the camera appears NOWHERE below this line.
# ----------------------------------------------------------------------
def trace4d(ro4, rd4):
    universe = 1 if ro4[3] >= 0.0 else -1
    if wh_on_sheet(ro4):
        ro3 = ro4[:3]
        f3 = rd4[:3]
        l3 = math.sqrt(f3[0]**2 + f3[1]**2 + f3[2]**2)
        if l3 > 1e-4:
            rd3 = (f3[0]/l3, f3[1]/l3, f3[2]/l3)
            b  = ro3[0]*rd3[0] + ro3[1]*rd3[1] + ro3[2]*rd3[2]
            c2 = ro3[0]**2 + ro3[1]**2 + ro3[2]**2 - RMaj*RMaj
            disc = b*b - c2
            tEnter = (-b - math.sqrt(disc)) if (b < 0.0 and disc > 0.0) else -1.0
            if tEnter < 0.0:
                return dict(kind='straight', universe=universe,
                            hit=intersect_scene(ro3, rd3, universe))
            pre = intersect_scene(ro3, rd3, universe)
            if pre is not None and pre < tEnter:
                return dict(kind='pre', universe=universe, hit=pre)
            return wh_march(ro4, rd4)
        return wh_march(ro4, rd4)
    return wh_march(ro4, rd4)

# ----------------------------------------------------------------------
# 4D camera (TestScene WorldCam conventions: Forward/Left/Up columns,
# rd4 = normalize(Focal*F - ndc.x*L + ndc.y*U))
# ----------------------------------------------------------------------
FOCAL = 1.732   # ~60 deg vertical FOV, matches the engine's precompute

def make_cam(pos3, fwd3, up3=(0.0, 1.0, 0.0)):
    f = (fwd3[0], fwd3[1], fwd3[2], 0.0); f = vmul(f, 1.0/vnorm(f))
    l = (fwd3[1]*0.0 - 0.0, 0.0, 0.0, 0.0)  # placeholder, built below
    # left = normalize(cross(f, up))... use standard: left = norm(cross(up, f))
    ux, uy, uz = up3
    lx = uy*f[2] - uz*f[1]; ly = uz*f[0] - ux*f[2]; lz = ux*f[1] - uy*f[0]
    l = (lx, ly, lz, 0.0); l = vmul(l, 1.0/vnorm(l))
    u = (f[1]*l[2] - f[2]*l[1], f[2]*l[0] - f[0]*l[2], f[0]*l[1] - f[1]*l[0], 0.0)
    u = vmul(u, 1.0/vnorm(u))
    return (V(*pos3, RMin), f, l, u)     # camera on the upper sheet (w = +RMin)

def cam_ray(cam, ndc_x, ndc_y):
    ro4, f, l, u = cam
    d = (FOCAL*f[0] - ndc_x*l[0] + ndc_y*u[0],
         FOCAL*f[1] - ndc_x*l[1] + ndc_y*u[1],
         FOCAL*f[2] - ndc_x*l[2] + ndc_y*u[2], 0.0)
    return trace4d(ro4, vmul(d, 1.0/vnorm(d)))

def render_view(cam, size=96):
    """Rasterize the camera view. Returns (cube_pixels, bbox, center, mask)."""
    mask = set()
    for j in range(size):
        for i in range(size):
            ndc_x = (2.0*(i + 0.5) - size) / size
            ndc_y = (2.0*(j + 0.5) - size) / size
            r = cam_ray(cam, ndc_x, ndc_y)
            if r['hit'] is not None:
                mask.add((i, j))
    n_cube = len(mask)
    if mask:
        xs = [i for i, _ in mask]; ys = [j for _, j in mask]
        bbox = (max(xs) - min(xs) + 1, max(ys) - min(ys) + 1)
        cx = (min(xs) + max(xs)) / 2.0; cy = (min(ys) + max(ys)) / 2.0
    else:
        bbox, cx, cy = (0, 0), float('nan'), float('nan')
    return dict(cube=n_cube, bbox=bbox, cx=cx, cy=cy, total=size*size,
                mask=mask)

def ascii_view(mask, size, w=56, h=28):
    """Downsample a hit mask into an ASCII panel (cube pixels = #)."""
    if not mask:
        return "    (cube not visible from here)"
    lines = []
    for r in range(h):
        row = ""
        for c in range(w):
            x0 = int(c * size / w); x1 = max(x0 + 1, int((c + 1) * size / w))
            y0 = int(r * size / h); y1 = max(y0 + 1, int((r + 1) * size / h))
            row += "#" if any((x, y) in mask
                              for x in range(x0, x1) for y in range(y0, y1)) else "."
        lines.append("    " + row)
    return "\n".join(lines)

# ----------------------------------------------------------------------
# GI: irradiance at a fixed receiver point = fraction of stratified
# hemisphere bounce rays that hit the cube. The camera is NOT an input.
# ----------------------------------------------------------------------
def hemisphere_directions(n_az, n_pol):
    """Stratified (deterministic) sample of the upper hemisphere around +y."""
    dirs = []
    for j in range(n_pol):
        # stratified in cos(theta): theta in [0, pi/2]
        ct = (j + 0.5) / n_pol
        st = math.sqrt(1.0 - ct*ct)
        for i in range(n_az):
            ph = 2.0 * math.pi * (i + 0.5) / n_az
            dirs.append((st*math.cos(ph), ct, st*math.sin(ph)))
    return dirs

def gi_irradiance(receiver, dirs, stats=None):
    """receiver: (x, y, z) local, upper sheet. Returns (cube_hits, total)."""
    ro4 = V(receiver[0], receiver[1], receiver[2], RMin)
    hits = 0
    for d in dirs:
        r = trace4d(ro4, (d[0], d[1], d[2], 0.0))   # unit hemisphere direction, w = 0
        if r['hit'] is not None:
            hits += 1
        if stats is not None:
            stats[r['kind']] = stats.get(r['kind'], 0) + 1
    return hits, len(dirs)

def fmt_bbox(bb):
    return f"{bb[0]}x{bb[1]}"

def main():
    print("=" * 72)
    print("GI vs wormhole: is GI through the mouth screen-space (camera-")
    print("dependent) or world-space?")
    print("=" * 72)
    print(f"\nScene: wormhole rMaj={RMaj} rMin={RMin}, sheets w=+/-{RMin};")
    print(f"orange cube (half-size {CUBE_HS}) centered at local {CUBE_CENTER}")
    print(f"       on the LOWER sheet (universe=-1) only.\n")

    # --- Three camera configurations where the cube's apparent image is
    # very different (close & low = smeared; high & far = normal).
    camA = make_cam((0.0, 1.2, -4.6), (0.0, -1.2, 4.6))    # close & low
    camB = make_cam((0.0, 5.5, -14.5), (0.0, -5.5, 14.5))  # high & far
    camC = make_cam((4.0, 1.2, 2.0), (0.0, -1.2, -6.0))    # oblique side view

    views = {}
    print("Camera views of the cube through the mouth (96x96):")
    for name, cam in (("A: close & low ", camA),
                      ("B: high & far  ", camB),
                      ("C: oblique side", camC)):
        v = render_view(cam)
        views[name[0]] = v
        print(f"  {name}: cube pixels = {v['cube']:5d}/{v['total']} "
              f"({100.0*v['cube']/v['total']:.2f}%), "
              f"bbox = {fmt_bbox(v['bbox'])}, "
              f"image center = ({v['cx']:.1f}, {v['cy']:.1f})")

    # --- Receiver points on the upper sheet (outside the mouth ball).
    # Mouth-level receivers (the mouth center sits at local y=0; a receiver
    # up high sees only the sliver of the mouth above its horizon).
    receivers = {
        "P_front (1.95u past rim)": (0.0, 0.0, -3.95),
        "P_side  (1.95u past rim)": (3.95, 0.0, 0.0),
        "P_far   (7u past rim)   ": (0.0, 0.0, -9.0),
    }
    dirs = hemisphere_directions(160, 80)  # 12800 rays per receiver

    print("\n" + "-" * 72)
    print("EXPERIMENT A: GI irradiance at FIXED receiver points while the")
    print("camera moves between A/B/C (the decisive screen-space test)")
    print("-" * 72)
    print(f"  (irradiance = fraction of {len(dirs)} stratified hemisphere "
          f"bounce rays that hit the cube)")
    results = {}
    for cam_name, cam in (("A", camA), ("B", camB), ("C", camC)):
        row = []
        for pname, p in receivers.items():
            hits, total = gi_irradiance(p, dirs)
            row.append(hits / total)
            results.setdefault(pname, {})[cam_name] = (hits, total)
        print(f"  camera {cam}: " + "  |  ".join(
            f"{pname.split()[0]} {r*100:6.3f}%" for pname, r in zip(receivers, row)))

    print("\n  Verdict check (identical across camera configs?):")
    for pname in receivers:
        vals = results[pname]
        fracs = tuple(h / t for h, t in vals.values())
        identical = len(set(vals.values())) == 1
        print(f"    {pname}: " + "  ".join(
            f"cam{k}={h}/{t}" for k, (h, t) in sorted(vals.items()))
            + f"   -> {'IDENTICAL (no camera dependence)' if identical else 'DIFFERS (camera-dependent!)'}")

    print("\n" + "-" * 72)
    print("EXPERIMENT B: does the DISTORTED image correlate with MORE light")
    print("(real lensing physics) or is it an artifact?")
    print("-" * 72)
    print("  For each receiver: what a virtual camera sitting THERE sees, vs")
    print("  the GI it collects:")
    for pname, p in receivers.items():
        vcam = make_cam(p, (0.0 - p[0], 0.0 - p[1], 0.0 - p[2]))  # look at mouth
        v = render_view(vcam, size=64)
        hits, total = gi_irradiance(p, dirs)
        area_pct = 100.0 * v['cube'] / v['total']
        print(f"    {pname}:")
        print(f"      apparent cube: {v['cube']:4d}/{v['total']} px "
              f"({area_pct:5.2f}% of view), bbox = {fmt_bbox(v['bbox'])}")
        print(f"      GI from cube : {hits:5d}/{total} "
              f"({100.0*hits/total:5.3f}% of hemisphere)")
        if 'P_front' in pname:
            print(ascii_view(v['mask'], 64))

    print("\n  The two main camera views (cube image shape):")
    print("  Camera A (close & low - the 'distorted' view):")
    print(ascii_view(views['A']['mask'], 96))
    print("  Camera B (high & far - the 'normal' view):")
    print(ascii_view(views['B']['mask'], 96))

    # --- Ray-path breakdown for one receiver (sanity: where do the rays go)
    print("\n" + "-" * 72)
    print("Ray-path breakdown for P_front (12800 hemisphere rays):")
    stats = {}
    gi_irradiance(receivers["P_front (1.95u past rim)"], dirs, stats)
    for k, n in sorted(stats.items(), key=lambda kv: -kv[1]):
        print(f"    {k:9s}: {n:5d} ({100.0*n/len(dirs):.2f}%)")

    # --- Proportionality: irradiance vs apparent solid angle
    print("\n" + "-" * 72)
    print("Consistency: GI fraction / apparent-area fraction (should be a")
    print("constant ~ view-cone/hemisphere solid-angle ratio, i.e. irradiance")
    print("proportional to the cube's apparent solid angle from that point):")
    for pname, p in receivers.items():
        vcam = make_cam(p, (0.0 - p[0], 0.0 - p[1], 0.0 - p[2]))
        v = render_view(vcam, size=64)
        hits, total = gi_irradiance(p, dirs)
        ratio = (hits / total) / (v['cube'] / v['total']) if v['cube'] else float('nan')
        print(f"    {pname}: ratio = {ratio:.3f}")

if __name__ == "__main__":
    main()
