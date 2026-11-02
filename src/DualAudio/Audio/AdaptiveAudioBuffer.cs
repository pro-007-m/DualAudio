using System.Runtime.InteropServices;
using System.Diagnostics;
using NAudio.Wave;

namespace DualAudio.Audio;

/// <summary>
/// One independent queue per output clock. All positions count complete stereo frames.
/// Empty reads produce fresh zeroes, never old samples. No silence is appended on writes,
/// so an inactive source cannot create an ever growing backlog.
/// </summary>
internal sealed class AdaptiveAudioBuffer : IWaveProvider
{
    internal const int MaximumDelayMs = 2000;
    private readonly object _gate = new();
    private readonly float[] _samples;
    private readonly int _capacityFrames;
    private readonly int _safetyFrames;
    private readonly bool _correctClock;
    private int _readFrame;
    private int _countFrames;
    private int _delayFrames;
    private int _insertSilenceFrames;
    private double _fraction;
    private double _rate = 1;
    private double _filteredError;
    private bool _primed;
    private bool _closed;
    private float _volume = 1;
    private int _fadeFrames;
    private readonly int _fadeLength;
    private long _droppedFrames;
    private long _underflows;
    private long _receivedFrames;
    private long _playedFrames;
    private long _lastWriteTimestamp;
    private bool _hasWriteTimestamp;

    public WaveFormat WaveFormat { get; }

