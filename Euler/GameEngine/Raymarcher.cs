using System.Numerics;
using Euler.Utils;
using Raylib_cs;
// Raylib_cs has its own ShaderType (GL stage enum) - alias the engine's.
using ShaderType = Euler.Utils.ShaderType;

namespace Euler.GameEngine;

/// <summary>
/// Full-screen raymarch renderer for flat, textured plane primitives
/// (<see cref="PlanePrimitive"/>), an infinite FBM terrain
/// (<see cref="TerrainPrimitive"/>) and a wormhole with a light-bending
/// gravitational field (<see cref="WormholePrimitive"]). Scene contents are
/// modular shape families - see the includes in <c>Raymarcher.comp</c>.
///
/// Pipeline (compute):
/// <list type="number">
/// <item>A compute shader (<c>Raymarcher.comp</c>) raymarches EVERY pixel in
/// parallel - one work item per pixel - and writes one packed RGBA8 uint per
/// pixel into an SSBO (binding point 0).</item>
/// <item>A display shader (<c>RaymarcherDisplay.fs</c>) draws one full-screen
/// quad that reads its pixel back from that SSBO and writes it to the screen.</item>
/// </list>
///
/// Both stages are loaded through <see cref="Resource.LoadShader"/>, so the
/// #include pre-processor applies to the compute shader exactly like the
/// graphic stages.
/// </summary>
public class Raymarcher
{
    public const int MaxPlanes = 8;

    private const string ComputeShaderPath = "Assets/Shaders/Raymarcher.comp";
    private const string DisplayShaderPath = "Assets/Shaders/RaymarcherDisplay.fs";

    /// <summary>
    /// Compute workgroup edge size (16x16 = 256 invocations). Must match the
    /// layout(local_size_x/y/z) qualifier in Raymarcher.comp.
    /// </summary>
    private const int WorkGroupSize = 16;

    /// <summary>
    /// Texture unit the compute stage samples <see cref="Raymarcher._planeTex"/>
    /// from. Chosen as 0: the raylib batch system always rebinds unit 0 to its
    /// own textures at draw time, so our pre-dispatch binding can never leak
    /// into a graphics draw.
    /// </summary>
    private const int PlaneTexUnit = 0;

    /// <summary>
    /// Texture unit the compute stage samples the terrain texture from. Unit 0
    /// belongs to the plane family (<see cref="PlaneTexUnit"/>); the same
    /// pre-dispatch binding rules apply (see the notes there and in Draw).
    /// </summary>
    private const int TerrainTexUnit = 1;

    /// <summary>SSBO binding point shared by the compute and display stages.</summary>
    private const uint PixelBufferBinding = 0;

    private readonly Shader _computeShader;
    private readonly Shader _displayShader;
    private readonly Texture2D _planeTex;
    private readonly Texture2D _terrainTex;
    private readonly Texture2D _quadTex;   // 1x1 white, only exists to fill the screen
    private readonly PlanePrimitive[] _planes = new PlanePrimitive[MaxPlanes];
    private readonly float[] _planeData = new float[MaxPlanes * 12];
    private int _planeCount;
    private TerrainPrimitive _terrain;     // at most one (a heightfield spans all XZ)
    private readonly float[] _terrainData = new float[16];
    private bool _hasTerrain;
    private WormholePrimitive _wormhole;   // at most one (the field is a per-pixel global effect)
    private readonly float[] _wormholeData = new float[4];
    private bool _hasWormhole;

    // Output SSBO: one packed RGBA8 uint per pixel (W*H*4 bytes), bound to
    // PixelBufferBinding for both programs.
    private uint _pixelBuffer;
    private int _pixelBufferCount;         // number of pixels the buffer covers

    // Compute shader uniform locations.
    private int _locResolution;
    private int _locCamToWorld;
    private int _locFocal;
    private int _locPlaneCount;
    private int _locPlaneData;
    private int _locPlaneTex;
    private int _locTerrainEnabled;
    private int _locTerrainData;
    private int _locTerrainTex;
    private int _locWormholeEnabled;
    private int _locWormholeData;

    // Display shader uniform locations.
    private int _locDisplayResolution;

