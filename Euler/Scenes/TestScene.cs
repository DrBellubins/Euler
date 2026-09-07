using System.Numerics;
using Euler.GameEngine;
using Raylib_cs;

namespace Euler.Scenes;

public class TestScene : Scene
{
    public TestScene(string name) : base(name) { }

    Vector2 testPos = new Vector2(0, 0);
    
    public override void Start()
    {
        
    }

    public override void Update()
    {
        if (Raylib.IsKeyDown(KeyboardKey.W))
            testPos += new Vector2(0, 1);
    }

    public override void Draw()
    {
        Raylib.BeginDrawing();
        Raylib.ClearBackground(Color.Black);
        
        Raylib.DrawCircleV(testPos, 10, Color.Red);
        
        Raylib.EndDrawing();
    }
}