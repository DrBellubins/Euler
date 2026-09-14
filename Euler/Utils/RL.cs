// ---------------------------------------------------------------------------
// RL.cs
//
// Pointer-free wrappers for the Raylib-cs (raylib 6.0) APIs that still expose
// raw pointers in their public signatures.
//
// raylib-cs 8.1.0 already ships clean (string / ref / array / Span) overloads
// for the large majority of the API. Those should be called directly:
//
//     Raylib.LoadImage("texture.png");
//     Raylib.DrawText("hello", 10, 10, 20, Color.White);
//     Raylib.UpdateCamera(ref camera, CameraMode.FirstPerson);
//     var anims = Raylib.LoadModelAnimations("model.glb");   // Span<ModelAnimation>
//     Raylib.SaveFileData(bytes, "data.bin");                // generic byte[] ok
//     Raylib.LoadCodepoints(text, out int count);           // int[]
//
// This file wraps ONLY the remaining gaps (verified against the actual
// assembly with reflection + the raylib 6.0 header for memory ownership):
//
//   File I/O   : LoadFileData, IsFileNameValid, GetDirectoryFiles(Ex),
//                LoadDroppedFileNames, ExportDataAsCode
//   Text       : TextFormat, TextInsert(Alloc), TextReplace(Alloc),
//                TextReplaceBetween(Alloc), TextJoin, TextSplit,
//                LoadTextLines, TextToUpper/ToLower/ToPascal/ToSnake/ToCamel,
//                TextRemoveSpaces, GetTextBetween, TextIsEqual, TextLength,
//                TextFindIndex, TextToInteger, TextToFloat, TextCopy,
//                TextAppend, GetCodepointNext/Previous, GetKeyName
//   Images     : LoadImageColors, LoadImagePalette, ExportImageToMemory,
//                GetPixelColor, SetPixelColor, GenImageFontAtlas
//   Fonts      : LoadFontData (+ handle), UnloadFontData, ExportFontAsCode
//   Audio      : LoadWaveSamples
//   Models     : LoadMaterials
//   Shaders    : LoadShaderFromMemory
//   Window     : GetWindowHandle
//   Raymath    : MatrixDecompose, QuaternionToAxisAngle, Vector3OrthoNormalize
//
// MEMORY MODEL (raylib 6.0 header contracts):
//   - Every native allocation this file receives is copied into managed
//     memory and the native buffer is freed (MemFree / the paired
//     Unload* function) before returning. Callers get ordinary managed
//     values: string, byte[], T[] (T = blittable raylib struct).
//   - Functions documented in raylib 6.0 as returning "static buffer" data
//     (most Text* functions, GetKeyName, ...) are NOT freed - freeing a
//     static buffer would crash. The managed string is a copy; the native
//     buffer is overwritten by the next call to the same function.
//   - LoadFontData: the GlyphInfo array embeds native Image data. It is
//     copied to managed memory, but the underlying pixel data must stay
//     alive until you are done with the glyphs (e.g. GenImageFontAtlas).
//     Keep the returned IntPtr handle and call RL.UnloadFontData(handle, n)
//     afterwards.
//   - LoadMaterials: the Material struct array is copied and freed, but
//     each material's internal maps array is still native. Release each
//     material with Raylib.UnloadMaterial(materials[i]) when done.
//
// REQUIREMENT: the consuming project must enable unsafe code:
//     <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
//
// AOT-compatible: only P/Invoke (via raylib-cs), fixed/stackalloc and
// Marshal.Copy are used. No reflection.
// ---------------------------------------------------------------------------

using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using Raylib_cs;

// File-local aliases so call sites read as Raylib.* / Raymath.* and stay
// immune to namespace collisions in user code.
using Raylib = Raylib_cs.Raylib;
using Raymath = Raylib_cs.Raymath;

namespace Euler.Utils;

/// <summary>
/// Pointer-free facade over the parts of the <c>Raylib_cs.Raylib</c> /
/// <c>Raylib_cs.Raymath</c> API that still require raw pointers.
/// Everything else should be called directly on <c>Raylib</c>.
/// </summary>
public static class RL
{
    // -------------------------------------------------------------------
    // Internal helpers
    // -------------------------------------------------------------------

    /// <summary>Reads a NUL-terminated UTF-8 native string into a managed string.</summary>
    private static unsafe string ReadString(SByte* ptr) => Utf8StringUtils.GetUTF8String(ptr);

