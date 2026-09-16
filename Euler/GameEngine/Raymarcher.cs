using System.Numerics;
using Euler.Utils;
using Raylib_cs;
// Raylib_cs has its own ShaderType (GL stage enum) - alias the engine's.
using ShaderType = Euler.Utils.ShaderType;

namespace Euler.GameEngine;

/// <summary>
/// Full-screen raymarch renderer for flat, textured plane primitives
/// (<see cref="PlanePrimitive"/>), an infinite FBM terrain
/// (<see cref="TerrainPrimitive"/>) and a traversable 4D wormhole
/// (<see cref="WormholePrimitive"/>). Scene contents are
/// modular shape families - see the includes in <c>Raymarcher.comp</c>.
///
/// Pipeline (compute + edge-aware upscale):
/// <list type="number">
/// <item>A compute shader (<c>Raymarcher.comp</c>) raymarches EVERY pixel of
/// the INTERNAL resolution (<see cref="RenderScale"/> x the window) in
/// parallel - one work item per pixel - and writes 3 uints per pixel into an
/// SSBO (binding point 0): packed RGBA8 color, the hit's total path length
/// (float bits; a large sentinel on a miss) and a 3x8-bit shaded world normal
/// with an 8-bit family ID in the top byte.</item>
/// <item>A display shader (<c>RaymarcherDisplay.fs</c>) draws one full-screen
/// quad at NATIVE window resolution. Each output pixel gathers the 2x2
/// neighborhood of low-res texels from that SSBO and reconstructs its color
/// with edge-aware (distance/normal/family gated) bilinear weights - a
/// single-frame spatial upscaler. <see cref="DebugMode"/> switches the output
/// to analysis views (nearest, bilinear, distance, normal, id, edge mask).</item>
/// </list>
///
/// Both stages are loaded through <see cref="Resource.LoadShader"/>, so the
/// #include pre-processor applies to the compute shader exactly like the
/// graphic stages.
/// </summary>
public class Raymarcher
{
    public const int MaxPlanes = 8;

    // -------------------------------------------------------------------
    // Upscale settings (live-tunable; the semantics live in
    // RaymarcherDisplay.fs)
    // -------------------------------------------------------------------

    /// <summary>
    /// Internal render scale: 1.0 = native window resolution, 0.5 = quarter
    /// the raymarched pixels. The expensive compute pass runs at
    /// <see cref="InternalSize"/>; the display pass always runs at the full
    /// window size.
    /// </summary>
    public float RenderScale { get; set; } = 1.0f;

    /// <summary>
    /// Display output view: 0 final (edge-aware + optional sharpen),
    /// 1 nearest, 2 plain bilinear, 3 edge-aware (no sharpen), 4 distance,
    /// 5 normal, 6 family id, 7 edge-rejection mask.
    /// </summary>
    public int DebugMode { get; set; }

    /// <summary>Depth-weight falloff: exp(-relDelta * DepthScale) with the
    /// relative delta |d - dRef| / max(1, min(d, dRef)).</summary>
    public float DepthScale { get; set; } = 25.0f;

    /// <summary>Normal-weight pow exponent (higher = sharper normal gating).</summary>
    public float NormalExponent { get; set; } = 8.0f;

    /// <summary>Weight for taps of a different (non-sky) family (0 = hard cut).</summary>
    public float CrossIdPenalty { get; set; } = 0.05f;

    /// <summary>Weight for sky<->geometry mixing (0 = hard separation; keep it).</summary>
    public float SkyReject { get; set; }

    /// <summary>Mild unsharp amount applied relative to plain bilinear (0 = off).</summary>
    public float Sharpen { get; set; }

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
    private const uint OutputBufferBinding = 0;

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
    private WormholePrimitive _wormhole;   // at most one (the throat is a per-pixel global effect)
    private readonly float[] _wormholeData = new float[8];
    private bool _hasWormhole;

