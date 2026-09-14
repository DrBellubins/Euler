using System.Numerics;
using Euler.Utils;
using Raylib_cs;

namespace Euler.GameEngine;

/// <summary>
/// Full-screen raymarch renderer for flat, textured plane primitives
/// (<see cref="PlanePrimitive"/>). Draws one full-screen quad through a
/// custom shader; the fragment stage raymarches the scene's planes and
/// shades the closest hit with a tiled texture.
/// </summary>
public class Raymarcher
{
    public const int MaxPlanes = 8;

    private const string VertexShaderPath = "Assets/Shaders/Raymarcher.vs";
    private const string FragmentShaderPath = "Assets/Shaders/Raymarcher.fs";

    private readonly Shader _shader;
    private readonly Texture2D _planeTex;
    private readonly Texture2D _quadTex;   // 1x1 white, only exists to fill the screen
    private readonly PlanePrimitive[] _planes = new PlanePrimitive[MaxPlanes];
    private readonly float[] _planeData = new float[MaxPlanes * 12];
    private int _planeCount;

    private int _locResolution;
    private int _locCamToWorld;
    private int _locFocal;
    private int _locPlaneCount;
    private int _locPlaneData;
    private int _locPlaneTex;

    public Raymarcher(Texture2D planeTexture)
    {
        _planeTex = planeTexture;

        _shader = Raylib.LoadShader(ResolveAsset(VertexShaderPath), ResolveAsset(FragmentShaderPath));
        if (!Raylib.IsShaderValid(_shader))
            throw new InvalidOperationException("Raymarcher shader failed to compile/load.");

        _locResolution = Raylib.GetShaderLocation(_shader, "Resolution");
        _locCamToWorld = Raylib.GetShaderLocation(_shader, "CamToWorld");
        _locFocal      = Raylib.GetShaderLocation(_shader, "Focal");
        _locPlaneCount = Raylib.GetShaderLocation(_shader, "PlaneCount");
        _locPlaneData  = Raylib.GetShaderLocation(_shader, "PlaneData");
        _locPlaneTex   = Raylib.GetShaderLocation(_shader, "PlaneTex");

        // A single white pixel is all the full-screen quad needs.
        Image img = Raylib.GenImageColor(1, 1, Color.White);
        _quadTex = Raylib.LoadTextureFromImage(img);
        Raylib.UnloadImage(img);
    }

    /// <summary>Adds a plane (up to <see cref="MaxPlanes"/>).</summary>
    public void AddPlane(PlanePrimitive plane)
    {
        if (_planeCount >= MaxPlanes)
            throw new InvalidOperationException($"Raymarcher supports at most {MaxPlanes} planes.");
        _planes[_planeCount++] = plane;
    }

    /// <summary>Draws the raymarched scene for <paramref name="camera"/> (fills the whole window).</summary>
    public void Draw(Camera3D camera)
    {
        for (int i = 0; i < _planeCount; i++)
            _planes[i].WriteInto(_planeData, i);

        Raylib.BeginShaderMode(_shader);

        Raylib.SetShaderValue(_shader, _locResolution,
            new Vector2(Engine.ScreenWidth, Engine.ScreenHeight), ShaderUniformDataType.Vec2);
        SetCameraMatrix(camera);
        Raylib.SetShaderValue(_shader, _locFocal,
            1f / MathF.Tan(GMath.ToRadians(camera.FovY) * 0.5f), ShaderUniformDataType.Float);
        Raylib.SetShaderValue(_shader, _locPlaneCount, _planeCount, ShaderUniformDataType.Int);
        if (_planeCount > 0)
            Raylib.SetShaderValueV(_shader, _locPlaneData, _planeData, ShaderUniformDataType.Vec4, _planeCount * 3);
        Raylib.SetShaderValueTexture(_shader, _locPlaneTex, _planeTex);

        // One full-screen quad; the fragment shader does the actual work.
        Raylib.DrawTextureRec(_quadTex,
            new Rectangle(0, 0, Engine.ScreenWidth, Engine.ScreenHeight),
            Vector2.Zero, Color.White);

        Raylib.EndShaderMode();
    }

    public void Unload()
    {
        Raylib.UnloadShader(_shader);
        Raylib.UnloadTexture(_planeTex);
        Raylib.UnloadTexture(_quadTex);
    }

    /// <summary>
    /// Resolves a relative asset path against the executable directory
    /// (works with <c>dotnet run</c> and published builds), falling back to
    /// the path as-is (Rider sets CWD to the output dir).
    /// </summary>
    private static string ResolveAsset(string relativePath)
    {
        string appDirPath = System.IO.Path.Combine(AppContext.BaseDirectory, relativePath);
        return Raylib.FileExists(appDirPath) ? appDirPath : relativePath;
    }

    /// <summary>
    /// Builds the camera-to-world matrix from the camera's
    /// position/orientation (x = right, y = true-up, z = -forward) and uploads
    /// it.
    /// </summary>
    private void SetCameraMatrix(Camera3D camera)
    {
        Vector3 forward = Vector3.Normalize(camera.Target - camera.Position);
        Vector3 right   = Vector3.Normalize(Vector3.Cross(camera.Up, forward));
        Vector3 up      = Vector3.Cross(forward, right);

        // Uploaded via SetShaderValueMatrix (glUniformMatrix4fv), NOT via
        // SetShaderValueV(..., Vec4, 4): that path calls glUniform4fv(loc, 4,
        // ...), which Mesa 26.x (llvmpipe) rejects with INVALID_OPERATION when
        // the target is a mat4 - the write is silently dropped and the uniform
        // stays at its initial zero matrix (degenerate rays -> flat sky).
        // SetShaderValueMatrix lands correctly; the constructor layout below
        // (intended columns as Matrix4x4 columns) was verified against the
        // on-screen uniform row-dump.
        var mat = new Matrix4x4(
            right.X, up.X, -forward.X, camera.Position.X,
            right.Y, up.Y, -forward.Y, camera.Position.Y,
            right.Z, up.Z, -forward.Z, camera.Position.Z,
            0f, 0f, 0f, 1f);
        Raylib.SetShaderValueMatrix(_shader, _locCamToWorld, mat);
    }
}
