// ---------------------------------------------------------------------------
// Resource.cs
//
// Central asset loading for Euler. Currently supports shaders (with a small
// #include pre-processor); add LoadTexture / LoadSound / ... here as the
// engine needs more asset types.
//
// Shader loading:
//
//     Shader fs = Resource.LoadShader("Assets/Shaders/RaymarcherDisplay.fs", ShaderType.Pixel);
//     Shader cs = Resource.LoadShader("Assets/Shaders/Raymarcher.comp", ShaderType.Compute);
//
// raylib 6.0 has no shader #include support - the file text goes verbatim to
// the GL driver compiler - so LoadShader pre-processes the source first:
// every #include "path" line is replaced IN PLACE by the content of the
// included file (recursively), as if all the files were one big shader.
// Include paths are resolved relative to the directory of the file containing
// the directive: a shader in .../Shaders/Raymarcher.comp including
// "NewFolder/shader.inc" reads .../Shaders/NewFolder/shader.inc.
//
// NOTES:
//   - #version must stay the first non-comment line of the FINAL source. Keep
//     #version before any #include in the main file, and never put a #version
//     in an .inc file.
//   - ShaderType.Pixel / Vertex load a single stage. raylib 6.0 links the
//     missing stage with its built-in default shader (default VS is
//     gl_Position = mvp * vertexPosition - a full-screen pass-through, so a
//     .fs loaded as ShaderType.Pixel needs no explicit vertex stage).
//   - ShaderType.Compute: the #include pre-processing above applies to
//     compute sources EXACTLY like the graphic stages - the file text is
//     fully expanded (recursively) before it reaches the GL driver compiler.
//     The preprocessed source is then compiled through rlgl's low-level API
//     (raylib 6.0 has no high-level compute loader) and returns a Shader
//     with Locs == null: validate with Id != 0 (NOT IsShaderValid), resolve
//     uniforms with Raylib.GetShaderLocation, set them with
//     Raylib.SetShaderValue*, dispatch with Rlgl.ComputeShaderDispatch(...),
//     and unload with Raylib.UnloadShader. Compute shaders additionally
//     require GLSL #version 430+ (checked before compiling) and a raylib
//     built with GRAPHICS_API_OPENGL_43 - this project deploys its own 4.3
//     build (Euler/native/build-raylib-linux.sh, see the
//     DeployCustomRaylibLinux target in Euler.csproj).
// ---------------------------------------------------------------------------

using System.Text;
using Raylib_cs;
using Raylib = Raylib_cs.Raylib;

namespace Euler.Utils;

/// <summary>The GLSL stage a shader file contains.</summary>
public enum ShaderType
{
    /// <summary>
    /// Fragment shader (.fs). The vertex stage is filled in by raylib's
    /// built-in default vertex shader (mvp * vertexPosition).
    /// </summary>
    Pixel,

    /// <summary>
    /// Vertex shader (.vs). The fragment stage is filled in by raylib's
    /// built-in default fragment shader (textured-quad shading).
    /// </summary>
    Vertex,

    /// <summary>
    /// Compute shader (.comp). Requires a raylib build with
    /// GRAPHICS_API_OPENGL_43.
    /// </summary>
    Compute
}

/// <summary>Raised when a shader cannot be preprocessed or fails to compile.</summary>
public class ShaderException : Exception
{
    public ShaderException(string message) : base(message) { }
}

/// <summary>
/// Central asset loading for the engine. Currently supports shaders (with
/// #include preprocessing); textures, sounds, and other asset types are
/// added here as the engine needs them.
/// </summary>
public static class Resource
{
    /// <summary>Maximum nested include depth (safeguard; cycles are caught first).</summary>
    private const int MaxIncludeDepth = 32;

    /// <summary>GL_COMPUTE_SHADER (see rlgl.h RL_COMPUTE_SHADER / glad.h).</summary>
    private const int GlComputeShader = 0x91B9;