    // Output SSBO: 3 uints per INTERNAL-resolution pixel (12 bytes) -
    // [0] packed RGBA8 color, [1] hit path length (float bits; DIST_SKY on a
    // miss), [2] 3x8-bit shaded normal | 8-bit family id - bound to
    // OutputBufferBinding for both programs.
    private uint _outputBuffer;
    private int _outputBufferPixels;       // number of pixels the buffer covers

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
    private int _locWormholeCam;

    // Display (upscale) shader uniform locations.
    private int _locDisplayResolution;
    private int _locLowRes;
    private int _locDebugMode;
    private int _locDepthScale;
    private int _locNormalExponent;
    private int _locCrossIdPenalty;
    private int _locSkyReject;
    private int _locSharpen;

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
        _locWormholeCam     = Raylib.GetShaderLocation(_computeShader, "WormholeCam");
        _locDisplayResolution = Raylib.GetShaderLocation(_displayShader, "Resolution");
        _locLowRes = Raylib.GetShaderLocation(_displayShader, "LowRes");
        _locDebugMode = Raylib.GetShaderLocation(_displayShader, "DebugMode");
        _locDepthScale = Raylib.GetShaderLocation(_displayShader, "DepthScale");
        _locNormalExponent = Raylib.GetShaderLocation(_displayShader, "NormalExponent");
        _locCrossIdPenalty = Raylib.GetShaderLocation(_displayShader, "CrossIdPenalty");
        _locSkyReject = Raylib.GetShaderLocation(_displayShader, "SkyReject");
        _locSharpen = Raylib.GetShaderLocation(_displayShader, "Sharpen");

        // A single white pixel is all the full-screen quad needs.
        Image img = Raylib.GenImageColor(1, 1, Color.White);
        _quadTex = Raylib.LoadTextureFromImage(img);
        Raylib.UnloadImage(img);

