/* =================================================================================================
 * FILE: SoundClip.cs
 * LOCATION: src/Audio/SoundClip.cs
 * 
 * WHAT THIS FILE DOES:
 *   Shared in-memory decoded audio buffer wrapping OpenAL AlBuffer.
 *   Provides PCM data uploads, duration/channel metrics, and procedural sound synthesis
 *   generators (sine wave, footsteps, impact thuds, jump whooshes, ambient noise).
 * ================================================================================================= */

using System;
using System.Runtime.InteropServices;
using Brutal.OpenAlApi;

namespace Jatayu.Audio;

/// <summary>
/// Audio buffer container holding decoded PCM audio samples in OpenAL memory.
/// </summary>
public sealed class SoundClip : IDisposable
{
    private AlBuffer _buffer;
    private readonly float _durationSeconds;
    private readonly int _sampleRate;
    private readonly int _channels;
    private bool _disposed;

    /// <summary>
    /// Gets the native OpenAL buffer handle.
    /// </summary>
    public AlBuffer Buffer => _buffer;

    /// <summary>
    /// Duration of the sound clip in seconds.
    /// </summary>
    public float DurationSeconds => _durationSeconds;

    /// <summary>
    /// Audio sample rate in Hertz (e.g. 44100 Hz).
    /// </summary>
    public int SampleRate => _sampleRate;

    /// <summary>
    /// Number of audio channels (1 for Mono, 2 for Stereo).
    /// 3D spatial sound emitters require Mono audio.
    /// </summary>
    public int Channels => _channels;

    /// <summary>
    /// True if the buffer is allocated and valid.
    /// </summary>
    public bool IsValid => !_buffer.IsNull();

    /// <summary>
    /// Constructs a sound clip from raw 16-bit signed PCM samples.
    /// </summary>
    public SoundClip(ReadOnlySpan<short> pcmSamples, int sampleRate = 44100, int channels = 1)
    {
        _sampleRate = sampleRate;
        _channels = channels;
        _durationSeconds = (float)pcmSamples.Length / (sampleRate * channels);

        try
        {
            _buffer = Al.GenBuffer();
            var format = channels == 1 ? AlFormat.Mono16 : AlFormat.Stereo16;
            ReadOnlySpan<byte> byteData = MemoryMarshal.AsBytes(pcmSamples);
            _buffer.SetData(format, byteData, sampleRate);
        }
        catch
        {
            _buffer = default;
        }
    }

    /// <summary>
    /// Constructs a sound clip from raw byte PCM data with a specified AlFormat.
    /// </summary>
    public SoundClip(ReadOnlySpan<byte> pcmData, AlFormat format, int sampleRate)
    {
        _sampleRate = sampleRate;
        _channels = (format == AlFormat.Mono8 || format == AlFormat.Mono16) ? 1 : 2;
        int bytesPerSample = (format == AlFormat.Mono16 || format == AlFormat.Stereo16) ? 2 : 1;
        _durationSeconds = (float)pcmData.Length / (sampleRate * _channels * bytesPerSample);

        try
        {
            _buffer = Al.GenBuffer();
            _buffer.SetData(format, pcmData, sampleRate);
        }
        catch
        {
            _buffer = default;
        }
    }

    /// <summary>
    /// Loads a standard PCM .wav audio file into an OpenAL sound clip.
    /// If the audio file has 2 channels (stereo), it is automatically downmixed to mono
    /// so that OpenAL can apply full 3D spatial attenuation and directional panning.
    /// </summary>
    public static SoundClip FromFile(string filePath)
    {
        if (!System.IO.File.Exists(filePath))
            throw new System.IO.FileNotFoundException($"Audio file not found: {filePath}", filePath);

        using var stream = System.IO.File.OpenRead(filePath);
        using var reader = new System.IO.BinaryReader(stream);

        // RIFF header
        string riff = new string(reader.ReadChars(4));
        if (riff != "RIFF")
            throw new System.IO.InvalidDataException($"Invalid WAV file (missing RIFF header): {filePath}");

        reader.ReadInt32(); // total size

        string wave = new string(reader.ReadChars(4));
        if (wave != "WAVE")
            throw new System.IO.InvalidDataException($"Invalid WAV file (missing WAVE format): {filePath}");

        short formatTag = 0;
        short channels = 0;
        int sampleRate = 0;
        short bitsPerSample = 0;
        byte[]? pcmData = null;

        while (reader.BaseStream.Position < reader.BaseStream.Length)
        {
            string chunkId = new string(reader.ReadChars(4));
            int chunkSize = reader.ReadInt32();

            if (chunkId == "fmt ")
            {
                formatTag = reader.ReadInt16();
                channels = reader.ReadInt16();
                sampleRate = reader.ReadInt32();
                reader.ReadInt32(); // byte rate
                reader.ReadInt16(); // block align
                bitsPerSample = reader.ReadInt16();

                // Skip any extra format bytes
                if (chunkSize > 16)
                    reader.BaseStream.Seek(chunkSize - 16, System.IO.SeekOrigin.Current);
            }
            else if (chunkId == "data")
            {
                pcmData = reader.ReadBytes(chunkSize);
                break; // Got audio data
            }
            else
            {
                // Skip unknown chunk (e.g. LIST, INFO, JUNK)
                reader.BaseStream.Seek(chunkSize, System.IO.SeekOrigin.Current);
            }
        }

        if (pcmData == null || pcmData.Length == 0)
            throw new System.IO.InvalidDataException($"No audio PCM data found in WAV file: {filePath}");

        // Downmix 16-bit stereo to mono for OpenAL 3D spatial audio
        if (channels == 2 && bitsPerSample == 16)
        {
            int sampleCount = pcmData.Length / 4; // 2 channels * 2 bytes = 4 bytes per stereo sample
            var monoSamples = new short[sampleCount];
            var srcSpan = MemoryMarshal.Cast<byte, short>(pcmData.AsSpan());

            for (int i = 0; i < sampleCount; i++)
            {
                int left = srcSpan[i * 2];
                int right = srcSpan[i * 2 + 1];
                monoSamples[i] = (short)((left + right) / 2);
            }

            return new SoundClip(monoSamples, sampleRate, channels: 1);
        }

        var alFormat = channels switch
        {
            1 => bitsPerSample == 8 ? AlFormat.Mono8 : AlFormat.Mono16,
            _ => bitsPerSample == 8 ? AlFormat.Stereo8 : AlFormat.Stereo16,
        };

        return new SoundClip(pcmData, alFormat, sampleRate);
    }

