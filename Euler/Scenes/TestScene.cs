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
        raymarcher = new Raymarcher(LoadGroundTexture());
        
        raymarcher.AddPlane(new PlanePrimitive(
            origin: new Vector3(0, -2, 0),
            normal: Vector3.UnitY,
            uvScale: new Vector2(1, 1)));
    }

    public override void Update()
    {
        freecam.Update();
    }
    
    private static Texture2D LoadGroundTexture()
    {
        string appDirPath = System.IO.Path.Combine(AppContext.BaseDirectory, TexturePath);
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

        Raylib.EndDrawing();
    }
}
