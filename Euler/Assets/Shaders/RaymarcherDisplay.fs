// Display stage: full-resolution EDGE-AWARE UPSCALER for the low-res
// raymarch.
//
// The compute stage (Raymarcher.comp) raymarches at an INTERNAL resolution
// (RenderScale x the window, e.g. 0.5x) and writes 3 uints per low-res
// pixel into the shared output buffer (SSBO, binding 0):
//
//   [0] packed RGBA8 color
//   [1] float bitcast of the hit's total path length (DIST_SKY = 1e30 on a
//       miss) - the primary edge signal
//   [2] 3x8-bit shaded world normal ([-1,1] -> [0,255]) | 8-bit family id
//       in the top byte (0 = sky, 1 = plane, 2 = terrain, 3+ = future)
//
// This stage draws one full-screen quad at NATIVE window resolution. For
// every output pixel it gathers the 2x2 neighborhood of low-res texels whose
// corners the pixel falls in (the exact same four taps as a bilinear read)
// and reconstructs its color with per-tap weights:
//
//   weight = spatial * depth * normal * id
//
//   spatial: the bilinear weights (sum 1)
//   depth:   exp(-relDelta * DepthScale) with the RELATIVE delta
//            relDelta = |d - dRef| / max(1, min(d, dRef))
//   normal:  pow(max(dot(n, nRef), 0), NormalExponent)  (skipped for sky)
//   id:      sky<->geometry taps get SkyReject (0 = hard separation);
//            other family mismatches get CrossIdPenalty
//
// where the REFERENCE (dRef, nRef, iRef) is the spatially dominant corner -
// the nearest low-res texel to the pixel center. The result behaves like
// bilinear on smooth regions and like nearest at discontinuities
// (silhouettes, the terrain horizon, sky edges, the wormhole throat - the
// 4D path length in [1] separates opposite sides of the mouth for free).
// If all four weights collapse, the pixel falls back to plain bilinear
// (never black pixels).
//
// No textures, no FBO: the low-res output lives in the SSBO and is indexed
// directly (a 2x2 neighborhood is four integer index expressions - simpler
// than UV mapping, and it reuses the binding the pipeline already has).
//
// DebugMode (set from C#) swaps the output for analysis views:
//   0 final (edge-aware + optional sharpen)   1 nearest (raw low-res)
//   2 plain bilinear                          3 edge-aware, no sharpen
//   4 distance (fog-matched ramp)             5 normal (sky = mid gray)
//   6 family id                               7 edge-rejection mask
//
// The vertex stage is raylib's built-in default full-screen pass-through
// (gl_Position = mvp * vertexPosition), so this file is loaded as
// ShaderType.Pixel with no explicit .vs.
#version 430

uniform vec2  Resolution;      // full framebuffer size in pixels (output)
uniform vec2  LowRes;          // internal raymarch resolution in texels
uniform int   DebugMode;       // see the header
uniform float DepthScale;      // depth falloff: exp(-relDelta * DepthScale)
uniform float NormalExponent;  // pow exponent of max(dot(n, nRef), 0)
uniform float CrossIdPenalty;  // weight for a non-sky family mismatch
uniform float SkyReject;       // weight for sky<->geometry mixing (0 = hard)
uniform float Sharpen;         // unsharp amount vs plain bilinear (0 = off)

out vec4 fragColor;

// The same output buffer Raymarcher.comp writes. Binding point 0 is
// context-global GL state, so one Rlgl.BindShaderBuffer(...) call makes it
// visible to BOTH the compute and this display program.
layout(std430, binding = 0) buffer Pixels
{
    uint data[];
} pixelBuf;

// ---------------------------------------------------------------------------
// Low-res texel access (direct SSBO indexing)
// ---------------------------------------------------------------------------

// Base index of texel (x, y) in the 3-uint stride. The border clamping makes
// the 2x2 gather total-safe for degenerate cases (a 1-wide/1-high low-res
// buffer, pixels on the buffer edge).
int TapIndex(int x, int y)
{
    x = clamp(x, 0, int(LowRes.x) - 1);
    y = clamp(y, 0, int(LowRes.y) - 1);
    return (y * int(LowRes.x) + x) * 3;
}

vec3 TapColor(int x, int y)
{
    uint px = pixelBuf.data[TapIndex(x, y)];
    return vec3(
        float(px & 0xFFu),
        float((px >> 8) & 0xFFu),
        float((px >> 16) & 0xFFu)) / 255.0;
}

