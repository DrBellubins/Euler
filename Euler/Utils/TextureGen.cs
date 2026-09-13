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
}