    /// <summary>Copies a native blittable struct array into managed memory.</summary>
    private static unsafe T[] CopyArray<T>(T* native, int count) where T : unmanaged
    {
        var result = new T[count];
        if (count > 0)
        {
            var raw = new byte[count * Marshal.SizeOf<T>()];
            Marshal.Copy((IntPtr)native, raw, 0, raw.Length);
            Buffer.BlockCopy(raw, 0, result, 0, raw.Length);
        }
        return result;
    }

    /// <summary>Converts a <see cref="FilePathList"/> (char** internally) to managed strings.</summary>
    private static unsafe string[] PathListToStrings(FilePathList list)
    {
        var paths = new string[list.Count];
        if (list.Paths == null) return paths;
        for (int i = 0; i < list.Count; i++)
            paths[i] = ReadString((SByte*)list.Paths[i]);
        return paths;
    }

    // -------------------------------------------------------------------
    // File system
    // -------------------------------------------------------------------

    /// <summary>
    /// Pointer-free <c>Raylib.LoadFileData</c>. Copies the file into a managed
    /// byte array and unloads the native buffer. Returns an empty array on failure.
    /// </summary>
    public static unsafe byte[] LoadFileData(string fileName)
    {
        int size = 0;
        byte* data = Raylib.LoadFileData(fileName, ref size);
        if (data == null || size <= 0) return Array.Empty<byte>();
        var result = new byte[size];
        Marshal.Copy((IntPtr)data, result, 0, size);
        Raylib.UnloadFileData(data);
        return result;
    }

    /// <summary>Pointer-free <c>Raylib.IsFileNameValid</c>.</summary>
    public static unsafe bool IsFileNameValid(string fileName)
    {
        using var buffer = Utf8StringUtils.ToUtf8Buffer(fileName);
        return Raylib.IsFileNameValid(buffer.AsPointer());
    }

    /// <summary>
    /// Pointer-free <c>Raylib.LoadDirectoryFiles</c>: returns the directory
    /// entries as managed strings (files + directories, no subdirs scan) and
    /// unloads the native <see cref="FilePathList"/>.
    /// </summary>
    public static string[] GetDirectoryFiles(string dirPath)
    {
        FilePathList list = Raylib.LoadDirectoryFiles(dirPath);
        try
        {
            return PathListToStrings(list);
        }
        finally
        {
            Raylib.UnloadDirectoryFiles(list);
        }
    }

    /// <summary>
    /// Pointer-free <c>Raylib.LoadDirectoryFilesEx</c> with extension filter
    /// and optional subdir scan (e.g. "*.obj", or use "DIRS*" to list dirs).
    /// </summary>
    public static string[] GetDirectoryFilesEx(string basePath, string filter, bool scanSubdirs)
    {
        FilePathList list = Raylib.LoadDirectoryFilesEx(basePath, filter, scanSubdirs);
        try
        {
            return PathListToStrings(list);
        }
        finally
        {
            Raylib.UnloadDirectoryFiles(list);
        }
    }

    /// <summary>
    /// Pointer-free <c>Raylib.LoadDroppedFiles</c>: file names dropped onto the
    /// window as managed strings (call after <c>Raylib.IsFileDropped()</c>).
    /// </summary>
    public static string[] LoadDroppedFileNames()
    {
        FilePathList list = Raylib.LoadDroppedFiles();
        try
        {
            return PathListToStrings(list);
        }
        finally
        {
            Raylib.UnloadDroppedFiles(list);
        }
    }

    /// <summary>
    /// Pointer-free <c>Raylib.ExportDataAsCode</c>: exports binary data as a
    /// C header file containing a byte array definition.
    /// </summary>
    public static unsafe bool ExportDataAsCode(byte[] data, string fileName)
    {
        using var name = Utf8StringUtils.ToUtf8Buffer(fileName);
        fixed (byte* bytes = data)
        {
            return Raylib.ExportDataAsCode(bytes, data.Length, name.AsPointer());
        }
    }

    // -------------------------------------------------------------------
    // Text / strings
    // NOTE: unless stated otherwise, the results come from raylib's internal
    // static buffers - they are copied into the returned managed strings, so
    // the strings remain valid indefinitely, but the next call to the same
    // native function overwrites the native buffer.
    // -------------------------------------------------------------------

    /// <summary>
    /// Pointer-free <c>Raylib.TextFormat</c>.
    /// NOTE: the C# binding drops the printf-style variadic arguments, so this
    /// returns a copy of <paramref name="text"/> unchanged. For formatted text
    /// in C# just use string interpolation: <c>$"score: {score}"</c>.
    /// </summary>
    public static unsafe string TextFormat(string text)
    {
        using var buffer = Utf8StringUtils.ToUtf8Buffer(text);
        return ReadString(Raylib.TextFormat(buffer.AsPointer()));
    }