float TapDist(int x, int y)
{
    return uintBitsToFloat(pixelBuf.data[TapIndex(x, y) + 1]);
}

// Decoded + renormalized shaded normal. (0,0,0) is reserved for sky/miss:
// the encoder maps a zero normal to mid-gray 127/128, which decodes back to
// ~zero and is dropped here.
vec3 TapNormal(int x, int y)
{
    uint p = pixelBuf.data[TapIndex(x, y) + 2];
    vec3 n = (vec3(
        float(p & 0xFFu),
        float((p >> 8) & 0xFFu),
        float((p >> 16) & 0xFFu)) / 255.0) * 2.0 - 1.0;
    float l = length(n);
    return (l > 1e-3) ? n / l : vec3(0.0);
}

int TapId(int x, int y)
{
    return int(pixelBuf.data[TapIndex(x, y) + 2] >> 24);
}

// ---------------------------------------------------------------------------
// Edge-aware weighting
// ---------------------------------------------------------------------------

// Guidance weight of one tap against the reference corner: its bilinear
// (spatial) weight modulated by depth similarity, normal similarity and
// family gating.
float TapWeight(float spatial, float d, vec3 n, int id,
                float dRef, vec3 nRef, int idRef)
{
    float w = spatial;

    // Family gating. Sky/geometry is the most visible failure mode of naive
    // upscaling (haloing at the horizon), so it gets its own, default-hard
    // weight; mismatches between two real surfaces get the softer penalty
    // (keeps the filter from going fully nearest-neighbor across e.g. a
    // plane/terrain seam that shares depth and normal).
    bool skyRef = (idRef == 0);
    bool skyTap = (id == 0);
    if (skyRef != skyTap)
        w *= SkyReject;
    else if (id != idRef)
        w *= CrossIdPenalty;

    // Depth similarity on a RELATIVE delta: close silhouettes need sharp
    // absolute sensitivity, while far surfaces (the fogged horizon) have
    // large absolute step-to-step depth variation that must not reject
    // everything. Dividing by the smaller of the two distances (min 1
    // world unit) keeps the filter scale-aware. (A wormhole-mediated hit's
    // d includes its 4D throat path, so taps on opposite sides of the
    // mouth also separate here.)
    float dd = abs(d - dRef) / max(1.0, min(d, dRef));
    w *= exp(-dd * DepthScale);

    // Normal similarity: preserves creases and stops smearing across
    // sloped terrain that depth alone would blend. Skipped for a sky
    // reference (its "normal" is the (0,0,0) marker - the dot would be 0
    // and collapse every sky-sky weight to the fallback).
    if (dot(nRef, nRef) > 1e-6)
        w *= pow(max(dot(n, nRef), 0.0), NormalExponent);

    return w;
}