    /// <summary>
    /// Generates a procedural sine wave tone (useful for tests and audio diagnostics).
    /// </summary>
    public static SoundClip CreateSineWave(float frequency = 440.0f, float durationSeconds = 1.0f, int sampleRate = 44100)
    {
        int sampleCount = (int)(sampleRate * durationSeconds);
        var samples = new short[sampleCount];
        float phaseInc = (2.0f * MathF.PI * frequency) / sampleRate;
        float phase = 0.0f;

        for (int i = 0; i < sampleCount; i++)
        {
            // Apply slight envelope fade-out at ends to avoid popping
            float envelope = 1.0f;
            if (i < 500) envelope = i / 500.0f;
            else if (i > sampleCount - 500) envelope = (sampleCount - i) / 500.0f;

            samples[i] = (short)(MathF.Sin(phase) * 16000.0f * envelope);
            phase += phaseInc;
        }

        return new SoundClip(samples, sampleRate, channels: 1);
    }

    /// <summary>
    /// Generates a low-frequency impact thud (useful for rigid body collisions).
    /// </summary>
    public static SoundClip CreateImpactSound(float durationSeconds = 0.25f, int sampleRate = 44100)
    {
        int sampleCount = (int)(sampleRate * durationSeconds);
        var samples = new short[sampleCount];
        float phase = 0.0f;

        for (int i = 0; i < sampleCount; i++)
        {
            float t = (float)i / sampleCount;
            // Frequency slides down from 120Hz to 40Hz
            float freq = 120.0f * (1.0f - t * 0.7f);
            float phaseInc = (2.0f * MathF.PI * freq) / sampleRate;

            // Exponential decay
            float decay = MathF.Exp(-t * 12.0f);
            samples[i] = (short)(MathF.Sin(phase) * 24000.0f * decay);
            phase += phaseInc;
        }

        return new SoundClip(samples, sampleRate, channels: 1);
    }

    /// <summary>
    /// Generates a short footstep scuff/step sound.
    /// </summary>
    public static SoundClip CreateFootstepSound(float durationSeconds = 0.12f, int sampleRate = 44100)
    {
        int sampleCount = (int)(sampleRate * durationSeconds);
        var samples = new short[sampleCount];
        var rng = new Random(42);

        for (int i = 0; i < sampleCount; i++)
        {
            float t = (float)i / sampleCount;
            float decay = MathF.Exp(-t * 18.0f);
            // Low-pass filtered noise
            float noise = (float)(rng.NextDouble() * 2.0 - 1.0);
            samples[i] = (short)(noise * 12000.0f * decay);
        }

        return new SoundClip(samples, sampleRate, channels: 1);
    }

    /// <summary>
    /// Generates a jumping whoosh sound.
    /// </summary>
    public static SoundClip CreateJumpSound(float durationSeconds = 0.2f, int sampleRate = 44100)
    {
        int sampleCount = (int)(sampleRate * durationSeconds);
        var samples = new short[sampleCount];
        float phase = 0.0f;

        for (int i = 0; i < sampleCount; i++)
        {
            float t = (float)i / sampleCount;
            // Upward frequency sweep 150Hz -> 450Hz
            float freq = 150.0f + (300.0f * t);
            float phaseInc = (2.0f * MathF.PI * freq) / sampleRate;
            float decay = MathF.Sin(t * MathF.PI); // parabolic swell

            samples[i] = (short)(MathF.Sin(phase) * 16000.0f * decay);
            phase += phaseInc;
        }

        return new SoundClip(samples, sampleRate, channels: 1);
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            if (!_buffer.IsNull())
            {
                _buffer.Delete();
                _buffer = AlBuffer.Null;
            }
            _disposed = true;
        }
        GC.SuppressFinalize(this);
    }
}
