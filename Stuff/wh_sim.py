#!/usr/bin/env python3
"""
Faithful replica of Euler's raymarcher wormhole pipeline (Raymarcher.comp +
Wormhole.inc + Terrain.inc) to diagnose the rim artifacts. All math mirrors
the GLSL; float64 (float32 effects are << the bands under study).
"""
import math

# ----------------------------------------------------------------------
# Terrain (Terrain.inc exact replica)
# ----------------------------------------------------------------------
TERRAIN_OFFSET = (0.0, -4.0, 0.0)
TERRAIN_AMP = 12.5
TERRAIN_FREQ = 0.04
TERRAIN_OCTAVES = 5.0
TERRAIN_LOD_NEAR, TERRAIN_LOD_FAR, TERRAIN_LOD_MIN = 50.0, 200.0, 2.0
ROT0, ROT1 = 0.80, 0.60          # mat2(0.8, 0.6, -0.6, 0.8)

def hash21(px, py):
    fx, fy = 123.34, 456.21
    x = (px * fx) - math.floor(px * fx)
    y = (py * fy) - math.floor(py * fy)
    x += x * x + y * y + 45.32 * 2  # p + dot(p, p + 45.32): adds 45.32 to each comp
    # NOTE: GLSL p += dot(p, p + 45.32) adds the SAME scalar to both comps
    return None

def hash21_glsl(px, py):
    # vec2 p = fract(p * vec2(123.34, 456.21)); p += dot(p, p + 45.32); return fract(p.x * p.y);
    ax, ay = px * 123.34, py * 456.21
    x = ax - math.floor(ax)
    y = ay - math.floor(ay)
    d = x * (x + 45.32) + y * (y + 45.32)
    x += d
    y += d
    v = x * y
    return v - math.floor(v)

def smooth5(f):
    return f * f * f * (f * (f * 6.0 - 15.0) + 10.0)

def value_noise(px, py):
    ix, iy = math.floor(px), math.floor(py)
    fx, fy = px - ix, py - iy
    ux, uy = smooth5(fx), smooth5(fy)
    n00 = hash21_glsl(ix, iy)
    n10 = hash21_glsl(ix + 1, iy)
    n01 = hash21_glsl(ix, iy + 1)
    n11 = hash21_glsl(ix + 1, iy + 1)
    a = n00 + (n10 - n00) * ux
    b = n01 + (n11 - n01) * ux
    return a + (b - a) * uy

def fbm(px, py, octaves):
    v, amp, norm = 0.0, 0.5, 0.0
    n = int(octaves)
    for i in range(6):
        if i >= n:
            break
        v += amp * value_noise(px, py)
        norm += amp
        # p = TerrainRot * p * 2.02
        px, py = (ROT0 * px - ROT1 * py) * 2.02, (ROT1 * px + ROT0 * py) * 2.02
        amp *= 0.5
    return v / norm

def terrain_height(world_x, world_z, dist):
    lx, lz = world_x - TERRAIN_OFFSET[0], world_z - TERRAIN_OFFSET[2]
    full = min(max(TERRAIN_OCTAVES, 1.0), 6.0)
    minoct = min(full, TERRAIN_LOD_MIN)
    f = min(max((dist - TERRAIN_LOD_NEAR) / (TERRAIN_LOD_FAR - TERRAIN_LOD_NEAR), 0.0), 1.0)
    octs = full + (minoct - full) * f
    h = fbm(lx * TERRAIN_FREQ, lz * TERRAIN_FREQ, octs)
    h *= h
    return TERRAIN_OFFSET[1] + TERRAIN_AMP * h

