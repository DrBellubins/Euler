using Raylib_cs;

namespace Euler.Utils;

public static class Debug
{
    public static List<string> DebugTextDraws = new();
    
    public static void DrawDebug(string text)
    {
        DebugTextDraws.Add(text);
    }

    public static void Draw()
    {
        for (int i = 0; i < DebugTextDraws.Count; i++)
            Raylib.DrawText(DebugTextDraws[i], 0, i * 25, 24, Color.White);
        
        DebugTextDraws.Clear();
    }
}