    /// <summary>Pointer-free <c>Raylib.TextInsert</c>. <paramref name="position"/> is a byte offset.</summary>
    public static unsafe string TextInsert(string text, string insert, int position)
    {
        using var t = Utf8StringUtils.ToUtf8Buffer(text);
        using var i = Utf8StringUtils.ToUtf8Buffer(insert);
        return ReadString(Raylib.TextInsert(t.AsPointer(), i.AsPointer(), position));
    }

    /// <summary>
    /// Pointer-free <c>Raylib.TextInsertAlloc</c>. The native result is
    /// heap-allocated and freed after being copied into the returned string.
    /// </summary>
    public static unsafe string TextInsertAlloc(string text, string insert, int position)
    {
        using var t = Utf8StringUtils.ToUtf8Buffer(text);
        using var i = Utf8StringUtils.ToUtf8Buffer(insert);
        SByte* result = Raylib.TextInsertAlloc(t.AsPointer(), i.AsPointer(), position);
        string managed = ReadString(result);
        Raylib.MemFree((void*)result);
        return managed;
    }

    /// <summary>Pointer-free <c>Raylib.TextReplace</c> (static result buffer, copied).</summary>
    public static unsafe string TextReplace(string text, string search, string replacement)
    {
        using var t = Utf8StringUtils.ToUtf8Buffer(text);
        using var s = Utf8StringUtils.ToUtf8Buffer(search);
        using var r = Utf8StringUtils.ToUtf8Buffer(replacement);
        return ReadString(Raylib.TextReplace(t.AsPointer(), s.AsPointer(), r.AsPointer()));
    }

    /// <summary>Pointer-free <c>Raylib.TextReplaceAlloc</c> (native result freed after copy).</summary>
    public static unsafe string TextReplaceAlloc(string text, string search, string replacement)
    {
        using var t = Utf8StringUtils.ToUtf8Buffer(text);
        using var s = Utf8StringUtils.ToUtf8Buffer(search);
        using var r = Utf8StringUtils.ToUtf8Buffer(replacement);
        SByte* result = Raylib.TextReplaceAlloc(t.AsPointer(), s.AsPointer(), r.AsPointer());
        string managed = ReadString(result);
        Raylib.MemFree((void*)result);
        return managed;
    }

    /// <summary>Pointer-free <c>Raylib.TextReplaceBetween</c> (static result buffer, copied).</summary>
    public static unsafe string TextReplaceBetween(string text, string begin, string end, string replacement)
    {
        using var t = Utf8StringUtils.ToUtf8Buffer(text);
        using var b = Utf8StringUtils.ToUtf8Buffer(begin);
        using var e = Utf8StringUtils.ToUtf8Buffer(end);
        using var r = Utf8StringUtils.ToUtf8Buffer(replacement);
        return ReadString(Raylib.TextReplaceBetween(t.AsPointer(), b.AsPointer(), e.AsPointer(), r.AsPointer()));
    }

    /// <summary>Pointer-free <c>Raylib.TextReplaceBetweenAlloc</c> (native result freed after copy).</summary>
    public static unsafe string TextReplaceBetweenAlloc(string text, string begin, string end, string replacement)
    {
        using var t = Utf8StringUtils.ToUtf8Buffer(text);
        using var b = Utf8StringUtils.ToUtf8Buffer(begin);
        using var e = Utf8StringUtils.ToUtf8Buffer(end);
        using var r = Utf8StringUtils.ToUtf8Buffer(replacement);
        SByte* result = Raylib.TextReplaceBetweenAlloc(t.AsPointer(), b.AsPointer(), e.AsPointer(), r.AsPointer());
        string managed = ReadString(result);
        Raylib.MemFree((void*)result);
        return managed;
    }

    /// <summary>Pointer-free <c>Raylib.TextJoin</c> (static result buffer, copied).</summary>
    public static unsafe string TextJoin(string[] textList, string delimiter)
    {
        if (textList == null || textList.Length == 0) return string.Empty;

        var bytes = new byte[textList.Length][];
        var handles = new GCHandle[textList.Length];
        var pointers = new SByte*[textList.Length];
        GCHandle listHandle = default;
        string result = string.Empty;
        try
        {
            for (int i = 0; i < textList.Length; i++)
            {
                bytes[i] = Encoding.UTF8.GetBytes(textList[i] ?? string.Empty);
                handles[i] = GCHandle.Alloc(bytes[i], GCHandleType.Pinned);
                pointers[i] = (SByte*)handles[i].AddrOfPinnedObject();
            }

            listHandle = GCHandle.Alloc(pointers, GCHandleType.Pinned);
            SByte** list = (SByte**)listHandle.AddrOfPinnedObject();

            using var delim = Utf8StringUtils.ToUtf8Buffer(delimiter ?? string.Empty);
            result = ReadString(Raylib.TextJoin(list, textList.Length, delim.AsPointer()));
        }
        finally
        {
            listHandle.Free();
            for (int i = 0; i < handles.Length; i++) handles[i].Free();
        }
        return result;
    }