def intersect_terrain(ro, rd):
    """Exact replica of IntersectTerrain. Returns (t, xz) or None."""
    # cull
    if rd[1] >= 0.0 and ro[1] >= TERRAIN_OFFSET[1] + TERRAIN_AMP:
        return None
    if terrain_height(ro[0], ro[2], 0.0) >= ro[1]:
        return None
    MaxDist, MaxSteps, Safety = 1000.0, 128, 0.75
    t, tPrev = 0.0, 0.0
    for i in range(MaxSteps):
        p = (ro[0] + rd[0] * t, ro[1] + rd[1] * t, ro[2] + rd[2] * t)
        d = p[1] - terrain_height(p[0], p[2], t)
        if d < 0.0:
            lo, hi = tPrev, t
            for j in range(8):
                mid = 0.5 * (lo + hi)
                pm = (ro[0] + rd[0] * mid, ro[1] + rd[1] * mid, ro[2] + rd[2] * mid)
                if pm[1] - terrain_height(pm[0], pm[2], mid) > 0.0:
                    lo = mid
                else:
                    hi = mid
            return (hi, (ro[0] + rd[0] * hi, ro[2] + rd[2] * hi))
        eps = 0.02 + 0.005 * t
        if d <= eps:
            return (t if t > 0.0 else 1e-3, (p[0], p[2]))
        tPrev = t
        t += d * Safety
        if t >= MaxDist:
            return None
    return None

# ----------------------------------------------------------------------
# Wormhole space (Wormhole.inc exact replica)
# ----------------------------------------------------------------------
RMaj, RMin = 2.0, 1.0
WH_MAX_STEPS = 96

def space_normal(p):
    x, y, z, w = p
    r2 = x * x + y * y + z * z
    if r2 >= RMaj * RMaj:
        return (0.0, 0.0, 0.0, 1.0 if w >= 0.0 else -1.0)
    rl = math.sqrt(r2)
    if rl > 1e-5:
        dx, dy, dz = x / rl, y / rl, z / rl
    else:
        dx, dy, dz = 0.0, 1.0, 0.0
    vx, vy, vz, vw = x - dx * RMaj, y - dy * RMaj, z - dz * RMaj, w
    l = math.sqrt(vx * vx + vy * vy + vz * vz + vw * vw)
    return (vx / l, vy / l, vz / l, vw / l)

def space_sdf(p):
    x, y, z, w = p
    r2 = x * x + y * y + z * z
    if r2 >= RMaj * RMaj:
        return abs(w) - RMin
    rl = math.sqrt(r2)
    if rl > 1e-5:
        dx, dy, dz = x / rl, y / rl, z / rl
    else:
        dx, dy, dz = 0.0, 1.0, 0.0
    vx, vy, vz, vw = x - dx * RMaj, y - dy * RMaj, z - dz * RMaj, w
    return math.sqrt(vx * vx + vy * vy + vz * vz + vw * vw) - RMin

def space_normalize(p, d):
    n = space_normal(p)
    sd = space_sdf(p)
    p = (p[0] - sd * n[0], p[1] - sd * n[1], p[2] - sd * n[2], p[3] - sd * n[3])
    dot = d[0] * n[0] + d[1] * n[1] + d[2] * n[2] + d[3] * n[3]
    q = (d[0] - n[0] * dot, d[1] - n[1] * dot, d[2] - n[2] * dot, d[3] - n[3] * dot)
    l = math.sqrt(q[0] ** 2 + q[1] ** 2 + q[2] ** 2 + q[3] ** 2)
    if l > 1e-6:
        d = (q[0] / l, q[1] / l, q[2] / l, q[3] / l)
    return p, d

def space_step_limit(p):
    return max(0.1 * RMin, math.sqrt(p[0] ** 2 + p[1] ** 2 + p[2] ** 2) - RMaj)

def wh_on_sheet(p):
    return abs(p[3]) >= RMin - 1e-4

