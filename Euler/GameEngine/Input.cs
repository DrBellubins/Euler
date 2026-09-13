using System.Numerics;
using Raylib_cs;

namespace Euler.GameEngine;

/// <summary>
/// Input abstraction over Raylib, with all state captured and managed
/// manually. Nothing relies on raylib's internal frame-based bookkeeping
/// (its <c>IsKeyPressed</c> / wheel accumulators are reset by
/// <c>BeginDrawing</c>/<c>EndDrawing</c> and are therefore frame-phase
/// sensitive) — instead <see cref="Update"/> takes a snapshot of the raw
/// key / mouse / wheel state and edge-detection is derived from the
/// previous snapshot, exactly like the original MonoGame implementation.
///
/// Call <see cref="Update"/> exactly once per frame, before any input is
/// read. All accessors below only ever read the last snapshot.
/// </summary>
public static class Input
{
    // -----------------------------------------------------------------
    // State
    // -----------------------------------------------------------------

    /// <summary>Every defined <see cref="KeyboardKey"/>, for full-state capture.</summary>
    private static readonly KeyboardKey[] _allKeys = Enum.GetValues<KeyboardKey>();

    private static readonly int _maxKey = (int)_allKeys.Max();

    /// <summary>Key state indexed by <c>(int)KeyboardKey</c> (avoids per-frame HashSet allocations).</summary>
    private static bool[] _currentKeys = new bool[_maxKey + 1];
    private static bool[] _prevKeys = new bool[_maxKey + 1];

    /// <summary>A full mouse snapshot: position + all five buttons.</summary>
    private struct MouseSnapshot
    {
        public Vector2 Position;
        public bool Left, Right, Middle, Side, Extra; // Side = XButton1, Extra = XButton2

        public static MouseSnapshot Capture() => new()
        {
            Position = Raylib.GetMousePosition(),
            Left = Raylib.IsMouseButtonDown(MouseButton.Left),
            Right = Raylib.IsMouseButtonDown(MouseButton.Right),
            Middle = Raylib.IsMouseButtonDown(MouseButton.Middle),
            Side = Raylib.IsMouseButtonDown(MouseButton.Side),
            Extra = Raylib.IsMouseButtonDown(MouseButton.Extra)
        };
    }

    private static MouseSnapshot _currentMouse, _prevMouse;

    // Wheel is reported by raylib as a per-frame delta that it clears on its
    // own, so accumulate it into a running total and derive the delta
    // ourselves (same contract as MonoGame's cumulative ScrollWheelValue).
    private static float _wheelTotal, _prevWheel;

    private static Vector2 _windowCenter = new(Engine.ScreenWidth / 2f, Engine.ScreenHeight / 2f);
    private static bool _cursorLocked;

    /// <summary>
    /// Replacement for MonoGame's <c>CursorManager.IsLocked</c>.
    /// When true the system cursor is hidden and pinned to the window
    /// center; <see cref="LookDelta"/> is then the movement from center
    /// (which avoids edge-clipping that raw deltas suffer from).
    /// </summary>
    public static bool CursorLocked
    {
        get => _cursorLocked;
        set
        {
            _cursorLocked = value;
            if (value)
            {
                Raylib.HideCursor();
                Raylib.SetMousePosition(Engine.ScreenWidth / 2, Engine.ScreenHeight / 2);
            }
            else
            {
                Raylib.ShowCursor();
            }
        }
    }

    // -----------------------------------------------------------------
    // Per-frame update
    // -----------------------------------------------------------------

    public static void Update()
    {
        // Keyboard: full snapshot
        (_prevKeys, _currentKeys) = (_currentKeys, _prevKeys); // reuse buffers, no alloc
        foreach (KeyboardKey key in _allKeys)
            _currentKeys[(int)key] = Raylib.IsKeyDown(key);

        // Mouse: full snapshot
        _prevMouse = _currentMouse;
        _currentMouse = MouseSnapshot.Capture();

        // Wheel: consume this frame's delta into the running total
        _prevWheel = _wheelTotal;
        _wheelTotal += Raylib.GetMouseWheelMoveV().Y;

        // Look delta
        if (CursorLocked)
        {
            // Calculate delta from center, then warp back
            Vector2 pos = _currentMouse.Position;
            LookDelta = new Vector2(pos.X - _windowCenter.X, pos.Y - _windowCenter.Y);

            // Warp back to center for next frame
            if (LookDelta != Vector2.Zero)
                Raylib.SetMousePosition(Engine.ScreenWidth / 2, Engine.ScreenHeight / 2);
        }
        else
        {
            // Normal delta (position snapshot vs. previous snapshot)
            LookDelta = _currentMouse.Position - _prevMouse.Position;
        }
    }

