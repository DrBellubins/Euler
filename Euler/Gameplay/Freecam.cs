using System.Numerics;
using Euler.GameEngine;
using Euler.Utils;
using Raylib_cs;

namespace Euler.Gameplay;

public class Freecam
{
    public const float SlowSpeed = 5f;
    public const float FastSpeed = 15f;
    public const float LookSensitivity = 0.0022f;
    public const float MaxPitch = 89.0f;
    public const float RollSpeed = 90f; // degrees per second (Q/E)
    
    public Camera3D Camera = new();

    public float CurrentSpeed;
    public float CurrentSpeedMultiplier = 1.0f;

    /// <summary>
    /// When true the 3D yaw/pitch/position integration is skipped: a
    /// <see cref="WorldCam"/> (4D, wormhole) owns the view, and this camera
    /// is only the derived 3D HUD state (sync it with
    /// <see cref="SyncFromWorldCam"/>). Look/move input is still captured in
    /// <see cref="LookDelta"/> / <see cref="LocalMove"/> / <see cref="MoveSpeed"/>
    /// for the 4D camera to consume.
    /// </summary>
    public bool WorldCamEnabled;

    /// <summary>This frame's look delta in pixels (raw, unscaled).</summary>
    public Vector2 LookDelta;

    /// <summary>This frame's movement keys as (Forward, Left, Up) components, -1/0/+1.</summary>
    public Vector3 LocalMove;

    /// <summary>This frame's movement scale (speed * dt), world units per unit LocalMove.</summary>
    public float MoveSpeed;

    /// <summary>
    /// This frame's roll delta in radians (Q = left/positive, E = right),
    /// Q/E held * RollSpeed * dt. Consumed by the 3D integration below or,
    /// when <see cref="WorldCamEnabled"/>, by the scene's WorldCam.
    /// </summary>
    public float RollDelta;

    private float yaw;
    private float pitch;
    private float roll;

    public void Start()
    {
        Camera.Position = new Vector3(0, 0, -10);
        Camera.Up = Vector3.UnitY;
        Camera.FovY = 90f;
        Camera.Projection = CameraProjection.Perspective;
        
        Input.CursorLocked = true;
    }

    public void Update()
    {
        if (Input.FlyToggle())
            Input.CursorLocked = !Input.CursorLocked;

        LookDelta = Input.LookDelta;

        float dt = Time.DeltaTimeF;

        // This frame's roll (Q/E), radians - consumed by the 3D integration
        // below or, when WorldCamEnabled, by the scene's WorldCam (which owns
        // its own basis, like LocalMove).
        RollDelta = ((Input.RollLeft() ? 1 : 0) - (Input.RollRight() ? 1 : 0))
            * GMath.ToRadians(RollSpeed) * dt;

        if (Input.CursorLocked && !WorldCamEnabled)
        {
            yaw += LookDelta.X * LookSensitivity;
            
            pitch = GMath.Clamp(pitch - LookDelta.Y * LookSensitivity,
                -GMath.ToRadians(MaxPitch), GMath.ToRadians(MaxPitch));

            roll += RollDelta;
        }
        
        Vector3 forward = new(
            MathF.Sin(yaw) * MathF.Cos(pitch),
            MathF.Sin(pitch),
            MathF.Cos(yaw) * MathF.Cos(pitch));

        // Camera up: world up rolled around the view axis (Q/E), so the
        // derived view (and strafing, which follows this up) rotates with it.
        float cr = MathF.Cos(roll);
        float sr = MathF.Sin(roll);
        Vector3 up = Vector3.Normalize(
            Vector3.UnitY * cr
            + Vector3.Cross(forward, Vector3.UnitY) * sr
            + forward * Vector3.Dot(forward, Vector3.UnitY) * (1f - cr));
        Camera.Up = up;

        Vector3 right = Vector3.Normalize(Vector3.Cross(up, forward));

        CurrentSpeedMultiplier += Input.ScrollDelta() * 0.05f;
        CurrentSpeedMultiplier = GMath.Clamp(CurrentSpeedMultiplier, 0.1f, 2.0f);
        
        if (Input.Run())
            CurrentSpeed = FastSpeed * CurrentSpeedMultiplier;
        else
            CurrentSpeed = SlowSpeed * CurrentSpeedMultiplier;

        // Captured in (Forward, Left, Up) basis components - consumed by the
        // 3D integration below or, when WorldCamEnabled, by the scene's
        // WorldCam (which owns its own basis).
        LocalMove = new Vector3(
            (Input.MoveForward() ? 1 : 0) - (Input.MoveBackward() ? 1 : 0),
            (Input.MoveLeft() ? 1 : 0) - (Input.MoveRight() ? 1 : 0),
            (Input.Jump() ? 1 : 0) - (Input.Crouch() ? 1 : 0));
        MoveSpeed = CurrentSpeed * dt;

        if (WorldCamEnabled)
        {
            // The 4D camera integrates look/move (the scene drives it); this
            // 3D camera is only the derived HUD state (SyncFromWorldCam).
            return;
        }

        if (LocalMove.X != 0) Camera.Position += forward * (LocalMove.X * MoveSpeed);
        if (LocalMove.Y != 0) Camera.Position -= right * (LocalMove.Y * MoveSpeed);
        if (LocalMove.Z != 0) Camera.Position += Camera.Up * (LocalMove.Z * MoveSpeed);

        Camera.Target = Camera.Position + forward;
    }

    /// <summary>
    /// Replaces this 3D camera with the derived 3D state of a 4D
    /// <paramref name="worldCam"/> (call after worldCam.Update when
    /// <see cref="WorldCamEnabled"/>).
    /// </summary>
    public void SyncFromWorldCam(WorldCam worldCam)
    {
        Camera = worldCam.ToCamera3D(Camera.FovY);
    }
}