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

    public override void Start()
    {
        freecam.Start();
        raymarcher = new Raymarcher(LoadGroundTexture(), LoadGroundTexture());

        // The scene's ground is the raymarched FBM terrain (the plane family
        // stays available for future scenes via AddPlane).
        raymarcher.AddTerrain(new TerrainPrimitive(
            offset: new Vector3(0, -4, 0),
            amplitude: 7.5f,   // peaks reach y = -0.5, below the camera's start height (0)
            frequency: 0.04f,
            octaves: 5,
            uvScale: new Vector2(0.5f, 0.5f),
            sunDirection: new Vector3(0.5f, 1.0f, 0.3f),
            sunIntensity: 0.8f));
    }

    public override void Update()
    {
        freecam.Update();
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
        
        raymarcher.Draw(freecam.Camera);

        Raylib.DrawText($"Position: {freecam.Camera.Position}",
            0, 0, 24, Color.White);
        
        Raylib.DrawText($"Current speed mult: {freecam.CurrentSpeedMultiplier}",
            0, 28, 24, Color.White);
        
        Raylib.EndDrawing();
    }
}
