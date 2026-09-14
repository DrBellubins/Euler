using System.Numerics;
using Euler.GameEngine;
using Euler.Utils;
using Raylib_cs;

namespace Euler.Gameplay;

public class Freecam
{
    public Camera3D Camera = new();

    // Free-cam orientation, in radians. Yaw = turn left/right, pitch = look up/down.
    private float _yaw;
    private float _pitch;

    private const float MoveSpeed = 10f;        // world units per second
    private const float LookSensitivity = 0.0022f; // radians per pixel
    private const float MaxPitch = 1.55f;       // ~89 deg, avoids gimbal flip at the poles

    public void Start()
    {
        Camera.Position = new Vector3(0, 0, -10);
        Camera.Up = Vector3.UnitY;
        Camera.FovY = 90f;
        Camera.Projection = CameraProjection.Perspective;

        // Start in mouse-look (free-cam) mode. Tab toggles it.
        Input.CursorLocked = true;
    }

    public void Update()
    {
        // Tab toggles the captured cursor / mouse-look on and off.
        if (Input.FlyToggle())
            Input.CursorLocked = !Input.CursorLocked;

        // Mouse look — only while the cursor is captured.
        if (Input.CursorLocked)
        {
            Vector2 look = Input.LookDelta;
            _yaw   += look.X * LookSensitivity;
            _pitch  = GMath.Clamp(_pitch - look.Y * LookSensitivity, -MaxPitch, MaxPitch);
        }

        // Derive a normalized forward vector from yaw/pitch.
        // At yaw=0, pitch=0 this is +Z, matching the original "look at the cube from -10".
        Vector3 forward = new(
            MathF.Sin(_yaw) * MathF.Cos(_pitch),
            MathF.Sin(_pitch),
            MathF.Cos(_yaw) * MathF.Cos(_pitch));

        // Camera's right vector (perpendicular to forward, in the horizontal plane).
        Vector3 right = Vector3.Normalize(Vector3.Cross(Camera.Up, forward));

        float dt = Time.DeltaTimeF;

        // Move along the CAMERA's own axes, not the world axes.
        if (Input.MoveForward())  Camera.Position += forward * (MoveSpeed * dt);
        if (Input.MoveBackward()) Camera.Position -= forward * (MoveSpeed * dt);
        if (Input.MoveLeft())     Camera.Position -= right * (MoveSpeed * dt);
        if (Input.MoveRight())    Camera.Position += right * (MoveSpeed * dt);

        // Optional vertical fly (Space = up, Ctrl = down).
        if (Input.Jump())   Camera.Position += Camera.Up * (MoveSpeed * dt);
        if (Input.Crouch()) Camera.Position -= Camera.Up * (MoveSpeed * dt);

        // THE key line: keep the camera aimed along its own forward direction
        // instead of letting the target stay pinned to the origin.
        Camera.Target = Camera.Position + forward;
    }
}