    // -----------------------------------------------------------------
    // Movement keys
    // -----------------------------------------------------------------

    public static bool MoveForward() => _currentKeys[(int)KeyboardKey.W];
    public static bool MoveBackward() => _currentKeys[(int)KeyboardKey.S];
    public static bool MoveLeft() => _currentKeys[(int)KeyboardKey.A];
    public static bool MoveRight() => _currentKeys[(int)KeyboardKey.D];

    public static bool Run() => _currentKeys[(int)KeyboardKey.LeftShift];
    public static bool Crouch() => _currentKeys[(int)KeyboardKey.LeftControl];
    public static bool Jump() => _currentKeys[(int)KeyboardKey.Space];

    /*public static bool Jump(bool isCrouching)
    {
        return isCrouching ? IsKeyPressed(KeyboardKey.Space) : _currentKeys[(int)KeyboardKey.Space];
    }*/

    // -----------------------------------------------------------------
    // Mouse input
    // -----------------------------------------------------------------

    public static Vector2 LookDelta { get; set; }
    public static Vector2 CursorPosition() => _currentMouse.Position;

    /// <summary>Vertical wheel movement since the last <see cref="Update"/>, in notches (1.0 per click, may be fractional with smooth-scroll mice).</summary>
    public static float ScrollDelta() => _wheelTotal - _prevWheel;

    /// <summary>
    /// Discards pending look movement so it is not double-applied (e.g. when
    /// the camera is re-aimed externally). Re-captures the mouse so the next
    /// delta is measured from the current position.
    /// </summary>
    public static void FlushLookDelta()
    {
        if (CursorLocked)
            Raylib.SetMousePosition(Engine.ScreenWidth / 2, Engine.ScreenHeight / 2);

        _prevMouse = MouseSnapshot.Capture();
        _currentMouse = _prevMouse;
        LookDelta = Vector2.Zero;
    }

    // Mouse buttons
    public static bool StartedBreaking => IsMouseButtonPressed(ButtonType.Left);
    public static bool Break() => _currentMouse.Left;
    public static bool StartedInteracting => IsMouseButtonPressed(ButtonType.Right);
    public static bool Interact() => _currentMouse.Right;

    // -----------------------------------------------------------------
    // Special keys
    // -----------------------------------------------------------------

    public static bool FlyToggle() => IsKeyPressed(KeyboardKey.Tab);

    public static bool Pause() => IsKeyPressed(KeyboardKey.Escape);

    public static bool OpenChat() => IsKeyPressed(KeyboardKey.T);
    public static bool OpenChatCmd() => IsKeyPressed(KeyboardKey.Slash); // '/' on most layouts
    public static bool SendChat() => IsKeyPressed(KeyboardKey.Enter);

    public static bool Quit() => IsKeyPressed(KeyboardKey.Q);

    // Numeric keys for hotbar selection (useful in Hotbar.cs)
    public static int GetNumberKeyPressed()
    {
        for (int i = 0; i < 9; i++)
        {
            if (IsKeyPressed(KeyboardKey.One + i))
                return i;
        }

        return -1;
    }

    // -----------------------------------------------------------------
    // Edge detection (manual: this snapshot vs. previous snapshot)
    // -----------------------------------------------------------------

    /// <summary>True only on the first frame <paramref name="key"/> is held.</summary>
    public static bool IsKeyPressed(KeyboardKey key)
        => _currentKeys[(int)key] && !_prevKeys[(int)key];

    /// <summary>The key first pressed since the last <see cref="Update"/> (first in enum order if several), or <see cref="KeyboardKey.Null"/> if none.</summary>
    public static KeyboardKey GetKeyPressed()
    {
        foreach (KeyboardKey key in _allKeys)
        {
            if (_currentKeys[(int)key] && !_prevKeys[(int)key])
                return key;
        }

        return KeyboardKey.Null;
    }

    // Utility for single-frame mouse button presses
    private enum ButtonType { Left, Right, Middle, XButton1, XButton2 }

    private static bool IsMouseButtonPressed(ButtonType btn)
    {
        switch (btn)
        {
            case ButtonType.Left: return _currentMouse.Left && !_prevMouse.Left;
            case ButtonType.Right: return _currentMouse.Right && !_prevMouse.Right;
            case ButtonType.Middle: return _currentMouse.Middle && !_prevMouse.Middle;
            case ButtonType.XButton1: return _currentMouse.Side && !_prevMouse.Side;
            case ButtonType.XButton2: return _currentMouse.Extra && !_prevMouse.Extra;
            default: return false;
        }
    }
}
