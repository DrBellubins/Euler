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
    
    public Camera3D Camera = new();
    
    private float yaw;
    private float pitch;

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

        if (Input.CursorLocked)
        {
            Vector2 look = Input.LookDelta;
            
            yaw += look.X * LookSensitivity;
            
            pitch = GMath.Clamp(pitch - look.Y * LookSensitivity,
                -GMath.ToRadians(MaxPitch), GMath.ToRadians(MaxPitch));
        }

        Vector3 forward = new(
            MathF.Sin(yaw) * MathF.Cos(pitch),
            MathF.Sin(pitch),
            MathF.Cos(yaw) * MathF.Cos(pitch));

        Vector3 right = Vector3.Normalize(Vector3.Cross(Camera.Up, forward));

        float dt = Time.DeltaTimeF;

        float currentSpeed;

        if (Input.Run())
            currentSpeed = FastSpeed;
        else
            currentSpeed = SlowSpeed;
        
        if (Input.MoveForward()) Camera.Position += forward * (currentSpeed * dt);
        if (Input.MoveBackward()) Camera.Position -= forward * (currentSpeed * dt);
        if (Input.MoveLeft()) Camera.Position -= right * (currentSpeed * dt);
        if (Input.MoveRight()) Camera.Position += right * (currentSpeed * dt);

        if (Input.Jump()) Camera.Position += Camera.Up * (currentSpeed * dt);
        if (Input.Crouch()) Camera.Position -= Camera.Up * (currentSpeed * dt);

        Camera.Target = Camera.Position + forward;
    }
}