    /// <summary>
    /// Pointer-free <c>Raylib.TextSplit</c>.
    /// NOTE: raylib 6.0 splits into a fixed set of MAX_TEXTSPLIT_COUNT static
    /// strings (256 chars each) - there is nothing to free, but strings longer
    /// than the static buffer are truncated by raylib itself.
    /// </summary>
    public static unsafe string[] TextSplit(string text, char delimiter)
    {
        using var buffer = Utf8StringUtils.ToUtf8Buffer(text);
        int count = 0;
        SByte** parts = Raylib.TextSplit(buffer.AsPointer(), delimiter, &count);
        if (parts == null || count <= 0) return Array.Empty<string>();

        var result = new string[count];
        for (int i = 0; i < count; i++) result[i] = ReadString(parts[i]);
        return result;
    }

    /// <summary>
    /// Pointer-free <c>Raylib.LoadTextLines</c>. The native line array is
    /// heap-allocated and freed (<c>UnloadTextLines</c>) after copying.
    /// </summary>
    public static unsafe string[] LoadTextLines(string text)
    {
        using var buffer = Utf8StringUtils.ToUtf8Buffer(text);
        int count = 0;
        SByte** lines = Raylib.LoadTextLines(buffer.AsPointer(), &count);
        if (lines == null || count <= 0) return Array.Empty<string>();

        var result = new string[count];
        for (int i = 0; i < count; i++) result[i] = ReadString(lines[i]);
        Raylib.UnloadTextLines(lines, &count);
        return result;
    }

    /// <summary>Pointer-free <c>Raylib.TextToUpper</c> (static result buffer, copied).</summary>
    public static unsafe string TextToUpper(string text)
    {
        using var buffer = Utf8StringUtils.ToUtf8Buffer(text);
        return ReadString(Raylib.TextToUpper(buffer.AsPointer()));
    }

    /// <summary>Pointer-free <c>Raylib.TextToLower</c> (static result buffer, copied).</summary>
    public static unsafe string TextToLower(string text)
    {
        using var buffer = Utf8StringUtils.ToUtf8Buffer(text);
        return ReadString(Raylib.TextToLower(buffer.AsPointer()));
    }

    /// <summary>Pointer-free <c>Raylib.TextToPascal</c> (static result buffer, copied).</summary>
    public static unsafe string TextToPascal(string text)
    {
        using var buffer = Utf8StringUtils.ToUtf8Buffer(text);
        return ReadString(Raylib.TextToPascal(buffer.AsPointer()));
    }

    /// <summary>Pointer-free <c>Raylib.TextToSnake</c> (static result buffer, copied).</summary>
    public static unsafe string TextToSnake(string text)
    {
        using var buffer = Utf8StringUtils.ToUtf8Buffer(text);
        return ReadString(Raylib.TextToSnake(buffer.AsPointer()));
    }

    /// <summary>Pointer-free <c>Raylib.TextToCamel</c> (static result buffer, copied).</summary>
    public static unsafe string TextToCamel(string text)
    {
        using var buffer = Utf8StringUtils.ToUtf8Buffer(text);
        return ReadString(Raylib.TextToCamel(buffer.AsPointer()));
    }

    /// <summary>Pointer-free <c>Raylib.TextRemoveSpaces</c> (static result buffer, copied).</summary>
    public static unsafe string TextRemoveSpaces(string text)
    {
        using var buffer = Utf8StringUtils.ToUtf8Buffer(text);
        return ReadString(Raylib.TextRemoveSpaces(buffer.AsPointer()));
    }

    /// <summary>Pointer-free <c>Raylib.GetTextBetween</c> (static result buffer, copied).</summary>
    public static unsafe string GetTextBetween(string text, string begin, string end)
    {
        using var t = Utf8StringUtils.ToUtf8Buffer(text);
        using var b = Utf8StringUtils.ToUtf8Buffer(begin);
        using var e = Utf8StringUtils.ToUtf8Buffer(end);
        return ReadString(Raylib.GetTextBetween(t.AsPointer(), b.AsPointer(), e.AsPointer()));
    }