# ----------------------------------------------------------------------
# WhMarch (driver exact replica)
# ----------------------------------------------------------------------
def intersect_scene(ro3, rd3, universe):
    """Only terrain in the sim (planes are absent). Returns (t, xz, family, nrm) or None."""
    hit = intersect_terrain(ro3, rd3)
    if hit is None:
        return None
    t, xz = hit
    # normal (finite difference, e = 0.5)
    e = 0.5
    hL = terrain_height(xz[0] - e, xz[1], t)
    hR = terrain_height(xz[0] + e, xz[1], t)
    hD = terrain_height(xz[0], xz[1] - e, t)
    hU = terrain_height(xz[0], xz[1] + e, t)
    n = (hL - hR, 2.0 * e, hD - hU)
    l = math.sqrt(sum(c * c for c in n))
    n = (n[0] / l, n[1] / l, n[2] / l)
    return (t, xz, 1, n)

def wh_march(ro4, rd4):
    """Returns dict: handoff(bool), budget(bool), universe, pos3, dir3, d3len,
    pathLen, exit info (steps, |xyz|, w at handoff), hit."""
    p4, d4 = ro4, rd4
    pathLen = 0.0
    rMaj2 = RMaj * RMaj
    inBall = (p4[0] ** 2 + p4[1] ** 2 + p4[2] ** 2) < rMaj2
    info = dict(handoff=False, budget=False, steps=0, exitPos=None, exitDir=None,
                dir3Len=None, pathLen=0.0, inBallStart=inBall)
    for i in range(WH_MAX_STEPS):
        d = space_step_limit(p4)
        p4 = (p4[0] + d4[0] * d, p4[1] + d4[1] * d, p4[2] + d4[2] * d, p4[3] + d4[3] * d)
        pathLen += d
        p4, d4 = space_normalize(p4, d4)
        info["steps"] = i + 1
        nowInBall = (p4[0] ** 2 + p4[1] ** 2 + p4[2] ** 2) < rMaj2
        if inBall and not nowInBall:
            universe = 1 if p4[3] >= 0.0 else -1
            pos3 = p4[:3]
            dir3 = d4[:3]
            l3 = math.sqrt(sum(c * c for c in dir3))
            info.update(handoff=True, universe=universe, pos3=pos3, dir3=dir3,
                        dir3Len=l3, pathLen=pathLen,
                        exitPos=(math.sqrt(sum(c * c for c in p4[:3])), p4[3]))
            hit = intersect_scene(pos3, dir3, universe)
            info["hit"] = hit
            return info
        inBall = nowInBall
    # budget exhausted
    universe = 1 if p4[3] >= 0.0 else -1
    pos3 = p4[:3]
    l3 = math.sqrt(sum(c * c for c in d4[:3]))
    info.update(budget=True, universe=universe, pos3=pos3, pathLen=pathLen,
                exitPos=(math.sqrt(sum(c * c for c in p4[:3])), p4[3]))
    if l3 > 1e-4:
        dir3 = tuple(c / l3 for c in d4[:3])
        info["dir3"] = dir3
        info["dir3Len"] = 1.0
        hit = intersect_scene(pos3, dir3, universe)
        info["hit"] = hit
    else:
        info["dir3"] = (0.0, 1.0, 0.0)
        info["hit"] = None
    return info

# ----------------------------------------------------------------------
# Driver per-pixel flow (main() wormhole branch, sheet-camera path)
# ----------------------------------------------------------------------
def drive_pixel(ro4, rd4, label=""):
    """Full driver flow. Returns (branch, detail)."""
    if wh_on_sheet(ro4):
        ro3 = ro4[:3]
        f3 = rd4[:3]
        l3 = math.sqrt(sum(c * c for c in f3))
        if l3 > 1e-4:
            rd3 = tuple(c / l3 for c in f3)
            b = ro4[0] * rd3[0] + ro4[1] * rd3[1] + ro4[2] * rd3[2]
            c2 = sum(c * c for c in ro4[:3]) - RMaj * RMaj
            disc = b * b - c2
            tEnter = (-b - math.sqrt(disc)) if (b < 0.0 and disc > 0.0) else -1.0
            if tEnter < 0.0:
                hit = intersect_scene(ro3, rd3, 1)
                return ("straight", dict(tEnter=tEnter, hit=hit, rd3=rd3))
            else:
                pre = intersect_scene(ro3, rd3, 1)
                if pre and pre[0] < tEnter:
                    return ("pre", dict(tEnter=tEnter, hit=pre, rd3=rd3))
                else:
                    m = wh_march(ro4, rd4)
                    m["tEnter"] = tEnter
                    return ("march", m)
        else:
            return ("march-no3d", wh_march(ro4, rd4))
    else:
        m = wh_march(ro4, rd4)
        return ("throat-cam", m)

