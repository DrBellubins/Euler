using System.Diagnostics;
using Euler.Scenes;
using Euler.Utils;
using Raylib_cs;

namespace Euler.GameEngine;

public class Engine
{
    public const int ScreenWidth = 1600;
    public const int ScreenHeight = 900;
    public const int FPS = 165;
    public const float FrameTimestep = 1.0f / (float)FPS;
    
    public static Engine Instance { get; private set; } = null!;
    
    public static bool IsRunning;
    public static bool IsPaused;

    public static Dictionary<string, Scene> Scenes { get; private set; } = new();
    
    private Stopwatch timer = new();
    private long previousTicks;
    
    public void Initialize()
    {
        // Init
        Instance = this;
        IsRunning = true;
        
        Console.WriteLine("Initializing...");
        
        GMath.Init();
        
        Raylib.InitWindow(ScreenWidth, ScreenHeight, "Euler Game Engine");
        Raylib.SetExitKey(KeyboardKey.Null);
        Raylib.SetTargetFPS(0); // Do our own spin-wait
        
        Console.WriteLine("Window initialized.");
        
        timer.Start();
        
        // --- INITIALIZE SCENES HERE ---
        var testScene = new TestScene("TestScene");
        
        // Start
        foreach (Scene scene in Scenes.Values)
            scene.Start();
        
        Console.WriteLine("Scenes started.");
        
        // Main loop
        Console.WriteLine("Main loop...");
        
        while (IsRunning)
        {
            if (Raylib.WindowShouldClose() || Raylib.IsKeyPressed(KeyboardKey.Q))
                break;
            
            // Cap frame rate with optimized spin-wait
            long targetTicks = (long)(FrameTimestep * (double)Stopwatch.Frequency); // Use double for precision
            long beforeWait = timer.ElapsedTicks;
            long elapsedTicks = beforeWait - previousTicks;
            
            while (elapsedTicks < targetTicks)
            {
                Thread.SpinWait(100); // Brief spin-wait to reduce CPU usage
                elapsedTicks = timer.ElapsedTicks - previousTicks;
            }
        
            long afterWait = timer.ElapsedTicks;
        
            // Calculate DeltaTime after spin-wait to include wait time
            Time.DeltaTime = (afterWait - previousTicks) / (double)Stopwatch.Frequency;
            Time.time += Time.DeltaTime;

            previousTicks = afterWait; // Update to the end of the frame
            
            Input.Update();
            Update();
            Draw();
        }
        
        Console.WriteLine("Exiting...");
        Exit();
    }
    
    public void Update()
    {
        foreach (Scene scene in Scenes.Values)
            scene.Update();
    }

    public void Draw()
    {
        foreach (Scene scene in Scenes.Values)
            scene.Draw();
    }

    public void Exit()
    {
        Environment.Exit(0);
    }
}