void main()
{
    int W = int(LowRes.x);
    int H = int(LowRes.y);

    // Map the output pixel (gl_FragCoord, texel center at +0.5) into low-res
    // texel space: the 2x2 neighborhood is the four texels the pixel's
    // center falls between - the exact taps of a bilinear read.
    vec2 lowPos = (gl_FragCoord.xy / Resolution) * LowRes - 0.5;   // gl_FragCoord is vec4
    vec2 maxBase = max(vec2(float(W - 2), float(H - 2)), vec2(0.0));
    vec2 base = clamp(floor(lowPos), vec2(0.0), maxBase);
    vec2 f = lowPos - base;

    int x0 = int(base.x);
    int y0 = int(base.y);
    int x1 = x0 + 1;
    int y1 = y0 + 1;

    // Bilinear (spatial) weights; they sum to 1 and double as the fallback.
    float w00 = (1.0 - f.x) * (1.0 - f.y);
    float w10 = f.x * (1.0 - f.y);
    float w01 = (1.0 - f.x) * f.y;
    float w11 = f.x * f.y;

    // Gather the 2x2 neighborhood: color + every guidance signal.
    vec3 c00 = TapColor(x0, y0);
    vec3 c10 = TapColor(x1, y0);
    vec3 c01 = TapColor(x0, y1);
    vec3 c11 = TapColor(x1, y1);

    float d00 = TapDist(x0, y0);
    float d10 = TapDist(x1, y0);
    float d01 = TapDist(x0, y1);
    float d11 = TapDist(x1, y1);

    vec3 n00 = TapNormal(x0, y0);
    vec3 n10 = TapNormal(x1, y0);
    vec3 n01 = TapNormal(x0, y1);
    vec3 n11 = TapNormal(x1, y1);

    int i00 = TapId(x0, y0);
    int i10 = TapId(x1, y0);
    int i01 = TapId(x0, y1);
    int i11 = TapId(x1, y1);

    // Reference = the spatially dominant corner (nearest low-res texel to
    // the pixel center). All guidance is measured against it.
    int rx = (f.x < 0.5) ? x0 : x1;
    int ry = (f.y < 0.5) ? y0 : y1;
    float dRef = TapDist(rx, ry);
    vec3  nRef = TapNormal(rx, ry);
    int   iRef = TapId(rx, ry);

    // Weighted (edge-aware) reconstruction; if every weight collapses the
    // pixel falls back to plain bilinear (a pixel surrounded by rejected
    // taps is exactly where unweighted blending is the stable answer).
    float e00 = TapWeight(w00, d00, n00, i00, dRef, nRef, iRef);
    float e10 = TapWeight(w10, d10, n10, i10, dRef, nRef, iRef);
    float e01 = TapWeight(w01, d01, n01, i01, dRef, nRef, iRef);
    float e11 = TapWeight(w11, d11, n11, i11, dRef, nRef, iRef);

    vec3 bilinear = w00 * c00 + w10 * c10 + w01 * c01 + w11 * c11;
    float eSum = e00 + e10 + e01 + e11;
    vec3 edge = (eSum > 1e-6)
        ? (e00 * c00 + e10 * c10 + e01 * c01 + e11 * c11) / eSum
        : bilinear;

    // ---------------------------------------------------------------
    // Debug / analysis views (C# DebugMode; 0 falls through below)
    // ---------------------------------------------------------------
    if (DebugMode == 1)                    // nearest (raw low-res magnified)
    {
        fragColor = vec4((rx == x0 ? (ry == y0 ? c00 : c01)
                                   : (ry == y0 ? c10 : c11)), 1.0);
        return;
    }
    if (DebugMode == 2)                    // plain bilinear (reference quality)
    {
        fragColor = vec4(bilinear, 1.0);
        return;
    }
    if (DebugMode == 3)                    // edge-aware, no sharpen (A/B vs 0)
    {
        fragColor = vec4(edge, 1.0);
        return;
    }
    if (DebugMode == 4)                    // hit distance (fog-matched ramp; sky white)
    {
        fragColor = vec4((dRef > 1e29) ? vec3(1.0) : vec3(1.0 - exp(-0.01 * dRef)), 1.0);
        return;
    }
    if (DebugMode == 5)                    // shaded normal (sky = mid gray)
    {
        fragColor = vec4(nRef * 0.5 + 0.5, 1.0);
        return;
    }
    if (DebugMode == 6)                    // family id (sky black, plane red, terrain green)
    {
        vec3 idCol = (iRef == 0)  ? vec3(0.0, 0.0, 0.0)
                     : (iRef == 1) ? vec3(1.0, 0.0, 0.0)
                     : (iRef == 2) ? vec3(0.0, 1.0, 0.0)
                     :               vec3(0.0, 1.0, 1.0);
        fragColor = vec4(idCol, 1.0);
        return;
    }
    if (DebugMode == 7)                    // edge-rejection mask (white where guidance rejected a spatially significant tap)
    {
        float m = max(max((e00 < 1e-4 && w00 > 1e-4) ? 1.0 : 0.0,
                          (e10 < 1e-4 && w10 > 1e-4) ? 1.0 : 0.0),
                      max((e01 < 1e-4 && w01 > 1e-4) ? 1.0 : 0.0,
                          (e11 < 1e-4 && w11 > 1e-4) ? 1.0 : 0.0));
        fragColor = vec4(vec3(m), 1.0);
        return;
    }

    // DebugMode 0: the real output. Edge-aware reconstruction plus optional
    // mild sharpening: an unsharp mask taken against the PLAIN BILINEAR of
    // the same 2x2, so flat areas (where edge ~ bilinear) are untouched and
    // only the guidance-rejected blends are pulled back - never a hard
    // sharpen across a discontinuity.
    vec3 outCol = edge;
    if (Sharpen > 0.0)
        outCol += (edge - bilinear) * Sharpen;

    fragColor = vec4(clamp(outCol, 0.0, 1.0), 1.0);
}
