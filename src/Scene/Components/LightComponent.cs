// ==================================================================================================
// Jatayu Engine - Scene & ECS
// Module: LightComponent.cs
// Role: Light source component defining directional, point, and spot light properties.
// ==================================================================================================

using System.Numerics;

namespace Jatayu.Scene.Components;

/// <summary>
/// Types of dynamic lights supported in the scene.
/// </summary>
public enum LightType
{
    Directional,
    Point,
    Spot
}

/// <summary>
/// Component attached to light-emitting entities.
/// </summary>
public struct LightComponent
{
    public LightType Type;
    public Vector3 Color;
    public float Intensity;
    public float Range;
    public float InnerConeAngle;
    public float OuterConeAngle;
    public bool CastsShadows;

    /// <summary>
    /// Creates a directional sunlight component.
    /// </summary>
    public static LightComponent CreateDirectional(Vector3 color, float intensity = 100000.0f, bool castsShadows = true)
    {
        return new LightComponent
        {
            Type = LightType.Directional,
            Color = color,
            Intensity = intensity,
            Range = 0.0f,
            InnerConeAngle = 0.0f,
            OuterConeAngle = 0.0f,
            CastsShadows = castsShadows
        };
    }

    /// <summary>
    /// Creates a point light component with attenuation range.
    /// </summary>
    public static LightComponent CreatePoint(Vector3 color, float intensity = 1000.0f, float range = 25.0f)
    {
        return new LightComponent
        {
            Type = LightType.Point,
            Color = color,
            Intensity = intensity,
            Range = range,
            InnerConeAngle = 0.0f,
            OuterConeAngle = 0.0f,
            CastsShadows = false
        };
    }

    /// <summary>
    /// Creates a spotlight component with inner/outer cone angles.
    /// </summary>
    public static LightComponent CreateSpot(Vector3 color, float intensity = 2000.0f, float range = 35.0f, float innerCone = 25.0f, float outerCone = 45.0f, bool castsShadows = true)
    {
        return new LightComponent
        {
            Type = LightType.Spot,
            Color = color,
            Intensity = intensity,
            Range = range,
            InnerConeAngle = innerCone,
            OuterConeAngle = outerCone,
            CastsShadows = castsShadows
        };
    }
}