    public Raymarcher(Texture2D planeTexture, Texture2D terrainTexture)
    {
        _planeTex = planeTexture;
        _terrainTex = terrainTexture;

        // Both stages go through Resource, so the #include pre-processor runs
        // on the compute source too (before it reaches the GL compiler).
        _computeShader = Resource.LoadShader(ComputeShaderPath, ShaderType.Compute);
        _displayShader = Resource.LoadShader(DisplayShaderPath, ShaderType.Pixel);

        _locResolution = Raylib.GetShaderLocation(_computeShader, "Resolution");
        _locCamToWorld = Raylib.GetShaderLocation(_computeShader, "CamToWorld");
        _locFocal      = Raylib.GetShaderLocation(_computeShader, "Focal");
        _locPlaneCount = Raylib.GetShaderLocation(_computeShader, "PlaneCount");
        _locPlaneData  = Raylib.GetShaderLocation(_computeShader, "PlaneData");
        _locPlaneTex   = Raylib.GetShaderLocation(_computeShader, "PlaneTex");
        _locTerrainEnabled = Raylib.GetShaderLocation(_computeShader, "TerrainEnabled");
        _locTerrainData    = Raylib.GetShaderLocation(_computeShader, "TerrainData");
        _locTerrainTex     = Raylib.GetShaderLocation(_computeShader, "TerrainTex");
        _locWormholeEnabled = Raylib.GetShaderLocation(_computeShader, "WormholeEnabled");
        _locWormholeData    = Raylib.GetShaderLocation(_computeShader, "WormholeData");
        _locDisplayResolution = Raylib.GetShaderLocation(_displayShader, "Resolution");

        // A single white pixel is all the full-screen quad needs.
        Image img = Raylib.GenImageColor(1, 1, Color.White);
        _quadTex = Raylib.LoadTextureFromImage(img);
        Raylib.UnloadImage(img);

        CreatePixelBuffer(Engine.ScreenWidth, Engine.ScreenHeight);
    }

    /// <summary>Adds a plane (up to <see cref="MaxPlanes"/>).</summary>
    public void AddPlane(PlanePrimitive plane)
    {
        if (_planeCount >= MaxPlanes)
            throw new InvalidOperationException($"Raymarcher supports at most {MaxPlanes} planes.");
        _planes[_planeCount++] = plane;
    }

    /// <summary>
    /// Sets the scene's terrain. A heightfield occupies the whole XZ plane, so
    /// there is only ever one - a second call REPLACES the first (unlike
    /// <see cref="AddPlane"/>).
    /// </summary>
    public void AddTerrain(TerrainPrimitive terrain)
    {
        _terrain = terrain;
        _hasTerrain = true;
    }

    /// <summary>
    /// Sets the scene's wormhole. The gravitational field is a per-pixel
    /// global effect, so there is only ever one - a second call REPLACES the
    /// first (unlike <see cref="AddPlane"/>).
    /// </summary>
    public void AddWormhole(WormholePrimitive wormhole)
    {
        _wormhole = wormhole;
        _hasWormhole = true;
    }

