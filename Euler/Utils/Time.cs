using Euler.GameEngine;

namespace Euler.Utils;

public class Time
{
    public static double time;
    
    private static double deltaTime;
    public static double DeltaTime
    {
        get
        {
            // Clamp deltaTime to prevent teleportation when lagging
            return !Engine.IsPaused ? GMath.Clamp(deltaTime, 0d, 0.1d) : 0d;
        }
        set{ deltaTime = value; }
    }
    
    public static float DeltaTimeF => (float)DeltaTime;
}