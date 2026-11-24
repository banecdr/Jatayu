/* =================================================================================================
 * FILE: AudioSource.cs
 * LOCATION: src/Audio/AudioSource.cs
 * 
 * WHAT THIS FILE DOES:
 *   3D spatial positional audio emitter backed by OpenAL AlSource.
 *   Provides 3D spatial attenuation (rolloff, reference distance, max distance), pitch,
 *   gain volume, looping, playback state management, and floating-origin translation.
 * ================================================================================================= */

using System;
using System.Numerics;
using Brutal.Numerics;
using Brutal.OpenAlApi;

namespace Jatayu.Audio;

/// <summary>
/// 3D spatial sound emitter in the game world.
/// </summary>
public sealed class AudioSource : IDisposable
{
    private AlSource _source;
    private SoundClip? _clip;
    private bool _disposed;

    /// <summary>
    /// Gets the native OpenAL source handle.
    /// </summary>
    public AlSource Source => _source;

    /// <summary>
    /// Gets or sets the sound clip currently bound to this emitter.
    /// </summary>
    public SoundClip? Clip
    {
        get => _clip;
        set
        {
            _clip = value;
            if (!_source.IsNull())
            {
                _source.Buffer = value != null ? value.Buffer : AlBuffer.Null;
            }
        }
    }

    private float3 _position = float3.Zero;
    private float3 _velocity = float3.Zero;
    private float _gain = 1.0f;
    private float _pitch = 1.0f;
    private bool _looping = false;
    private float _referenceDistance = 1.0f;
    private float _maxDistance = 100.0f;
    private float _rolloffFactor = 1.0f;

    /// <summary>
    /// Gets or sets the camera-relative position of this audio emitter.
    /// </summary>
    public float3 Position
    {
        get => _position;
        set
        {
            _position = value;
            if (!_source.IsNull())
            {
                try { _source.Position = value; } catch { }
            }
        }
    }

    /// <summary>
    /// Gets or sets the position as System.Numerics.Vector3.
    /// </summary>
    public Vector3 PositionVector3
    {
        get => new(_position.X, _position.Y, _position.Z);
        set => Position = new float3(value.X, value.Y, value.Z);
    }

    /// <summary>
    /// Gets or sets the velocity of this emitter for Doppler frequency shifting.
    /// </summary>
    public float3 Velocity
    {
        get => _velocity;
        set
        {
            _velocity = value;
            if (!_source.IsNull())
            {
                try { _source.Velocity = value; } catch { }
            }
        }
    }

    /// <summary>
    /// Gets or sets volume gain (0.0 = silent, 1.0 = normal).
    /// </summary>
    public float Gain
    {
        get => _gain;
        set
        {
            _gain = MathF.Max(0.0f, value);
            if (!_source.IsNull())
            {
                try { _source.Gain = _gain; } catch { }
            }
        }
    }

    /// <summary>
    /// Gets or sets playback pitch multiplier (1.0 = normal).
    /// </summary>
    public float Pitch
    {
        get => _pitch;
        set
        {
            _pitch = Math.Clamp(value, 0.05f, 32.0f);
            if (!_source.IsNull())
            {
                try { _source.Pitch = _pitch; } catch { }
            }
        }
    }

    /// <summary>
    /// Gets or sets whether this sound repeats automatically when finished.
    /// </summary>
    public bool IsLooping
    {
        get => _looping;
        set
        {
            _looping = value;
            if (!_source.IsNull())
            {
                try { _source.Looping = value; } catch { }
            }
        }
    }

    /// <summary>
    /// Distance under which the sound volume is heard at full intensity without attenuation (default 1.0m).
    /// </summary>
    public float ReferenceDistance
    {
        get => _referenceDistance;
        set
        {
            _referenceDistance = MathF.Max(0.01f, value);
            if (!_source.IsNull())
            {
                try { _source.ReferenceDistance = _referenceDistance; } catch { }
            }
        }
    }

    /// <summary>
    /// Distance at which the sound volume reaches minimum or stops attenuating (default 100.0m).
    /// </summary>
    public float MaxDistance
    {
        get => _maxDistance;
        set
        {
            _maxDistance = MathF.Max(0.1f, value);
            if (!_source.IsNull())
            {
                try { _source.MaxDistance = _maxDistance; } catch { }
            }
        }
    }

    /// <summary>
    /// Rate of distance attenuation roll-off (default 1.0).
    /// </summary>
    public float RolloffFactor
    {
        get => _rolloffFactor;
        set
        {
            _rolloffFactor = MathF.Max(0.0f, value);
            if (!_source.IsNull())
            {
                try { _source.RolloffFactor = _rolloffFactor; } catch { }
            }
        }
    }

    /// <summary>
    /// Gets whether this emitter is currently actively playing sound.
    /// </summary>
    public bool IsPlaying => !_source.IsNull() && _source.State == AlSourceState.Playing;

    /// <summary>
    /// Gets whether this emitter is paused.
    /// </summary>
    public bool IsPaused => !_source.IsNull() && _source.State == AlSourceState.Paused;

    /// <summary>
    /// Constructs a 3D audio emitter.
    /// </summary>
    public AudioSource()
    {
        try
        {
            _source = Al.GenSource();
            if (!_source.IsNull())
            {
                _source.Position = _position;
                _source.Velocity = _velocity;
                _source.Gain = _gain;
                _source.Pitch = _pitch;
                _source.Looping = _looping;
                _source.ReferenceDistance = _referenceDistance;
                _source.MaxDistance = _maxDistance;
                _source.RolloffFactor = _rolloffFactor;
            }
        }
        catch
        {
            _source = default;
        }
    }

    /// <summary>
    /// Starts or resumes playback of the bound clip.
    /// </summary>
    public void Play()
    {
        if (!_source.IsNull() && _clip != null)
        {
            _source.Play();
        }
    }

    /// <summary>
    /// Plays the specified clip once through this emitter.
    /// </summary>
    public void PlayOneShot(SoundClip clip, float volumeScale = 1.0f)
    {
        if (clip == null || _source.IsNull()) return;
        Stop();
        Clip = clip;
        Gain = volumeScale;
        IsLooping = false;
        Play();
    }

    /// <summary>
    /// Pauses playback.
    /// </summary>
    public void Pause()
    {
        if (!_source.IsNull())
        {
            _source.Pause();
        }
    }

    /// <summary>
    /// Stops playback and rewinds to beginning.
    /// </summary>
    public void Stop()
    {
        if (!_source.IsNull())
        {
            _source.Stop();
        }
    }

    /// <summary>
    /// Translates the emitter by the specified offset during a floating-origin rebase (Section 1.3).
    /// </summary>
    public void Translate(in float3 deltaOffset)
    {
        var pos = Position;
        Position = new float3(pos.X + deltaOffset.X, pos.Y + deltaOffset.Y, pos.Z + deltaOffset.Z);
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            if (!_source.IsNull())
            {
                _source.Stop();
                _source.Buffer = AlBuffer.Null;
                _source.Delete();
                _source = AlSource.Null;
            }
            _clip = null;
            _disposed = true;
        }
        GC.SuppressFinalize(this);
    }
}
