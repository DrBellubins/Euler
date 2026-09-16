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

        Raylib.DrawText($"FPS: {1f / (float)Time.DeltaTimeRaw}",
            0, 0, 24, Color.White);
        
        Raylib.DrawText($"Position: {freecam.Camera.Position}",
            0, 28, 24, Color.White);
        
        Raylib.DrawText($"Current speed mult: {freecam.CurrentSpeedMultiplier}",
            0, 50, 24, Color.White);

        string universe = worldCam.Universe switch
        {
            1 => "upper universe",
            -1 => "lower universe",
            _ => "throat (between universes)"
        };
        
        Raylib.DrawText($"Wormhole: {universe}  (w = {worldCam.Pos.W:F2})",
            0, 56, 24, Color.White);
        
        Raylib.EndDrawing();
    }
}
