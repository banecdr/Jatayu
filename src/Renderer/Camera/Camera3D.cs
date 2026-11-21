/* =================================================================================================
 * FILE: Camera3D.cs
 * LOCATION: src/Renderer/Camera/Camera3D.cs
 * 
 * WHAT THIS FILE DOES:
 *   Implements a fully-featured 3D perspective camera tailored specifically for modern Vulkan 1.3.
 *   Computes View, Projection, and View-Projection matrices that transform 3D world-space coordinates
 *   into Vulkan Normalized Device Coordinates (NDC) for rasterization.
 *
 * HOW IT WORKS INTERNALLY:
 *   1. View Matrix (Eye to World Transformation):
 *      Computed via System.Numerics.Matrix4x4.CreateLookAt(Position, Target, Up).
 *      Calculates the orientation and translation needed to look at any target point in 3D space.
 *
 *   2. Vulkan-Corrected Perspective Projection Matrix:
 *      Vulkan differs from legacy OpenGL in two critical coordinate conventions:
 *        a) Depth Range (Z): Vulkan maps depth from [0.0, 1.0] (DirectX/Vulkan style), NOT [-1.0, 1.0]!
 *           System.Numerics.Matrix4x4.CreatePerspectiveFieldOfView already produces [0.0, 1.0] depth.
 *        b) Y-Axis Orientation: Vulkan's clip space has the Y-axis pointing DOWNWARDS (+1 is bottom,
 *           -1 is top). To maintain standard mathematical 3D world space (where +Y is Up), we negate
 *           the M22 element of the projection matrix (proj.M22 = -proj.M22). This ensures geometry
 *           renders right-side-up and face winding matches front-face culling rules.
 *
 *   3. View-Projection Matrix:
 *      Combines View * Projection so shaders can transform a 3D vertex position to screen clip space
 *      with a single matrix multiplication: gl_Position = uMvp * vec4(inPosition, 1.0).
 *
 *   4. Directional Vectors:
 *      Exposes unit vectors for Forward, Right, and Up, enabling smooth camera flight, FPS navigation,
 *      and raycasting.
 *
 * PARAMETERS & PROPERTIES (IN PLAIN ENGLISH):
 *   - Position: Camera eye location in 3D world space (default: [0, 2, -4]).
 *   - Target: Point in 3D space the camera is focused on (default: [0, 0, 0]).
 *   - Up: World-space up direction vector (default: [0, 1, 0]).
 *   - FovDegrees: Vertical Field of View in degrees (default: 60°).
 *   - AspectRatio: Width divided by height of the render viewport.
 *   - NearPlane: Minimum distance to render geometry (default: 0.1 units).
 *   - FarPlane: Maximum draw distance (default: 1000.0 units).
 *   - ViewMatrix: The current View matrix.
 *   - ProjectionMatrix: The Vulkan-adjusted perspective Projection matrix.
 *   - ViewProjection: The combined View * Projection matrix ready for GPU upload.
 *
 * USAGE EXAMPLES:
 *   // 1. Create a 3D camera looking at the scene origin
 *   var camera = new Camera3D(
 *       position: new Vector3(0f, 2f, -4f),
 *       target: Vector3.Zero,
 *       width: swapchain.Width,
 *       height: swapchain.Height
 *   );
 *
 *   // 2. On window resize:
 *   camera.UpdateAspectRatio(newWidth, newHeight);
 *
 *   // 3. Compute MVP matrix for a rotating object:
 *   Matrix4x4 model = Matrix4x4.CreateRotationY(time);
 *   Matrix4x4 mvp = model * camera.ViewProjection;
 * ================================================================================================= */

using System;
using System.Numerics;

namespace HelloBrutal.Renderer.Camera;

/// <summary>
/// 3D Perspective Camera for Vulkan 1.3 rendering with automatic NDC Y-axis flipping.
/// </summary>
public sealed class Camera3D
{
    private Vector3 _position;
    private Vector3 _target;
    private Vector3 _up;
    private float _fovDegrees;
    private float _aspectRatio;
    private float _nearPlane;
    private float _farPlane;

    private bool _viewDirty = true;
    private bool _projDirty = true;
    private Matrix4x4 _viewMatrix;
    private Matrix4x4 _projectionMatrix;
    private Matrix4x4 _viewProjection;

    /// <summary>
    /// Gets or sets the camera's position in 3D world space.
    /// </summary>
    public Vector3 Position
    {
        get => _position;
        set
        {
            if (_position != value)
            {
                _position = value;
                _viewDirty = true;
            }
        }
    }

    /// <summary>
    /// Gets or sets the target point in 3D world space the camera looks toward.
    /// </summary>
    public Vector3 Target
    {
        get => _target;
        set
        {
            if (_target != value)
            {
                _target = value;
                _viewDirty = true;
            }
        }
    }

    /// <summary>
    /// Gets or sets the camera's world up vector (default: [0, 1, 0]).
    /// </summary>
    public Vector3 Up
    {
        get => _up;
        set
        {
            if (_up != value)
            {
                _up = value;
                _viewDirty = true;
            }
        }
    }

    /// <summary>
    /// Gets or sets the vertical Field of View (FOV) in degrees (default: 60°).
    /// </summary>
    public float FovDegrees
    {
        get => _fovDegrees;
        set
        {
            var clamped = Math.Clamp(value, 10f, 170f);
            if (Math.Abs(_fovDegrees - clamped) > 0.0001f)
            {
                _fovDegrees = clamped;
                _projDirty = true;
            }
        }
    }

