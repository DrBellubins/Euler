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

        _shader = Raylib.LoadShaderFromMemory(Shaders.RaymarcherVertexSource, Shaders.RaymarcherFragmentSource);
        if (!Raylib.IsShaderValid(_shader))
            throw new InvalidOperationException("Raymarcher shader failed to compile/load.");

        _locResolution = Raylib.GetShaderLocation(_shader, "Resolution");
        _locCamToWorld = Raylib.GetShaderLocation(_shader, "CamToWorld");
        _locFocal      = Raylib.GetShaderLocation(_shader, "Focal");
        _locPlaneCount = Raylib.GetShaderLocation(_shader, "PlaneCount");
        _locPlaneData  = Raylib.GetShaderLocation(_shader, "PlaneData");
        _locPlaneTex   = Raylib.GetShaderLocation(_shader, "PlaneTex");

        // TEMP-DEBUG
        Console.WriteLine($"DEBUG locs: Resolution={_locResolution} CamToWorld={_locCamToWorld} Focal={_locFocal} PlaneCount={_locPlaneCount} PlaneData={_locPlaneData} PlaneTex={_locPlaneTex}");

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
    private int _dbgFrames;

    public void Draw(Camera3D camera)
    {
        for (int i = 0; i < _planeCount; i++)
            _planes[i].WriteInto(_planeData, i);

        if (_dbgFrames++ == 5)
            Raylib.TakeScreenshot("rowdump.png");

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
    /// Builds the camera-to-world matrix (column-major) from the camera's
    /// position/orientation: x = right, y = true-up, z = -forward, and uploads
    /// it. A mat4 uniform is a vec4 array of 4 columns, so it goes through
    /// <c>SetShaderValueV</c> with count 4.
    /// </summary>
    private void SetCameraMatrix(Camera3D camera)
    {
        Vector3 forward = Vector3.Normalize(camera.Target - camera.Position);
        Vector3 right   = Vector3.Normalize(Vector3.Cross(camera.Up, forward));
        Vector3 up      = Vector3.Cross(forward, right);

        Span<float> m = stackalloc float[16]
        {
            right.X,    right.Y,    right.Z,    0f,
            up.X,       up.Y,       up.Z,       0f,
            -forward.X, -forward.Y, -forward.Z, 0f,
            camera.Position.X, camera.Position.Y, camera.Position.Z, 1f
        };

        if (_dbgFrames < 2)
            Console.WriteLine($"DEBUG C# mat col3=[{m[12]},{m[13]},{m[14]},{m[15]}] col0=[{m[0]},{m[1]},{m[2]}]");

        Raylib.SetShaderValueV(_shader, _locCamToWorld, m, ShaderUniformDataType.Vec4, 4);
    }
}