    public AdaptiveAudioBuffer(int sampleRate, int delayMs, float volume,
        int safetyMs = 40, int capacityMs = MaximumDelayMs + 1000, bool correctClock = true)
    {
        if (sampleRate < 1000 || sampleRate > 384000)
            throw new ArgumentOutOfRangeException(nameof(sampleRate));
        ValidateSettings(delayMs, volume);
        if (capacityMs <= safetyMs + delayMs || safetyMs < 0)
            throw new ArgumentOutOfRangeException(nameof(capacityMs));
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 2);
        _capacityFrames = checked(sampleRate * capacityMs / 1000);
        _samples = new float[checked(_capacityFrames * 2)];
        _safetyFrames = sampleRate * safetyMs / 1000;
        _fadeLength = Math.Max(1, sampleRate * 5 / 1000);
        _correctClock = correctClock;
        _delayFrames = sampleRate * delayMs / 1000;
        _volume = volume;
    }

    internal static void ValidateSettings(int delayMs, float volume)
    {
        if (delayMs < 0 || delayMs > MaximumDelayMs)
            throw new ArgumentOutOfRangeException(nameof(delayMs), "Задержка должна быть от 0 до 2000 мс.");
        if (!float.IsFinite(volume) || volume < 0 || volume > 1)
            throw new ArgumentOutOfRangeException(nameof(volume), "Громкость должна быть от 0 до 1.");
    }

    public void SetSettings(int delayMs, float volume)
    {
        ValidateSettings(delayMs, volume);
        lock (_gate)
        {
            int delayFrames = WaveFormat.SampleRate * delayMs / 1000;
            int difference = delayFrames - _delayFrames;
            _delayFrames = delayFrames;
            _volume = volume;
            if (_primed)
            {
                if (difference > 0)
                    _insertSilenceFrames += difference;
                else if (difference < 0)
                {
                    int remove = -difference;
                    int removeSilence = Math.Min(remove, _insertSilenceFrames);
                    _insertSilenceFrames -= removeSilence;
                    remove -= removeSilence;
                    SkipFrames(Math.Min(remove, _countFrames), true);
                    _fraction = 0;
                    _fadeFrames = _fadeLength;
                }
                _filteredError = 0;
                _rate = 1;
            }
        }
    }

    public void Write(ReadOnlySpan<float> stereoSamples) => WriteAt(stereoSamples, Stopwatch.GetTimestamp());

    internal void WriteAt(ReadOnlySpan<float> stereoSamples, long timestamp)
    {
        if (stereoSamples.Length % 2 != 0)
            throw new ArgumentException("Audio must contain complete stereo frames.", nameof(stereoSamples));
        lock (_gate)
        {
            if (_closed || stereoSamples.IsEmpty) return;
            // If capture was interrupted while still priming, do not resurrect an old
            // short sound when a later sound finally fills the delay queue.
            if (!_primed && _countFrames > 0 && _hasWriteTimestamp &&
                timestamp - _lastWriteTimestamp > Stopwatch.Frequency / 4)
            {
                SkipFrames(_countFrames, true);
                _fraction = 0;
            }
            _lastWriteTimestamp = timestamp;
            _hasWriteTimestamp = true;
            int frames = stereoSamples.Length / 2;
            _receivedFrames += frames;
            if (frames >= _capacityFrames)
            {
                _droppedFrames += _countFrames + frames - _capacityFrames;
                stereoSamples = stereoSamples[^(_capacityFrames * 2)..];
                frames = _capacityFrames;
                _readFrame = 0;
                _countFrames = 0;
                _fraction = 0;
                _fadeFrames = _fadeLength;
            }
            else if (_countFrames + frames > _capacityFrames)
            {
                SkipFrames(_countFrames + frames - _capacityFrames, true);
                _fraction = 0;
                _fadeFrames = _fadeLength;
            }
            int writeFrame = (_readFrame + _countFrames) % _capacityFrames;
            int firstFrames = Math.Min(frames, _capacityFrames - writeFrame);
            stereoSamples[..(firstFrames * 2)].CopyTo(_samples.AsSpan(writeFrame * 2));
            stereoSamples[(firstFrames * 2)..].CopyTo(_samples);
            _countFrames += frames;
        }
    }

    public int Read(byte[] buffer, int offset, int count)
    {
        if (offset < 0 || count < 0 || offset > buffer.Length - count)
            throw new ArgumentOutOfRangeException(nameof(count));
        var bytes = buffer.AsSpan(offset, count);
        bytes.Clear();
        int framesRequested = count / WaveFormat.BlockAlign;
        var output = MemoryMarshal.Cast<byte, float>(bytes[..(framesRequested * WaveFormat.BlockAlign)]);
        ReadFrames(output);
        // Live stream: an empty source is silence, not end of stream.
        return count;
    }

    internal void ReadFrames(Span<float> output)
    {
        if (output.Length % 2 != 0) throw new ArgumentException("Output must be stereo.", nameof(output));
        output.Clear();
        lock (_gate)
        {
            if (_closed || output.IsEmpty) return;
            int requested = output.Length / 2;
            int target = _safetyFrames + _delayFrames;
            if (!_primed)
            {
                // The first WASAPI read fills its entire hardware buffer. Leave a safety
                // reserve after that read instead of immediately falling back into underflow.
                if (_countFrames < target + requested) return;
                _primed = true;
                _fraction = 0;
                _filteredError = 0;
                _rate = 1;
                _insertSilenceFrames = 0;
            }
            int silence = Math.Min(requested, _insertSilenceFrames);
            _insertSilenceFrames -= silence;
            if (silence == requested) return;

            int remaining = requested - silence;
            // A stalled endpoint must catch up to the present when it resumes. Never
            // replay an old multi-second queue after Bluetooth is suspended.
            int normalFrames = target + remaining;
            int maximumExtra = WaveFormat.SampleRate * 250 / 1000;
            if (_countFrames > normalFrames + maximumExtra)
            {
                int keep = Math.Min(_countFrames, normalFrames + WaveFormat.SampleRate * 20 / 1000);
                SkipFrames(_countFrames - keep, true);
                _fraction = 0;
                _fadeFrames = _fadeLength;
            }

            if (_correctClock)
            {
                // A slow low-pass prevents packet scheduling jitter from modulating pitch.
                // Linear interpolation changes consumption by at most 0.2%, enough to
                // correct ordinary independent DAC clock drift without hard frame repeats.
                double error = (_countFrames - remaining - target) / (double)WaveFormat.SampleRate;
                double smoothing = 1 - Math.Exp(-remaining / (2.0 * WaveFormat.SampleRate));
                _filteredError += smoothing * (error - _filteredError);
                _rate = 1 + Math.Clamp(_filteredError * 0.08, -0.002, 0.002);
            }

            for (int frame = silence; frame < requested; frame++)
            {
                if (_countFrames == 0)
                {
                    _underflows++;
                    _primed = false;
                    _fraction = 0;
                    _rate = 1;
                    _filteredError = 0;
                    _insertSilenceFrames = 0;
                    break;
                }
                int first = _readFrame * 2;
                int second = ((_readFrame + Math.Min(1, _countFrames - 1)) % _capacityFrames) * 2;
                float left = (float)(_samples[first] + (_samples[second] - _samples[first]) * _fraction);
                float right = (float)(_samples[first + 1] + (_samples[second + 1] - _samples[first + 1]) * _fraction);
                float gain = _volume;
                if (_fadeFrames > 0)
                {
                    gain *= 1 - _fadeFrames / (float)_fadeLength;
                    _fadeFrames--;
                }
                output[frame * 2] = left * gain;
                output[frame * 2 + 1] = right * gain;
                _playedFrames++;
                _fraction += _rate;
                int consumed = (int)_fraction;
                _fraction -= consumed;
                SkipFrames(Math.Min(consumed, _countFrames), false);
            }
        }
    }

    private void SkipFrames(int count, bool dropped)
    {
        if (count <= 0) return;
        _readFrame = (_readFrame + count) % _capacityFrames;
        _countFrames -= count;
        if (dropped) _droppedFrames += count;
    }

    public BufferMetrics GetMetrics()
    {
        lock (_gate)
            return new(_countFrames, _capacityFrames, _droppedFrames, _underflows,
                _receivedFrames, _playedFrames, (_rate - 1) * 1_000_000, _primed);
    }

    public void Close()
    {
        lock (_gate)
        {
            _closed = true;
            _countFrames = 0;
            _fraction = 0;
            _insertSilenceFrames = 0;
        }
    }
}

internal readonly record struct BufferMetrics(int BufferedFrames, int CapacityFrames,
    long DroppedFrames, long Underflows, long ReceivedFrames, long PlayedFrames,
    double CorrectionPpm, bool Primed);