    /// <summary>
    /// Gets or sets the viewport aspect ratio (Width / Height).
    /// </summary>
    public float AspectRatio
    {
        get => _aspectRatio;
        set
        {
            var valid = Math.Max(0.001f, value);
            if (Math.Abs(_aspectRatio - valid) > 0.0001f)
            {
                _aspectRatio = valid;
                _projDirty = true;
            }
        }
    }

    /// <summary>
    /// Gets or sets the near clipping plane distance (default: 0.1f).
    /// </summary>
    public float NearPlane
    {
        get => _nearPlane;
        set
        {
            var valid = Math.Max(0.01f, value);
            if (Math.Abs(_nearPlane - valid) > 0.0001f)
            {
                _nearPlane = valid;
                _projDirty = true;
            }
        }
    }

    /// <summary>
    /// Gets or sets the far clipping plane distance (default: 1000.0f).
    /// </summary>
    public float FarPlane
    {
        get => _farPlane;
        set
        {
            var valid = Math.Max(_nearPlane + 0.1f, value);
            if (Math.Abs(_farPlane - valid) > 0.0001f)
            {
                _farPlane = valid;
                _projDirty = true;
            }
        }
    }

    /// <summary>
    /// Gets the normalized forward direction vector pointing from the camera toward the target.
    /// </summary>
    public Vector3 Forward => Vector3.Normalize(_target - _position);

    /// <summary>
    /// Gets the normalized right direction vector perpendicular to forward and up.
    /// </summary>
    public Vector3 Right => Vector3.Normalize(Vector3.Cross(Forward, _up));

    /// <summary>
    /// Gets the orthogonal up vector perpendicular to right and forward.
    /// </summary>
    public Vector3 UpVector => Vector3.Normalize(Vector3.Cross(Right, Forward));

    /// <summary>
    /// Gets the current 3D View matrix.
    /// </summary>
    public Matrix4x4 ViewMatrix
    {
        get
        {
            UpdateMatrices();
            return _viewMatrix;
        }
    }

    /// <summary>
    /// Gets the current Vulkan-adjusted perspective Projection matrix.
    /// </summary>
    public Matrix4x4 ProjectionMatrix
    {
        get
        {
            UpdateMatrices();
            return _projectionMatrix;
        }
    }

    /// <summary>
    /// Gets the combined View-Projection matrix (View * Projection).
    /// </summary>
    public Matrix4x4 ViewProjection
    {
        get
        {
            UpdateMatrices();
            return _viewProjection;
        }
    }

    /// <summary>
    /// Initializes a new 3D perspective camera.
    /// </summary>
    /// <param name="position">Initial camera eye location.</param>
    /// <param name="target">Point to look at.</param>
    /// <param name="width">Viewport width in pixels.</param>
    /// <param name="height">Viewport height in pixels.</param>
    /// <param name="fovDegrees">Field of view in degrees (default: 60°).</param>
    /// <param name="nearPlane">Near clipping plane (default: 0.1f).</param>
    /// <param name="farPlane">Far clipping plane (default: 1000.0f).</param>
    public Camera3D(
        Vector3 position,
        Vector3 target,
        int width,
        int height,
        float fovDegrees = 60f,
        float nearPlane = 0.1f,
        float farPlane = 1000f)
    {
        _position = position;
        _target = target;
        _up = Vector3.UnitY;
        _fovDegrees = fovDegrees;
        _aspectRatio = Math.Max(0.001f, (float)width / Math.Max(1, height));
        _nearPlane = nearPlane;
        _farPlane = farPlane;
    }

    /// <summary>
    /// Updates the camera's aspect ratio based on new viewport dimensions.
    /// </summary>
    /// <param name="width">New width in pixels.</param>
    /// <param name="height">New height in pixels.</param>
    public void UpdateAspectRatio(int width, int height)
    {
        AspectRatio = (float)width / Math.Max(1, height);
    }

    /// <summary>
    /// Smoothly orbits the camera around the target point.
    /// </summary>
    /// <param name="yawRadians">Horizontal orbit angle in radians.</param>
    /// <param name="pitchRadians">Vertical elevation angle in radians.</param>
    /// <param name="distance">Distance from target.</param>
    public void Orbit(float yawRadians, float pitchRadians, float distance)
    {
        var clampedPitch = Math.Clamp(pitchRadians, -1.5f, 1.5f);
        var x = distance * MathF.Cos(clampedPitch) * MathF.Sin(yawRadians);
        var y = distance * MathF.Sin(clampedPitch);
        var z = distance * MathF.Cos(clampedPitch) * MathF.Cos(yawRadians);

        Position = _target + new Vector3(x, y, z);
    }

    /// <summary>
    /// Recalculates cached View, Projection, and View-Projection matrices if any properties changed.
    /// </summary>
    private void UpdateMatrices()
    {
        if (_viewDirty)
        {
            _viewMatrix = Matrix4x4.CreateLookAt(_position, _target, _up);
            _viewDirty = false;
        }

        if (_projDirty)
        {
            var fovRad = _fovDegrees * (MathF.PI / 180f);
            var proj = Matrix4x4.CreatePerspectiveFieldOfView(fovRad, _aspectRatio, _nearPlane, _farPlane);

            // Invert the Y axis for Vulkan clip space coordinates:
            // Vulkan NDC has Y pointing downwards, so negating M22 restores standard 3D Up = +Y.
            proj.M22 = -proj.M22;

            _projectionMatrix = proj;
            _projDirty = false;
        }

        _viewProjection = _viewMatrix * _projectionMatrix;
    }
}
