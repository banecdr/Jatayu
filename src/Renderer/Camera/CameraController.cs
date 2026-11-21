/* =================================================================================================
 * FILE: CameraController.cs
 * LOCATION: src/Renderer/Camera/CameraController.cs
 * 
 * WHAT THIS FILE DOES:
 *   Provides smooth, versatile motion controls for a Camera3D instance. Supports both an automated
 *   or interactive Orbit mode (circling around an object of interest) and a First-Person Flycam
 *   mode (WASD spatial movement and looking).
 *
 * HOW IT WORKS INTERNALLY:
 *   1. Orbit Mode:
 *      Maintains spherical coordinates: Yaw (horizontal angle), Pitch (vertical elevation), and
 *      Distance (zoom radius). Calculates the eye position relative to the camera's target:
 *        X = Distance * cos(Pitch) * sin(Yaw)
 *        Y = Distance * sin(Pitch)
 *        Z = Distance * cos(Pitch) * cos(Yaw)
 *
 *   2. Smooth Auto-Orbit:
 *      Can automatically revolve around the target over time with configurable rotational speed,
 *      producing cinematic revolving views of 3D models and scenes.
 *
 *   3. First-Person Free-Look (Flycam):
 *      Provides MoveForward(), MoveRight(), and MoveUp() methods that translate the camera along its
 *      current local coordinate axes, making it easy to wire up keyboard navigation (WASD, Space, Shift).
 *
 * PARAMETERS & PROPERTIES (IN PLAIN ENGLISH):
 *   - Camera: The active Camera3D instance being manipulated.
 *   - Yaw: Horizontal angle in radians around the target.
 *   - Pitch: Vertical elevation angle in radians (clamped to prevent gimbal flips).
 *   - Distance: Radius from the camera target.
 *   - OrbitSpeed: Radians per second for automatic scene rotation (default: 0.5 rad/s).
 *   - MoveSpeed: Units per second for free-fly movement (default: 5.0 units/s).
 *   - AutoOrbitEnabled: Whether the camera automatically spins around the target every frame.
 *
 * USAGE EXAMPLES:
 *   // 1. Create a controller bound to a camera
 *   var controller = new CameraController(camera)
 *   {
 *       Distance = 5.0f,
 *       AutoOrbitEnabled = true,
 *   };
 *
 *   // 2. In game loop:
 *   controller.Update(time.DeltaTime);
 *
 *   // 3. Or handle keyboard inputs:
 *   if (isKeyDownW) controller.MoveForward(time.DeltaTime);
 * ================================================================================================= */

using System;
using System.Numerics;

namespace Jatayu.Renderer.Camera;

/// <summary>
/// Controller for Camera3D providing orbit controls and first-person flying capabilities.
/// </summary>
public sealed class CameraController
{
    private float _pitch = 0.35f;

    /// <summary>
    /// Gets the Camera3D instance managed by this controller.
    /// </summary>
    public Camera3D Camera { get; }

    /// <summary>
    /// Gets or sets the horizontal orbit angle (Yaw) in radians.
    /// </summary>
    public float Yaw { get; set; } = 0.0f;

    /// <summary>
    /// Gets or sets the vertical elevation angle (Pitch) in radians (clamped between -85° and +85°).
    /// </summary>
    public float Pitch
    {
        get => _pitch;
        set => _pitch = Math.Clamp(value, -1.48f, 1.48f);
    }

    /// <summary>
    /// Gets or sets the orbit distance from the camera's target.
    /// </summary>
    public float Distance { get; set; } = 4.5f;

    /// <summary>
    /// Gets or sets the rotation speed for auto-orbit in radians per second.
    /// </summary>
    public float OrbitSpeed { get; set; } = 0.6f;

    /// <summary>
    /// Gets or sets the movement speed for free-fly camera movement in units per second.
    /// </summary>
    public float MoveSpeed { get; set; } = 5.0f;

    /// <summary>
    /// Gets or sets whether automatic orbiting around the target is enabled.
    /// </summary>
    public bool AutoOrbitEnabled { get; set; } = true;

    /// <summary>
    /// Initializes a new CameraController bound to the specified camera.
    /// </summary>
    /// <param name="camera">The Camera3D to control.</param>
    public CameraController(Camera3D camera)
    {
        Camera = camera ?? throw new ArgumentNullException(nameof(camera));
        UpdateCameraOrbit();
    }

    /// <summary>
    /// Updates the camera controller state every frame.
    /// If AutoOrbitEnabled is true, advances Yaw by (OrbitSpeed * deltaTime).
    /// </summary>
    /// <param name="deltaTime">Elapsed time since the last frame in seconds.</param>
    public void Update(float deltaTime)
    {
        if (AutoOrbitEnabled)
        {
            Yaw += OrbitSpeed * deltaTime;
            if (Yaw > MathF.PI * 2f)
                Yaw -= MathF.PI * 2f;
            else if (Yaw < -MathF.PI * 2f)
                Yaw += MathF.PI * 2f;
        }

        UpdateCameraOrbit();
    }

    /// <summary>
    /// Moves the camera along its forward viewing vector.
    /// </summary>
    public void MoveForward(float deltaTime)
    {
        var offset = Camera.Forward * (MoveSpeed * deltaTime);
        Camera.Position += offset;
        Camera.Target += offset;
    }

    /// <summary>
    /// Moves the camera backward against its viewing vector.
    /// </summary>
    public void MoveBackward(float deltaTime)
    {
        var offset = Camera.Forward * (-MoveSpeed * deltaTime);
        Camera.Position += offset;
        Camera.Target += offset;
    }

    /// <summary>
    /// Moves the camera laterally to the right.
    /// </summary>
    public void MoveRight(float deltaTime)
    {
        var offset = Camera.Right * (MoveSpeed * deltaTime);
        Camera.Position += offset;
        Camera.Target += offset;
    }

    /// <summary>
    /// Moves the camera laterally to the left.
    /// </summary>
    public void MoveLeft(float deltaTime)
    {
        var offset = Camera.Right * (-MoveSpeed * deltaTime);
        Camera.Position += offset;
        Camera.Target += offset;
    }

    /// <summary>
    /// Moves the camera vertically upward.
    /// </summary>
    public void MoveUp(float deltaTime)
    {
        var offset = Vector3.UnitY * (MoveSpeed * deltaTime);
        Camera.Position += offset;
        Camera.Target += offset;
    }

    /// <summary>
    /// Moves the camera vertically downward.
    /// </summary>
    public void MoveDown(float deltaTime)
    {
        var offset = Vector3.UnitY * (-MoveSpeed * deltaTime);
        Camera.Position += offset;
        Camera.Target += offset;
    }

    /// <summary>
    /// Adjusts the orbit zoom distance.
    /// </summary>
    /// <param name="zoomDelta">Change in distance (positive zooms out, negative zooms in).</param>
    public void Zoom(float zoomDelta)
    {
        Distance = Math.Clamp(Distance + zoomDelta, 0.5f, 50.0f);
        UpdateCameraOrbit();
    }

    /// <summary>
    /// Recalculates the camera's position in 3D world space based on spherical orbit coordinates.
    /// </summary>
    private void UpdateCameraOrbit()
    {
        Camera.Orbit(Yaw, Pitch, Distance);
    }
}
