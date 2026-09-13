using System.Numerics;
using Euler.GameEngine;
using Euler.Gameplay;
using Euler.Utils;
using Raylib_cs;

namespace Euler.Scenes;

public class TestScene : Scene
{
    public TestScene(string name) : base(name) { }
    
    private Freecam freecam = new();

    public override void Start()
    {
        freecam.Start();
    }

    public override void Update()
    {
        freecam.Update();
    }

    public override void Draw()
    {
        Raylib.BeginDrawing();
        Raylib.ClearBackground(Color.Black);

        Raylib.BeginMode3D(freecam.Camera);
        Raylib.DrawCubeV(Vector3.Zero, Vector3.One, Color.Orange);
        Raylib.EndMode3D();

        Raylib.EndDrawing();
    }
}