# ----------------------------------------------------------------------
# Camera (TestScene)
# ----------------------------------------------------------------------
# WorldCam local: pos (0,5,-16), forward (0,0,1,0), left (-1,0,0,0), up (0,1,0,0)
CAM_POS4 = (0.0, 5.0, -16.0, RMin)
FOCAL = 1.0

def pixel_ray(ndc_x, ndc_y):
    # rd4 = normalize(WormholeCam * vec4(Focal, -ndc.x, ndc.y, 0))
    # = normalize(Focal*F - ndc.x*L + ndc.y*U)
    x = FOCAL * 0.0 + (-ndc_x) * (-1.0) + ndc_y * 0.0
    y = FOCAL * 0.0 + (-ndc_x) * 0.0 + ndc_y * 1.0
    z = FOCAL * 1.0 + (-ndc_x) * 0.0 + ndc_y * 0.0
    w = 0.0
    l = math.sqrt(x * x + y * y + z * z + w * w)
    return (x / l, y / l, z / l, w / l)

def impact_param(rd3):
    # perpendicular distance of line (CAM_POS4.xyz, rd3) to the wormhole center
    ro = CAM_POS4[:3]
    b = sum(ro[i] * rd3[i] for i in range(3))
    c2 = sum(c * c for c in ro)
    d = b * b - c2
    return math.sqrt(max(0.0, -d))

def main():
    # find ndc.y crossing the near rim at ndc.x = 0
    def branch_for(ny):
        rd = pixel_ray(0.0, ny)
        br, det = drive_pixel(CAM_POS4, rd)
        return br, det

    print("=== Sweep ndc.y across NEAR rim (ndc.x=0) ===")
    for ny1000 in range(-220, -150):
        ny = ny1000 / 1000.0
        br, det = branch_for(ny)
        if br == "straight":
            hit = det["hit"]
            s = f"ndc.y={ny:+.3f} straight  tEnter={det['tEnter']:.4f}"
            if hit:
                s += f"  hit t={hit[0]:8.3f} at ({hit[1][0]:7.3f},{hit[1][1]:7.3f})"
            print(s)
        else:
            e = det
            tag = "march" + ("[budget!]" if e.get("budget") else "")
            s = f"ndc.y={ny:+.3f} {tag} tEnter={e.get('tEnter', float('nan')):7.4f}"
            if e.get("handoff"):
                ex = e["exitPos"]
                s += f"  steps={e['steps']:3d} handoff @ (|xyz|={ex[0]:7.4f}, w={ex[1]:8.4f}) dir3Len={e['dir3Len']:.4f}"
                h = e["hit"]
                if h:
                    s += f"  hit t={h[0]:8.3f} at ({h[1][0]:7.3f},{h[1][1]:7.3f})"
                else:
                    s += "  hit=SKY"
            else:
                s += f"  steps={e['steps']:3d} pos=({e['pos3'][0]:7.3f},{e['pos3'][1]:7.3f},{e['pos3'][2]:7.3f}) w={e['exitPos'][1]:8.4f}"
                h = e.get("hit")
                if h:
                    s += f"  hit t={h[0]:8.3f} at ({h[1][0]:7.3f},{h[1][1]:7.3f})"
                else:
                    s += "  hit=SKY"
            print(s)

if __name__ == "__main__":
    main()