    /// <summary>Draws the raymarched scene for <paramref name="camera"/> (fills the whole window).</summary>
    public void Draw(Camera3D camera)
    {
        int width = Engine.ScreenWidth;
        int height = Engine.ScreenHeight;

        // Recreate the pixel buffer if the framebuffer size changed.
        if (_pixelBufferCount != width * height)
            CreatePixelBuffer(width, height);

        for (int i = 0; i < _planeCount; i++)
            _planes[i].WriteInto(_planeData, i);

        if (_hasTerrain)
            _terrain.WriteInto(_terrainData, 0);

        if (_hasWormhole)
            _wormhole.WriteInto(_wormholeData, 0);

        // ---------------------------------------------------------------
        // Stage 1 - compute: raymarch every pixel into the pixel SSBO.
        // ---------------------------------------------------------------

        // PlaneTex must be bound to a KNOWN unit before the dispatch.
        // Raylib.SetShaderValueTexture is deliberately NOT used here: it only
        // records the texture in the batch's active-texture table and the real
        // glBindTexture happens at batch-draw time - AFTER the dispatch - so
        // the compute stage would sample a stale/other texture. Unit 0 is
        // safe: the batch always rebinds unit 0 to its own textures when it
        // flushes (see rlDrawRenderBatch), so this binding can't leak into a
        // graphics draw.
        Rlgl.ActiveTextureSlot(PlaneTexUnit);
        Rlgl.EnableTexture(_planeTex.Id);

        // The terrain family owns unit 1 (same pre-dispatch binding rule).
        Rlgl.ActiveTextureSlot(TerrainTexUnit);
        Rlgl.EnableTexture(_terrainTex.Id);

        // SetShaderValue* enables the compute program before uploading (the
        // uniform state lives on the program), so the plain high-level API
        // works for compute shaders too.
        Raylib.SetShaderValue(_computeShader, _locResolution,
            new Vector2(width, height), ShaderUniformDataType.Vec2);
        SetCameraMatrix(camera);
        Raylib.SetShaderValue(_computeShader, _locFocal,
            1f / MathF.Tan(GMath.ToRadians(camera.FovY) * 0.5f), ShaderUniformDataType.Float);
        Raylib.SetShaderValue(_computeShader, _locPlaneCount, _planeCount, ShaderUniformDataType.Int);
        if (_planeCount > 0)
            Raylib.SetShaderValueV(_computeShader, _locPlaneData, _planeData, ShaderUniformDataType.Vec4, _planeCount * 3);
        Raylib.SetShaderValue(_computeShader, _locPlaneTex, PlaneTexUnit, ShaderUniformDataType.Int);

        Raylib.SetShaderValue(_computeShader, _locTerrainEnabled, _hasTerrain ? 1 : 0, ShaderUniformDataType.Int);
        if (_hasTerrain)
            Raylib.SetShaderValueV(_computeShader, _locTerrainData, _terrainData, ShaderUniformDataType.Vec4, 4);
        Raylib.SetShaderValue(_computeShader, _locTerrainTex, TerrainTexUnit, ShaderUniformDataType.Int);

        Raylib.SetShaderValue(_computeShader, _locWormholeEnabled, _hasWormhole ? 1 : 0, ShaderUniformDataType.Int);
        if (_hasWormhole)
            Raylib.SetShaderValueV(_computeShader, _locWormholeData, _wormholeData, ShaderUniformDataType.Vec4, 1);

        // Grid rounded up to whole workgroups; the shader guards the tail.
        Rlgl.ComputeShaderDispatch(
            (uint)((width + WorkGroupSize - 1) / WorkGroupSize),
            (uint)((height + WorkGroupSize - 1) / WorkGroupSize),
            1);

        // The dispatch's SSBO writes must complete before the display
        // fragment stage reads the pixel buffer (OpenGL memory consistency
        // model; raylib's dispatch wrapper is a bare glDispatchCompute).
        RL.MemoryBarrierShaderStorageBuffer();

        // ---------------------------------------------------------------
        // Stage 2 - display: blit the pixel SSBO to the screen.
        // ---------------------------------------------------------------
        Raylib.BeginShaderMode(_displayShader);
        Raylib.SetShaderValue(_displayShader, _locDisplayResolution,
            new Vector2(width, height), ShaderUniformDataType.Vec2);

        // One full-screen quad; the display shader does the actual pixel read.
        Raylib.DrawTextureRec(_quadTex,
            new Rectangle(0, 0, width, height),
            Vector2.Zero, Color.White);

        Raylib.EndShaderMode();
    }

    public void Unload()
    {
        Raylib.UnloadShader(_computeShader);
        Raylib.UnloadShader(_displayShader);
        if (_pixelBuffer != 0)
        {
            Rlgl.UnloadShaderBuffer(_pixelBuffer);
            _pixelBuffer = 0;
        }
        Raylib.UnloadTexture(_planeTex);
        Raylib.UnloadTexture(_terrainTex);
        Raylib.UnloadTexture(_quadTex);
    }

    /// <summary>
    /// (Re)creates the pixel SSBO for a <paramref name="width"/>x<paramref name="height"/>
    /// framebuffer and binds it to <see cref="PixelBufferBinding"/>. Binding
    /// points are context-global GL state, so the single bind makes the buffer
    /// visible to BOTH the compute and the display program without any
    /// per-program uniform.
    /// </summary>
    private void CreatePixelBuffer(int width, int height)
    {
        if (_pixelBuffer != 0)
        {
            Rlgl.UnloadShaderBuffer(_pixelBuffer);
            _pixelBuffer = 0;
        }

        // One uint (packed RGBA8) per pixel; raylib zero-clears the buffer.
        _pixelBuffer = RL.LoadShaderBuffer(width * height * 4);
        Rlgl.BindShaderBuffer(_pixelBuffer, PixelBufferBinding);
        _pixelBufferCount = width * height;
    }

    /// <summary>
    /// Builds the camera-to-world matrix from the camera's
    /// position/orientation (x = right, y = true-up, z = -forward) and uploads
    /// it to the compute stage.
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
        Raylib.SetShaderValueMatrix(_computeShader, _locCamToWorld, mat);
    }
}