        CreateOutputBuffer(Engine.ScreenWidth, Engine.ScreenHeight);
    }

    /// <summary>
    /// The INTERNAL resolution <see cref="Draw"/> dispatches the compute pass
    /// at, for the current <see cref="RenderScale"/> (minimum 1x1 so a 0 or
    /// negative scale degrades to the smallest possible buffer instead of
    /// crashing the dispatch).
    /// </summary>
    public (int Width, int Height) InternalSize
    {
        get
        {
            int w = Math.Max(1, (int)MathF.Round(Engine.ScreenWidth * RenderScale));
            int h = Math.Max(1, (int)MathF.Round(Engine.ScreenHeight * RenderScale));
            return (w, h);
        }
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
    /// Sets the scene's wormhole. The 4D throat is a per-pixel global
    /// effect, so there is only ever one - a second call REPLACES the first
    /// (unlike <see cref="AddPlane"/>).
    /// </summary>
    public void AddWormhole(WormholePrimitive wormhole)
    {
        _wormhole = wormhole;
        _hasWormhole = true;
    }

    /// <summary>Draws the raymarched scene (fills the whole window).
    /// <paramref name="camera"/> is the 3D camera (used when no wormhole is active);
    /// <paramref name="worldCam"/> is the 4D-authoritative camera, required when a
    /// wormhole is active (see <see cref="WorldCam"/>).</summary>
    public void Draw(Camera3D camera, WorldCam? worldCam = null)
    {
        int width = Engine.ScreenWidth;
        int height = Engine.ScreenHeight;
        (int iw, int ih) = InternalSize;

        // Recreate the output buffer if the INTERNAL size changed (a new
        // RenderScale, or a window resize).
        if (_outputBufferPixels != iw * ih)
            CreateOutputBuffer(iw, ih);

        for (int i = 0; i < _planeCount; i++)
            _planes[i].WriteInto(_planeData, i);

        if (_hasTerrain)
            _terrain.WriteInto(_terrainData, 0);

        if (_hasWormhole)
        {
            _wormhole.WriteInto(_wormholeData, 0);
            if (worldCam is not null)
                Raylib.SetShaderValueMatrix(_computeShader, _locWormholeCam, worldCam.ToMatrix());
        }

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
        //
        // Resolution is the INTERNAL size: the compute pass raymarches the
        // low-res image, the display pass maps back up to the window.
        Raylib.SetShaderValue(_computeShader, _locResolution,
            new Vector2(iw, ih), ShaderUniformDataType.Vec2);
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
            Raylib.SetShaderValueV(_computeShader, _locWormholeData, _wormholeData, ShaderUniformDataType.Vec4, 2);

        // Grid rounded up to whole workgroups; the shader guards the tail.
        // (INTERNAL size - the whole point of RenderScale.)
        Rlgl.ComputeShaderDispatch(
            (uint)((iw + WorkGroupSize - 1) / WorkGroupSize),
            (uint)((ih + WorkGroupSize - 1) / WorkGroupSize),
            1);

        // The dispatch's SSBO writes must complete before the display
        // fragment stage reads the pixel buffer (OpenGL memory consistency
        // model; raylib's dispatch wrapper is a bare glDispatchCompute).
        RL.MemoryBarrierShaderStorageBuffer();

        // ---------------------------------------------------------------
        // Stage 2 - display: edge-aware upscale of the output SSBO to the
        // full window resolution (see RaymarcherDisplay.fs).
        // ---------------------------------------------------------------
        Raylib.BeginShaderMode(_displayShader);
        Raylib.SetShaderValue(_displayShader, _locDisplayResolution,
            new Vector2(width, height), ShaderUniformDataType.Vec2);
        Raylib.SetShaderValue(_displayShader, _locLowRes,
            new Vector2(iw, ih), ShaderUniformDataType.Vec2);
        Raylib.SetShaderValue(_displayShader, _locDebugMode, DebugMode, ShaderUniformDataType.Int);
        Raylib.SetShaderValue(_displayShader, _locDepthScale, DepthScale, ShaderUniformDataType.Float);
        Raylib.SetShaderValue(_displayShader, _locNormalExponent, NormalExponent, ShaderUniformDataType.Float);
        Raylib.SetShaderValue(_displayShader, _locCrossIdPenalty, CrossIdPenalty, ShaderUniformDataType.Float);
        Raylib.SetShaderValue(_displayShader, _locSkyReject, SkyReject, ShaderUniformDataType.Float);
        Raylib.SetShaderValue(_displayShader, _locSharpen, Sharpen, ShaderUniformDataType.Float);

        // One full-screen quad; the display shader does the actual 2x2
        // neighborhood gather and edge-aware reconstruction.
        Raylib.DrawTextureRec(_quadTex,
            new Rectangle(0, 0, width, height),
            Vector2.Zero, Color.White);

        Raylib.EndShaderMode();
    }

    public void Unload()
    {
        Raylib.UnloadShader(_computeShader);
        Raylib.UnloadShader(_displayShader);
        if (_outputBuffer != 0)
        {
            Rlgl.UnloadShaderBuffer(_outputBuffer);
            _outputBuffer = 0;
        }
        Raylib.UnloadTexture(_planeTex);
        Raylib.UnloadTexture(_terrainTex);
        Raylib.UnloadTexture(_quadTex);
    }

    /// <summary>
    /// (Re)creates the output SSBO for a <paramref name="width"/>x<paramref name="height"/>
    /// INTERNAL resolution and binds it to <see cref="OutputBufferBinding"/>.
    /// Binding points are context-global GL state, so the single bind makes
    /// the buffer visible to BOTH the compute and the display program without
    /// any per-program uniform.
    /// </summary>
    private void CreateOutputBuffer(int width, int height)
    {
        if (_outputBuffer != 0)
        {
            Rlgl.UnloadShaderBuffer(_outputBuffer);
            _outputBuffer = 0;
        }

        // 3 uints (12 bytes) per pixel: color / distance / normal+id;
        // raylib zero-clears the buffer.
        _outputBuffer = RL.LoadShaderBuffer(width * height * 12);
        Rlgl.BindShaderBuffer(_outputBuffer, OutputBufferBinding);
        _outputBufferPixels = width * height;
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