    /// <summary>Pointer-free <c>Raylib.TextIsEqual</c>.</summary>
    public static unsafe bool TextIsEqual(string text1, string text2)
    {
        using var a = Utf8StringUtils.ToUtf8Buffer(text1);
        using var b = Utf8StringUtils.ToUtf8Buffer(text2);
        return Raylib.TextIsEqual(a.AsPointer(), b.AsPointer());
    }

    /// <summary>
    /// Pointer-free <c>Raylib.TextLength</c>. Returns the length in UTF-8 BYTES
    /// (not characters). Use <c>Raylib.GetCodepointCount(text)</c> for the
    /// character count.
    /// </summary>
    public static unsafe uint TextLength(string text)
    {
        using var buffer = Utf8StringUtils.ToUtf8Buffer(text);
        return Raylib.TextLength(buffer.AsPointer());
    }

    /// <summary>Pointer-free <c>Raylib.TextFindIndex</c> (-1 if not found).</summary>
    public static unsafe int TextFindIndex(string text, string search)
    {
        using var t = Utf8StringUtils.ToUtf8Buffer(text);
        using var s = Utf8StringUtils.ToUtf8Buffer(search);
        return Raylib.TextFindIndex(t.AsPointer(), s.AsPointer());
    }

    /// <summary>Pointer-free <c>Raylib.TextToInteger</c>.</summary>
    public static unsafe int TextToInteger(string text)
    {
        using var buffer = Utf8StringUtils.ToUtf8Buffer(text);
        return Raylib.TextToInteger(buffer.AsPointer());
    }

    /// <summary>Pointer-free <c>Raylib.TextToFloat</c>.</summary>
    public static unsafe float TextToFloat(string text)
    {
        using var buffer = Utf8StringUtils.ToUtf8Buffer(text);
        return Raylib.TextToFloat(buffer.AsPointer());
    }

    /// <summary>
    /// Pointer-free <c>Raylib.TextCopy</c>. (C# strings are already immutable
    /// copies, so this mostly exists for API symmetry.)
    /// </summary>
    public static unsafe string TextCopy(string src)
    {
        using var s = Utf8StringUtils.ToUtf8Buffer(src ?? string.Empty);
        int length = (int)Raylib.TextLength(s.AsPointer());
        var dst = new byte[length + 1];
        fixed (byte* d = dst)
        {
            Raylib.TextCopy((SByte*)d, s.AsPointer());
            return ReadString((SByte*)d);
        }
    }

    /// <summary>
    /// Pointer-free <c>Raylib.TextAppend</c>. Appends <paramref name="append"/>
    /// into <paramref name="text"/> at <paramref name="position"/> (a byte
    /// offset, updated to the new cursor position) and returns the new string.
    /// </summary>
    public static unsafe string TextAppend(string text, string append, ref int position)
    {
        text ??= string.Empty;
        append ??= string.Empty;

        int textBytes = Encoding.UTF8.GetByteCount(text);
        var buffer = new byte[textBytes + append.Length + 8];
        Encoding.UTF8.GetBytes(text, 0, text.Length, buffer, 0);

        using var appendBuf = Utf8StringUtils.ToUtf8Buffer(append);
        int pos = position;
        fixed (byte* b = buffer)
        {
            Raylib.TextAppend((SByte*)b, appendBuf.AsPointer(), &pos);
            position = pos;
            return ReadString((SByte*)b);
        }
    }

    /// <summary>
    /// Pointer-free <c>Raylib.GetCodepointNext</c>. Returns the codepoint at the
    /// start of <paramref name="text"/> and the number of bytes it occupies in
    /// <paramref name="codepointSize"/>.
    /// </summary>
    public static unsafe int GetCodepointNext(string text, ref int codepointSize)
    {
        using var buffer = Utf8StringUtils.ToUtf8Buffer(text);
        int size = codepointSize;
        int codepoint = Raylib.GetCodepointNext(buffer.AsPointer(), &size);
        codepointSize = size;
        return codepoint;
    }

    /// <summary>
    /// Pointer-free <c>Raylib.GetCodepointPrevious</c>. Returns the last codepoint
    /// of <paramref name="text"/> (the native call scans the string backwards from
    /// its end) and the number of bytes it occupies in <paramref name="codepointSize"/>.
    /// Returns 0x3f ('?') on failure, e.g. for an empty string.
    /// </summary>
    public static unsafe int GetCodepointPrevious(string text, ref int codepointSize)
    {
        using var buffer = Utf8StringUtils.ToUtf8Buffer(text);
        int size = codepointSize;
        int codepoint = Raylib.GetCodepointPrevious(buffer.AsPointer(), &size);
        codepointSize = size;
        return codepoint;
    }

