using System.Numerics;
using Euler.GameEngine;
using Euler.Utils;
using Raylib_cs;

namespace Euler.Scenes;

public class TestScene : Scene
{
    public TestScene(string name) : base(name) { }

    private Camera3D cameraTest;
    
    public override void Start()
    {
        cameraTest = new Camera3D();
        cameraTest.FovY = 90f;
        cameraTest.Position = new Vector3(0, 0, -10);
        cameraTest.Up = Vector3.UnitY;
        cameraTest.Projection = CameraProjection.Perspective;
    }

    public override void Update()
    {
        if (Input.MoveForward())
            cameraTest.Position += Vector3.UnitZ * Time.DeltaTimeF * 10;
        
        if (Input.MoveBackward())
            cameraTest.Position -= Vector3.UnitZ * Time.DeltaTimeF * 10;
        
        if (Input.MoveLeft())
            cameraTest.Position += Vector3.UnitX * Time.DeltaTimeF * 10;
        
        if (Input.MoveRight())
            cameraTest.Position -= Vector3.UnitX * Time.DeltaTimeF * 10;
    }

    public override void Draw()
    {
        Raylib.BeginDrawing();
        Raylib.ClearBackground(Color.Black);
        
        Raylib.BeginMode3D(cameraTest);
        
        Raylib.DrawCubeV(Vector3.Zero, Vector3.One, Color.Orange);
        
        Raylib.EndMode3D();
        
        Raylib.EndDrawing();
    }
}