    /// <summary>
    /// Loads, pre-processes (#include expansion) and compiles a shader file,
    /// returning the raylib <see cref="Raylib_cs.Shader"/>.
    /// The #include pre-processing applies to ALL stages, including compute
    /// (<see cref="ShaderType.Compute"/>): the file is read and fully expanded
    /// first, and only the resulting single source is compiled.
    /// Relative paths resolve against the executable directory first, then
    /// the current working directory (see <see cref="ResolveAssetPath"/>).
    /// Throws <see cref="ShaderException"/> (or <see cref="FileNotFoundException"/>)
    /// on any preprocessing or compile failure - raylib silently falls back to
    /// its default shader on failed links, which would otherwise hide errors.
    /// </summary>
    public static Raylib_cs.Shader LoadShader(string path, ShaderType type)
    {
        string file = ResolveAssetPath(path);
        if (!File.Exists(file))
            throw new FileNotFoundException($"Shader file not found: {path} (resolved to {file}).", file);

        string source = Preprocess(File.ReadAllText(file), file);

        return type switch
        {
            ShaderType.Pixel   => LoadGraphicShader(null, source),
            ShaderType.Vertex  => LoadGraphicShader(source, null),
            ShaderType.Compute => LoadComputeShader(source),
            _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Unknown shader type.")
        };
    }

    /// <summary>
    /// Pre-processes a shader source: each <c>#include "path"</c> line is
    /// replaced by the content of the included file, recursively, with paths
    /// resolved relative to the directory of the file containing the
    /// directive. Lines that are not #include directives pass through
    /// unchanged (line endings preserved).
    /// </summary>
    /// <param name="source">Raw shader text.</param>
    /// <param name="filePath">The file the source came from (used as the base
    /// for include resolution; made absolute internally).</param>
    public static string Preprocess(string source, string filePath)
    {
        var stack = new HashSet<string>(StringComparer.Ordinal);
        return ResolveIncludes(source, Path.GetFullPath(filePath), stack, 0);
    }

    /// <summary>
    /// Resolves an asset path: absolute paths are normalized as-is; relative
    /// paths are tried against the executable's directory first (works with
    /// <c>dotnet run</c> and published builds), then the current working
    /// directory.
    /// </summary>
    public static string ResolveAssetPath(string path)
    {
        if (Path.IsPathRooted(path))
            return Path.GetFullPath(path);

        string appDirPath = Path.Combine(AppContext.BaseDirectory, path);
        if (File.Exists(appDirPath))
            return appDirPath;

        return Path.GetFullPath(path);
    }

    // -------------------------------------------------------------------
    // #include expansion
    // -------------------------------------------------------------------

    private static string ResolveIncludes(string source, string fullPath, HashSet<string> stack, int depth)
    {
        if (depth > MaxIncludeDepth)
            throw new ShaderException(
                $"Shader include depth greater than {MaxIncludeDepth} while processing {fullPath} - check for circular includes.");

        if (!stack.Add(fullPath))
            throw new ShaderException($"Circular shader include: {fullPath} is already being expanded.");

        try
        {
            string directory = Path.GetDirectoryName(fullPath) ?? string.Empty;
            string[] lines = source.Split('\n');
            var result = new StringBuilder(source.Length + 1024);

            for (int i = 0; i < lines.Length; i++)
            {
                string? includePath = GetIncludePath(lines[i]);

                if (includePath is null)
                {
                    result.Append(lines[i]);
                }
                else
                {
                    string includeFile = Path.GetFullPath(Path.Combine(directory, includePath));
                    if (!File.Exists(includeFile))
                        throw new FileNotFoundException(
                            $"Cannot find shader include \"{includePath}\" (resolved to {includeFile}), included from {fullPath} (line {i + 1}).",
                            includeFile);

                    string included = ResolveIncludes(File.ReadAllText(includeFile), includeFile, stack, depth + 1);
                    if (included.Length > 0 && !included.EndsWith('\n'))
                        included += "\n";      // never glue the next line onto the include

                    result.Append(included);
                }

                if (i + 1 < lines.Length)
                    result.Append('\n');       // rejoin the original line endings
            }

            return result.ToString();
        }
        finally
        {
            stack.Remove(fullPath);
        }
    }

    /// <summary>
    /// Returns the include path if <paramref name="line"/> is a
    /// <c>#include "path"</c> directive (leading whitespace allowed, trailing
    /// C-style comments allowed), null if it is not an #include directive, and
    /// throws <see cref="ShaderException"/> for malformed directives.
    /// </summary>
    private static string? GetIncludePath(string line)
    {
        string trimmed = line.TrimStart();
        if (!trimmed.StartsWith("#include", StringComparison.Ordinal))
            return null;

        // "#include" must be a whole word: #includeFoo is not a directive.
        if (trimmed.Length > "#include".Length && !char.IsWhiteSpace(trimmed["#include".Length]))
            return null;

        string rest = trimmed["#include".Length..].Trim();
        if (rest.Length == 0)
            throw new ShaderException($"Malformed #include directive (missing path): \"{line.Trim()}\"");

        if (rest[0] != '"')
            throw new ShaderException($"Only #include \"...\" (quoted paths) are supported: \"{line.Trim()}\"");

        int close = rest.IndexOf('"', 1);
        if (close < 1)
            throw new ShaderException($"Unterminated #include path: \"{line.Trim()}\"");

        string path = rest[1..close];
        if (path.Length == 0)
            throw new ShaderException($"Empty #include path: \"{line.Trim()}\"");

        string trailing = rest[(close + 1)..].Trim();
        if (trailing.Length > 0
            && !trailing.StartsWith("//", StringComparison.Ordinal)
            && !trailing.StartsWith("/*", StringComparison.Ordinal))
            throw new ShaderException($"Unexpected text after #include path: \"{line.Trim()}\"");

        return path;
    }