    /// <summary>
    /// Pointer-free <c>Raylib.GetKeyName</c>: layout-aware key name
    /// (static result buffer, copied).
    /// </summary>
    public static unsafe string GetKeyName(KeyboardKey key)
    {
        return ReadString(Raylib.GetKeyName(key));
    }

    // -------------------------------------------------------------------
    // Images / pixels
    // -------------------------------------------------------------------

    /// <summary>
    /// Pointer-free <c>Raylib.LoadImageColors</c>: all pixels of the image as a
    /// managed Color[] (RGBA, image.Width * image.Height entries). The native
    /// array is freed (<c>UnloadImageColors</c>) after copying.
    /// </summary>
    public static unsafe Color[] LoadImageColors(Image image)
    {
        long count = (long)image.Width * image.Height;
        if (count <= 0) return Array.Empty<Color>();

        Color* colors = Raylib.LoadImageColors(image);
        if (colors == null) return Array.Empty<Color>();
        var result = CopyArray(colors, (int)count);
        Raylib.UnloadImageColors(colors);
        return result;
    }

    /// <summary>
    /// Pointer-free <c>Raylib.LoadImagePalette</c>: up to
    /// <paramref name="maxPaletteSize"/> unique colors as a managed array. The
    /// native array is freed (<c>UnloadImagePalette</c>) after copying.
    /// </summary>
    public static unsafe Color[] LoadImagePalette(Image image, int maxPaletteSize, ref int colorCount)
    {
        int count = colorCount;
        Color* colors = Raylib.LoadImagePalette(image, maxPaletteSize, &count);
        colorCount = count;
        if (colors == null || count <= 0)
        {
            colorCount = 0;
            return Array.Empty<Color>();
        }
        var result = CopyArray(colors, count);
        Raylib.UnloadImagePalette(colors);
        return result;
    }

    /// <summary>
    /// Pointer-free <c>Raylib.ExportImageToMemory</c>: encodes the image into a
    /// managed byte array. <paramref name="fileType"/> is the format extension
    /// (e.g. ".png", ".tga"). The native buffer is freed after copying.
    /// </summary>
    public static unsafe byte[] ExportImageToMemory(Image image, string fileType)
    {
        using var type = Utf8StringUtils.ToUtf8Buffer(fileType);
        int size = 0;
        byte* data = Raylib.ExportImageToMemory(image, type.AsPointer(), &size);
        if (data == null || size <= 0) return Array.Empty<byte>();
        var result = new byte[size];
        Marshal.Copy((IntPtr)data, result, 0, size);
        Raylib.MemFree((void*)data);
        return result;
    }

    /// <summary>
    /// Pointer-free <c>Raylib.GetPixelColor</c>: decodes the FIRST pixel of
    /// <paramref name="pixelData"/> interpreted as <paramref name="format"/>.
    /// </summary>
    public static unsafe Color GetPixelColor(byte[] pixelData, PixelFormat format)
    {
        if (pixelData == null || pixelData.Length == 0)
            throw new ArgumentException("pixelData must not be empty.", nameof(pixelData));
        fixed (byte* p = pixelData)
        {
            return Raylib.GetPixelColor((void*)p, format);
        }
    }

    /// <summary>
    /// Pointer-free <c>Raylib.SetPixelColor</c>: writes <paramref name="color"/>
    /// encoded as <paramref name="format"/> into the FIRST pixel of
    /// <paramref name="pixelData"/> (in place).
    /// </summary>
    public static unsafe void SetPixelColor(byte[] pixelData, Color color, PixelFormat format)
    {
        if (pixelData == null || pixelData.Length == 0)
            throw new ArgumentException("pixelData must not be empty.", nameof(pixelData));
        fixed (byte* p = pixelData)
        {
            Raylib.SetPixelColor((void*)p, color, format);
        }
    }

