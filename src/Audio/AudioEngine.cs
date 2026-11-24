/* =================================================================================================
 * FILE: AudioEngine.cs
 * LOCATION: src/Audio/AudioEngine.cs
 * 
 * WHAT THIS FILE DOES:
 *   Master coordinator for the OpenAL 3D spatial audio system:
 *   - Boots OpenAL device and context with graceful headless / CI fallback.
 *   - Coordinates master volume gain, listener tracking, and active 3D audio emitters.
 *   - Coordinates deterministic floating-origin shifts (D16: listener sync and emitter translations).
 *   - Caches procedural gameplay sound clips (footsteps, impacts, jumping).
 *   - Provides zero-allocation one-shot 3D sound playback via a pooled emitter ring.
 * ================================================================================================= */

using System;
using System.Collections.Generic;
using Brutal.Numerics;
using Brutal.OpenAlApi;
using Jatayu.Core.Events;
using Jatayu.Core.Logging;

namespace Jatayu.Audio;

/// <summary>
/// Master coordinator for 3D positional spatial audio powered by OpenAL.
/// </summary>
public sealed class AudioEngine : IDisposable
{
    private Device _device = default!;
    private Context _context = default!;
    private readonly bool _isHardwareAvailable;

    private readonly AudioListener _listener;
    private readonly List<AudioSource> _activeSources = new(64);
    private readonly AudioSource[] _oneShotPool;
    private int _oneShotIndex;
    private readonly object _lock = new();

    private readonly Core.Events.EventHandler<OriginShiftedEvent> _originShiftHandler;
    private bool _disposed;

    // Shared procedural sound clips
    public SoundClip? SineClip { get; }
    public SoundClip? FootstepClip { get; }
    public SoundClip? ImpactClip { get; }
    public SoundClip? JumpClip { get; }

    /// <summary>
    /// Gets the central audio listener receptor.
    /// </summary>
    public AudioListener Listener => _listener;

    /// <summary>
    /// True if an active OpenAL physical device and context are initialized.
    /// </summary>
    public bool IsHardwareAvailable => _isHardwareAvailable;

    /// <summary>
    /// Master audio volume multiplier (0.0 = silent, 1.0 = normal).
    /// </summary>
    public float MasterVolume
    {
        get => _listener.Gain;
        set => _listener.Gain = value;
    }

    public AudioEngine(string? deviceName = null)
    {
        try
        {
            _device = Alc.OpenDevice(deviceName!);
            if (!_device.IsNull())
            {
                _context = _device.CreateContext(default);
                if (!_context.IsNull())
                {
                    _context.MakeCurrent();
                    _isHardwareAvailable = true;
                    Log.Info($"[AudioEngine] OpenAL initialized successfully on device: {_device.Specifier}");
                }
                else
                {
                    Log.Warning("[AudioEngine] Failed to create OpenAL context. Running in silent fallback mode.");
                }
            }
            else
            {
                Log.Warning("[AudioEngine] No audio device detected. Running in silent fallback mode.");
            }
        }
        catch (Exception ex)
        {
            Log.Warning($"[AudioEngine] OpenAL initialization exception: {ex.Message}. Running in silent fallback mode.");
            _isHardwareAvailable = false;
        }

        _listener = new AudioListener();

        // Initialize shared procedural sound clips
        SineClip = SoundClip.CreateSineWave(440f, 0.5f);
        FootstepClip = SoundClip.CreateFootstepSound();
        ImpactClip = SoundClip.CreateImpactSound();
        JumpClip = SoundClip.CreateJumpSound();

        // Pre-allocate one-shot source pool (16 emitters)
        _oneShotPool = new AudioSource[16];
        for (int i = 0; i < _oneShotPool.Length; i++)
        {
            _oneShotPool[i] = new AudioSource();
        }

        // Subscribe to floating-origin shift events (Section 1.3, D16)
        _originShiftHandler = OnOriginShifted;
        EventBus.Subscribe<OriginShiftedEvent>(_originShiftHandler);
    }

    private void OnOriginShifted(in OriginShiftedEvent e)
    {
        float3 offset = new(-(float)e.DeltaX, -(float)e.DeltaY, -(float)e.DeltaZ);

        lock (_lock)
        {
            _listener.Translate(offset);

            for (int i = 0; i < _activeSources.Count; i++)
            {
                _activeSources[i].Translate(offset);
            }

            for (int i = 0; i < _oneShotPool.Length; i++)
            {
                _oneShotPool[i].Translate(offset);
            }
        }
    }

    /// <summary>
    /// Creates and registers a new 3D audio emitter.
    /// </summary>
    public AudioSource CreateSource()
    {
        if (!_isHardwareAvailable)
            return null!;

        var source = new AudioSource();
        lock (_lock)
        {
            _activeSources.Add(source);
        }
        return source;
    }

    /// <summary>
    /// Removes a managed audio source.
    /// </summary>
    public void RemoveSource(AudioSource source)
    {
        if (source == null) return;
        lock (_lock)
        {
            _activeSources.Remove(source);
        }
        source.Dispose();
    }

    /// <summary>
    /// Plays a sound clip once at the specified 3D world position using a pooled emitter.
    /// Zero heap allocation in steady state.
    /// </summary>
    public void PlayOneShot(SoundClip clip, in float3 position, float volume = 1.0f)
    {
        if (!_isHardwareAvailable || clip == null || _oneShotPool.Length == 0)
            return;

        lock (_lock)
        {
            var source = _oneShotPool[_oneShotIndex];
            _oneShotIndex = (_oneShotIndex + 1) % _oneShotPool.Length;

            source.Position = position;
            source.PlayOneShot(clip, volume);
        }
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            EventBus.Unsubscribe<OriginShiftedEvent>(_originShiftHandler);

            lock (_lock)
            {
                for (int i = 0; i < _activeSources.Count; i++)
                {
                    _activeSources[i].Dispose();
                }
                _activeSources.Clear();

                for (int i = 0; i < _oneShotPool.Length; i++)
                {
                    _oneShotPool[i].Dispose();
                }

                FootstepClip?.Dispose();
                ImpactClip?.Dispose();
                JumpClip?.Dispose();
            }

            if (!_context.IsNull())
            {
                _context.Dispose();
                _context = default!;
            }

            if (!_device.IsNull())
            {
                _device.Close();
                _device.Dispose();
                _device = default!;
            }

            _disposed = true;
        }
        GC.SuppressFinalize(this);
    }
}
