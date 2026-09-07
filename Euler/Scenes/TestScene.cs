using System.Numerics;
using Euler.GameEngine;
using Raylib_cs;

namespace Euler.Scenes;

public class TestScene : Scene
{
    public TestScene(string name) : base(name) { }
    
    public override void Start()
    {
        
    }

    public override void Update()
    {
        
    }

    public override void Draw()
    {
        Raylib.BeginDrawing();
        Raylib.ClearBackground(Color.Black);
        
        Raylib.EndDrawing();
    }
}