    /// <summary>
    /// Pointer-free <c>Raylib.GenImageFontAtlas</c>.
    /// <paramref name="glyphs"/> may be the managed copy from
    /// <see cref="LoadFontData(byte[], int, int[], FontType, out IntPtr)"/> -
    /// its pixel data stays alive as long as the native glyph handle does.
    /// <paramref name="packMethod"/>: 0 = default (rRay), 1 = skyline.
    /// The native glyphRecs array is freed after copying.
    /// </summary>
    public static unsafe Image GenImageFontAtlas(
        GlyphInfo[] glyphs, out Rectangle[] glyphRecs, int fontSize, int padding, int packMethod)
    {
        if (glyphs == null || glyphs.Length == 0)
        {
            glyphRecs = Array.Empty<Rectangle>();
            return default;
        }

        fixed (GlyphInfo* chars = &glyphs[0])
        {
            Rectangle* recs = null;
            Image image = Raylib.GenImageFontAtlas(chars, &recs, glyphs.Length, fontSize, padding, packMethod);
            glyphRecs = recs == null
                ? Array.Empty<Rectangle>()
                : CopyArray(recs, glyphs.Length);
            if (recs != null) Raylib.MemFree((void*)recs);
            return image;
        }
    }

    // -------------------------------------------------------------------
    // Fonts
    // -------------------------------------------------------------------

    /// <summary>
    /// Pointer-free <c>Raylib.LoadFontData</c>. Decodes raw font file data
    /// (e.g. from <see cref="LoadFileData(string)"/>) into managed
    /// <see cref="GlyphInfo"/> structs.
    ///
    /// IMPORTANT: the managed structs are copies, but their
    /// <c>GlyphInfo.Image.Data</c> pointers reference native pixel data owned
    /// by the native glyph array. Pass <paramref name="glyphHandle"/> to
    /// <see cref="UnloadFontData(IntPtr, int)"/> once the glyph images are no
    /// longer needed (e.g. after building the font texture), and not before.
    ///
    /// <paramref name="codepoints"/>: optional explicit codepoint list; pass
    /// null for the default character set.
    /// </summary>
    public static unsafe GlyphInfo[] LoadFontData(
        byte[] fileData, int fontSize, int[]? codepoints, FontType type, out IntPtr glyphHandle)
    {
        int glyphCount = 0;
        fixed (byte* data = fileData)
        fixed (int* codepointsPtr = codepoints)
        {
            GlyphInfo* glyphs = Raylib.LoadFontData(
                data, fileData.Length, fontSize, codepointsPtr,
                codepoints?.Length ?? 0, type, &glyphCount);

            if (glyphs == null || glyphCount <= 0)
            {
                glyphHandle = IntPtr.Zero;
                return Array.Empty<GlyphInfo>();
            }

            var result = CopyArray(glyphs, glyphCount);
            glyphHandle = (IntPtr)glyphs;
            return result;
        }
    }

    /// <summary>
    /// Companion to <see cref="LoadFontData(byte[], int, int[], FontType, out IntPtr)"/>:
    /// frees the native glyph array and its embedded image data
    /// (<c>Raylib.UnloadFontData</c>).
    /// </summary>
    public static unsafe void UnloadFontData(IntPtr glyphHandle, int glyphCount)
    {
        if (glyphHandle == IntPtr.Zero) return;
        Raylib.UnloadFontData((GlyphInfo*)glyphHandle, glyphCount);
    }

    /// <summary>Pointer-free <c>Raylib.ExportFontAsCode</c>.</summary>
    public static unsafe bool ExportFontAsCode(Font font, string fileName)
    {
        using var name = Utf8StringUtils.ToUtf8Buffer(fileName);
        return Raylib.ExportFontAsCode(font, name.AsPointer());
    }

    // -------------------------------------------------------------------
    // Audio
    // -------------------------------------------------------------------

    /// <summary>
    /// Pointer-free <c>Raylib.LoadWaveSamples</c>: the wave as a managed
    /// float[] (frameCount samples, 32-bit float). The native array is freed
    /// (<c>UnloadWaveSamples</c>) after copying.
    /// </summary>
    public static unsafe float[] LoadWaveSamples(Wave wave)
    {
        if (wave.FrameCount == 0) return Array.Empty<float>();
        float* samples = Raylib.LoadWaveSamples(wave);
        if (samples == null) return Array.Empty<float>();
        var result = new float[wave.FrameCount];
        Marshal.Copy((IntPtr)samples, result, 0, result.Length);
        Raylib.UnloadWaveSamples(samples);
        return result;
    }

    // -------------------------------------------------------------------
    // Materials
    // -------------------------------------------------------------------

