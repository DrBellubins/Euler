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
    private Raymarcher raymarcher = null!; // assigned in Start()

    public override void Start()
    {
        freecam.Start();

        // The tiled ground texture: the bundled checkerboard, or a procedural
        // fallback if the asset is missing. REPEAT wrap is what makes UV
        // tiling possible (coordinates outside [0,1] wrap instead of clamping).
        raymarcher = new Raymarcher(LoadGroundTexture());

        // A floor 2 world units below spawn, tiled 1 checkerboard tile per
        // world unit in both axes. Change UvScale to re-tile: e.g.
        // new Vector2(4, 2) = one tile every 0.25 x 0.5 world units.
        raymarcher.AddPlane(new PlanePrimitive(
            origin: new Vector3(0, -2, 0),
            normal: Vector3.UnitY,
            uvScale: new Vector2(1, 1)));
    }

    public override void Update()
    {
        freecam.Update();
    }

    /// <summary>
    /// Loads the ground texture from the bundled asset, resolving the path
    /// against both the app directory (works with <c>dotnet run</c> and
    /// published builds) and the current working directory (Rider sets CWD
    /// to the output dir). Falls back to a procedural checkerboard.
    /// </summary>
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

        // The raymarcher renders full-screen (tiled plane + sky + fog).
        // NOTE: the old Mode3D cube would now be hidden behind the opaque
        // raymarch pass - re-add it as a raymarched SDF when the engine grows
        // more geometry:
        //   Raylib.BeginMode3D(freecam.Camera);
        //   Raylib.DrawCubeV(Vector3.Zero, Vector3.One, Color.Orange);
        //   Raylib.EndMode3D();
        raymarcher.Draw(freecam.Camera);

        Raylib.EndDrawing();
    }
}