    // -------------------------------------------------------------------
    // Compilation
    // -------------------------------------------------------------------

    /// <summary>
    /// Compiles one graphic pipeline stage (the null stage is linked from
    /// raylib's built-in default shader). Throws <see cref="ShaderException"/>
    /// when the stage fails: raylib silently substitutes its default shader id
    /// for failed links, so IsShaderValid alone would not catch it.
    /// </summary>
    private static Raylib_cs.Shader LoadGraphicShader(string? vertexCode, string? fragmentCode)
    {
        Raylib_cs.Shader shader = RL.LoadShaderFromMemory(vertexCode, fragmentCode);

        if (!Raylib.IsShaderValid(shader) || shader.Id == Raylib_cs.Rlgl.GetShaderIdDefault())
            throw new ShaderException(
                "Shader failed to compile or link (raylib fell back to its default shader). " +
                "Check the GLSL error above in the console output.");

        return shader;
    }

    /// <summary>
    /// Compiles a compute shader via the low-level rlgl API (raylib 6.0 has no
    /// high-level LoadComputeShader). The source must already be preprocessed
    /// (#includes expanded) and must declare #version 430+ (enforced here with
    /// a clear error, since a wrong version produces only a cryptic driver
    /// log). Returns a Shader with Locs == null.
    /// </summary>
    private static unsafe Raylib_cs.Shader LoadComputeShader(string source)
    {
        int version = GetGlslVersion(source);
        if (version < 430)
            throw new ShaderException(
                "Compute shaders require GLSL #version 430 or newer" +
                (version > 0
                    ? $" but this shader declares #version {version}."
                    : " but this shader declares no #version.") +
                " Put '#version 430' as the first line of the .comp file, before any #include.");

        using var code = source.ToUtf8Buffer();

        uint csId = Raylib_cs.Rlgl.LoadShader(code.AsPointer(), GlComputeShader);
        if (csId == 0)
            throw new ShaderException(
                "Compute shader failed to compile. Check the GLSL error above in the console output.");

        uint programId = Raylib_cs.Rlgl.LoadShaderProgramCompute(csId);

        // Unlike the graphics path, raylib's compute path does NOT detach and
        // delete the shader object after linking - release it ourselves.
        Raylib_cs.Rlgl.UnloadShader(csId);

        if (programId == 0)
            throw new ShaderException(
                "Compute shader failed to link. Compute shaders require a raylib built with " +
                "GRAPHICS_API_OPENGL_43 - see Euler/native/build-raylib-linux.sh.");

        return new Raylib_cs.Shader { Id = programId };
    }

    /// <summary>
    /// Returns the GLSL version declared by the first <c>#version</c>
    /// directive in <paramref name="source"/> (e.g. 430 for "#version 430",
    /// 450 for "#version 450 core"), or 0 when the source declares none.
    /// Throws <see cref="ShaderException"/> for a malformed directive.
    /// </summary>
    private static int GetGlslVersion(string source)
    {
        foreach (string line in source.Split('\n'))
        {
            string trimmed = line.TrimStart();
            if (!trimmed.StartsWith("#version", StringComparison.Ordinal))
                continue;    // comments/directives before #version are legal GLSL

            // "#version" must be a whole word: #versionFoo is not a directive.
            if (trimmed.Length > "#version".Length && !char.IsWhiteSpace(trimmed["#version".Length]))
                continue;

            string rest = trimmed["#version".Length..].Trim();
            int space = rest.IndexOf(' ');
            string token = space < 0 ? rest : rest[..space];
            if (!int.TryParse(token, out int version))
                throw new ShaderException($"Malformed #version directive: \"{trimmed}\"");

            return version;
        }

        return 0;
    }
}
