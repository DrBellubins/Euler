using Raylib_cs;

namespace Euler.Utils;

/// <summary>
/// Procedural textures, used as fallbacks / test assets so the engine never
/// depends on an external file existing.
/// </summary>
public static class TextureGen
{
    /// <summary>
    /// Checkerboard texture with a thin dark seam along its left/top edge, so
    /// UV tiling and repeat wrapping are easy to verify at a glance.
    /// Uploaded with REPEAT wrap + bilinear filtering.
    /// </summary>
    public static Texture2D Checkerboard()
        => Checkerboard(256, 4, (238, 238, 238), (44, 48, 56), (20, 22, 26));

    /// <param name="sizePx">Texture size (square, power of two).</param>
    /// <param name="cells">Checkerboard cells per side (must divide sizePx).</param>
    /// <param name="light">RGB of the light cells.</param>
    /// <param name="dark">RGB of the dark cells.</param>
    /// <param name="seam">RGB of the 2px seam marking the tile boundary.</param>
    public static Texture2D Checkerboard(
        int sizePx, int cells,
        (byte r, byte g, byte b) light,
        (byte r, byte g, byte b) dark,
        (byte r, byte g, byte b) seam)
    {
        if (sizePx <= 0 || cells <= 0 || sizePx % cells != 0)
            throw new ArgumentException("sizePx must be positive and cells must divide sizePx.");

        int cell = sizePx / cells;
        byte[] data = new byte[sizePx * sizePx * 4];

        for (int y = 0; y < sizePx; y++)
        {
            for (int x = 0; x < sizePx; x++)
            {
                (byte r, byte g, byte b) c;
                if (x < 2 || y < 2) c = seam;
                else c = (((x / cell) ^ (y / cell)) & 1) == 0 ? light : dark;

                int i = (y * sizePx + x) * 4;
                data[i + 0] = c.r;
                data[i + 1] = c.g;
                data[i + 2] = c.b;
                data[i + 3] = 255;
            }
        }

        // NOTE: first arg is the file TYPE (extension), not a file name.
        Image img = Raylib.LoadImageFromMemory(".png", data);
        Texture2D tex = Raylib.LoadTextureFromImage(img);
        Raylib.UnloadImage(img);

        Raylib.SetTextureWrap(tex, TextureWrap.Repeat);
        Raylib.SetTextureFilter(tex, TextureFilter.Bilinear);
        return tex;
    }

    // -------------------------------------------------------------------
    // Terrain noise lattice (baked Hash21)
    // -------------------------------------------------------------------

    /// <summary>
    /// Default lattice edge, in cells. MUST match NOISE_LATTICE_SIZE in
    /// <c>Assets/Shaders/Raymarcher/Shapes/Terrain.inc</c>.
    /// </summary>
    public const int NoiseLatticeSize = 256;

    /// <summary>
    /// The terrain FBM's value-noise lattice, baked once at startup: each
    /// texel holds <see cref="Hash21"/> of its integer lattice coordinate as
    /// 16-bit fixed point across R+G. The compute stage reads it with NEAREST
    /// at integer lattice coordinates (see Hash21 in
    /// <c>Raymarcher/Shapes/Terrain.inc</c>), which replaces ~40 ALU ops per
    /// hash with one cached texel fetch while leaving the noise FIELD
    /// unchanged: the quintic interpolation, the octave stacking and the hill
    /// shaping all stay in the shader.
    ///
    /// The table wraps, so each FBM octave is periodic with period
    /// <paramref name="size"/> lattice cells in its own (rotated, scaled)
    /// frame. At the scene's terrain frequency (0.04) that is ~6400 world
    /// units for the base octave and ~380 for the finest - both far beyond
    /// the fog horizon, so the repetition is never visible. Raise the size
    /// together with NOISE_LATTICE_SIZE if a scene uses a much higher
    /// Frequency.
    ///
    /// Uploaded with REPEAT wrap and POINT (nearest) filtering: the lookup is
    /// at exact texel centers, and interpolation would change the noise.
    /// </summary>
    public static Texture2D NoiseLattice(int size = NoiseLatticeSize)
    {
        if (size <= 0)
            throw new ArgumentException("size must be positive.", nameof(size));

        byte[] data = new byte[size * size * 4];
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                // 16-bit fixed point: 1.5e-5 resolution, i.e. ~0.001 world
                // units of height error at the scene's amplitude - invisible,
                // and the same value is used consistently everywhere (it is a
                // lookup, not noise), so it adds no high-frequency detail.
                int v = (int)MathF.Round(Math.Clamp(Hash21(x, y), 0f, 1f) * 65535f);

                int i = (y * size + x) * 4;
                data[i + 0] = (byte)(v >> 8);        // high byte
                data[i + 1] = (byte)(v & 0xFF);      // low byte
                data[i + 2] = 0;
                data[i + 3] = 255;
            }
        }

        Texture2D tex = RL.LoadTextureRgba8(data, size, size);
        Raylib.SetTextureWrap(tex, TextureWrap.Repeat);
        Raylib.SetTextureFilter(tex, TextureFilter.Point);
        return tex;
    }

    /// <summary>
    /// C# twin of the GLSL <c>Hash21()</c> in
    /// <c>Assets/Shaders/Raymarcher/Shapes/Terrain.inc</c> (the analytic
    /// <c>#else</c> branch of it). Keep the two in sync: this bakes the table
    /// the shader reads, so a divergence changes the terrain's shape.
    /// All arithmetic is float32 to match the shader; the only difference the
    /// two can have is a GLSL FMA contraction, worth ~1 ULP - far below the
    /// 16-bit storage resolution.
    /// </summary>
    private static float Hash21(float px, float py)
    {
        px = Fract(px * 123.34f);
        py = Fract(py * 456.21f);
        float d = px * (px + 45.32f) + py * (py + 45.32f);   // dot(p, p + 45.32)
        px += d;
        py += d;
        return Fract(px * py);
    }

    /// <summary>GLSL <c>fract()</c>: x - floor(x), always in [0, 1).</summary>
    private static float Fract(float x) => x - MathF.Floor(x);
}
