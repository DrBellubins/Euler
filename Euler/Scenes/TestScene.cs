using System.Numerics;
using Euler.GameEngine;
using Euler.Gameplay;
using Euler.Utils;
using Raylib_cs;

namespace Euler.Scenes;

public class TestScene : Scene
{
    private const string TexturePath = "Assets/Textures/checkerboard.png";

    public TestScene(string name) : base(name) { }
    
    private Freecam freecam = new();
    private Raymarcher raymarcher = null!;
    private WorldCam worldCam = null!;
    private WormholePrimitive wormhole;

    // Live-upscale control cycles (see Raymarcher for semantics).
    private static readonly float[] RenderScaleCycle = { 1.0f, 0.75f, 0.67f, 0.5f };
    private static readonly float[] DepthScaleCycle = { 5f, 10f, 25f, 50f, 100f };
    private static readonly float[] NormalExponentCycle = { 1f, 2f, 4f, 8f, 16f, 32f };

    private static readonly string[] DebugModeNames =
    {
        "final (edge-aware)", "nearest", "bilinear", "edge-aware (no sharpen)",
        "distance", "normal", "family id", "edge mask"
    };

    private static float NextInCycle(float current, float[] cycle)
    {
        int idx = 0;
        for (int i = 1; i < cycle.Length; i++)
            if (Math.Abs(cycle[i] - current) < 0.001f) idx = i;
        return cycle[(idx + 1) % cycle.Length];
    }

    public override void Start()
    {
        freecam.Start();
        raymarcher = new Raymarcher(LoadGroundTexture(), LoadGroundTexture());
        
        raymarcher.AddTerrain(new TerrainPrimitive(
            offset: new Vector3(0, -4, 0),
            amplitude: 12.5f,
            frequency: 0.04f,
            octaves: 5,
            uvScale: new Vector2(0.5f, 0.5f),
            sunDirection: new Vector3(0.5f, 1.0f, 0.3f),
            sunIntensity: 0.8f));
        
        wormhole = new WormholePrimitive(new Vector3(0, 4, 6), rmaj: 2.0f, rmin: 1.0f);
        raymarcher.AddWormhole(wormhole);
        
        worldCam = new WorldCam(
            center: wormhole.Center,
            pos:     new Vector4(0f, 5f, -16f, wormhole.Rmin),
            forward: new Vector4(0f, 0f, 1f, 0f),
            left:    new Vector4(-1f, 0f, 0f, 0f),
            up:      new Vector4(0f, 1f, 0f, 0f));
        
        freecam.WorldCamEnabled = true;
    }

    public override void Update()
    {
        freecam.Update();

        // --- Raymarcher upscale controls (live A/B of quality/perf) ---
        if (Input.IsKeyPressed(KeyboardKey.F1))
            raymarcher.RenderScale = NextInCycle(raymarcher.RenderScale, RenderScaleCycle);
        if (Input.IsKeyPressed(KeyboardKey.F2))
            raymarcher.DebugMode = (raymarcher.DebugMode + 1) % DebugModeNames.Length;
        if (Input.IsKeyPressed(KeyboardKey.F4))
            raymarcher.DepthScale = NextInCycle(raymarcher.DepthScale, DepthScaleCycle);
        if (Input.IsKeyPressed(KeyboardKey.F5))
            raymarcher.NormalExponent = NextInCycle(raymarcher.NormalExponent, NormalExponentCycle);
        if (Input.IsKeyPressed(KeyboardKey.F7))
            raymarcher.Sharpen = raymarcher.Sharpen <= 0f ? 0.5f : 0f;
        
        worldCam.Update(
            yawDelta:   -freecam.LookDelta.X * Freecam.LookSensitivity,
            pitchDelta: -freecam.LookDelta.Y * Freecam.LookSensitivity,
            rollDelta:  freecam.RollDelta,
            move:       freecam.LocalMove * freecam.MoveSpeed,
            wormhole);

        freecam.SyncFromWorldCam(worldCam);
    }
    
    private static Texture2D LoadGroundTexture()
    {
        string appDirPath = Path.Combine(AppContext.BaseDirectory, TexturePath);
        string path = Raylib.FileExists(appDirPath) ? appDirPath : TexturePath;

        Texture2D tex = Raylib.FileExists(path)
            ? Raylib.LoadTexture(path)
            : TextureGen.Checkerboard();

        Raylib.SetTextureWrap(tex, TextureWrap.Repeat);
        Raylib.SetTextureFilter(tex, TextureFilter.Bilinear);
        return tex;
    }

    public override void Draw()
    {
        Raylib.BeginDrawing();
        Raylib.ClearBackground(Color.Black);
        
        raymarcher.Draw(freecam.Camera, worldCam);
        
        Debug.DrawDebug($"FPS: {1f / (float)Time.DeltaTimeRaw}");
        Debug.DrawDebug($"Position: {freecam.Camera.Position}");
        Debug.DrawDebug($"Current speed mult: {freecam.CurrentSpeedMultiplier}");

        string universe = worldCam.Universe switch
        {
            1 => "upper universe",
            -1 => "lower universe",
            _ => "throat (between universes)"
        };
        
        Debug.DrawDebug($"Wormhole: {universe}  (w = {worldCam.Pos.W:F2})");

        (int iw, int ih) = raymarcher.InternalSize;
        
        Debug.DrawDebug($"Render: {raymarcher.RenderScale:0.00}x  ({iw}x{ih} internal)  [F1 cycle]");
        Debug.DrawDebug($"View: {DebugModeNames[raymarcher.DebugMode]}  [F2 cycle | F4 depth x{raymarcher.DepthScale:0} | F5 normal ^{raymarcher.NormalExponent:0} | F7 sharpen on/off]");

        Debug.Draw();
        
        Raylib.EndDrawing();
    }
}