    /// <summary>
    /// Pointer-free <c>Raylib.LoadMaterials</c>: loads the material definitions
    /// from an .mtl file into managed <see cref="Material"/> structs; the
    /// native material array is freed after copying.
    ///
    /// IMPORTANT: each <see cref="Material"/> still contains a native pointer
    /// to its maps array. Release each material with
    /// <c>Raylib.UnloadMaterial(materials[i])</c> when done (that also frees
    /// the maps array and unloads the GPU resources).
    /// </summary>
    public static unsafe Material[] LoadMaterials(string fileName)
    {
        using var name = Utf8StringUtils.ToUtf8Buffer(fileName);
        int count = 0;
        Material* materials = Raylib.LoadMaterials(name.AsPointer(), &count);
        if (materials == null || count <= 0) return Array.Empty<Material>();
        var result = CopyArray(materials, count);
        Raylib.MemFree((void*)materials);
        return result;
    }

    // -------------------------------------------------------------------
    // Shaders
    // -------------------------------------------------------------------

    /// <summary>
    /// Pointer-free <c>Raylib.LoadShaderFromMemory</c>. A <c>null</c> stage is
    /// passed as a real NULL, which tells raylib 6.0 to link that stage with
    /// its built-in default shader (default VS: <c>mvp * vertexPosition</c>;
    /// default FS: textured-quad shading). NOTE: an empty string is NOT null -
    /// it will fail to compile, so pass <c>null</c> for stages you don't have.
    /// The returned Shader is a native object; release it with
    /// <c>Raylib.UnloadShader</c>.
    /// </summary>
    public static unsafe Shader LoadShaderFromMemory(string? vertexCode, string? fragmentCode)
    {
        IntPtr vsMem = IntPtr.Zero;
        IntPtr fsMem = IntPtr.Zero;
        try
        {
            if (vertexCode != null) vsMem = Marshal.StringToCoTaskMemUTF8(vertexCode);
            if (fragmentCode != null) fsMem = Marshal.StringToCoTaskMemUTF8(fragmentCode);

            sbyte* vsPtr = null;
            sbyte* fsPtr = null;
            if (vsMem != IntPtr.Zero) vsPtr = (sbyte*)vsMem.ToPointer();
            if (fsMem != IntPtr.Zero) fsPtr = (sbyte*)fsMem.ToPointer();

            return Raylib.LoadShaderFromMemory(vsPtr, fsPtr);
        }
        finally
        {
            if (vsMem != IntPtr.Zero) Marshal.ZeroFreeCoTaskMemUTF8(vsMem);
            if (fsMem != IntPtr.Zero) Marshal.ZeroFreeCoTaskMemUTF8(fsMem);
        }
    }

    // -------------------------------------------------------------------
    // Window
    // -------------------------------------------------------------------

    /// <summary>
    /// Pointer-free <c>Raylib.GetWindowHandle</c>: the native platform window
    /// handle (HWND on Windows, X11 Window on Linux, NSView on macOS) as an
    /// IntPtr. Not owned by this wrapper - never free it.
    /// </summary>
    public static unsafe IntPtr GetWindowHandle()
    {
        return (IntPtr)Raylib.GetWindowHandle();
    }

    // -------------------------------------------------------------------
    // Raymath (output-parameter functions)
    // -------------------------------------------------------------------

    /// <summary>
    /// Pointer-free <c>Raymath.MatrixDecompose</c>.
    /// </summary>
    public static unsafe void MatrixDecompose(
        Matrix4x4 mat, out Vector3 translation, out Quaternion rotation, out Vector3 scale)
    {
        Vector3 t = default;
        Quaternion r = default;
        Vector3 s = default;
        Vector3* tp = &t;
        Quaternion* rp = &r;
        Vector3* sp = &s;
        Raymath.MatrixDecompose(mat, tp, rp, sp);
        translation = t;
        rotation = r;
        scale = s;
    }

    /// <summary>
    /// Pointer-free <c>Raymath.QuaternionToAxisAngle</c>.
    /// </summary>
    public static unsafe void QuaternionToAxisAngle(Quaternion q, out Vector3 axis, out float angle)
    {
        Vector3 a = default;
        float ang = 0f;
        Vector3* ap = &a;
        float* angp = &ang;
        Raymath.QuaternionToAxisAngle(q, ap, angp);
        axis = a;
        angle = ang;
    }

    /// <summary>
    /// Pointer-free <c>Raymath.Vector3OrthoNormalize</c>: orthogonalizes
    /// <paramref name="v2"/> relative to <paramref name="v1"/> and normalizes
    /// both (in place).
    /// </summary>
    public static unsafe void Vector3OrthoNormalize(ref Vector3 v1, ref Vector3 v2)
    {
        fixed (Vector3* p1 = &v1)
        fixed (Vector3* p2 = &v2)
        {
            Raymath.Vector3OrthoNormalize(p1, p2);
        }
    }
}
