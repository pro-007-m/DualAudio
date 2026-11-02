using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace DualAudio.Audio;

/// <summary>Copies one render endpoint's mix to the other selected render endpoints.</summary>
public sealed class AudioRouter : IDisposable
{
    private readonly object _gate = new();
    private Session? _session;
    private bool _disposed;
    public event EventHandler<string>? Faulted;

    public bool IsRunning { get { lock (_gate) return _session is { IsFaulted: false, IsStopping: false }; } }
    public float Peak { get { lock (_gate) return _session?.Peak ?? 0; } }

    public void Start(string sourceId, string firstId, string secondId,
        int firstDelayMs, int secondDelayMs, float firstVolume, float secondVolume)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(firstId);
        ArgumentException.ThrowIfNullOrWhiteSpace(secondId);
        AdaptiveAudioBuffer.ValidateSettings(firstDelayMs, firstVolume);
        AdaptiveAudioBuffer.ValidateSettings(secondDelayMs, secondVolume);
        ValidateRoute(sourceId, firstId, secondId);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            StopCore();
            var session = new Session(this, sourceId, firstId, secondId,
                firstDelayMs, secondDelayMs, firstVolume, secondVolume);
            _session = session;
            try { session.Begin(); }
            catch { _session = null; session.Dispose(); throw; }
        }
    }

    internal static bool NeedsRender(string sourceId, string outputId) =>
        !string.Equals(sourceId, outputId, StringComparison.OrdinalIgnoreCase);

    internal static void ValidateRoute(string sourceId, string firstId, string secondId)
    {
        if (string.Equals(firstId, secondId, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Выберите два разных устройства вывода.");
        // The source endpoint already plays its own mix. Sending the captured mix back
        // to that endpoint would be recaptured, producing an ever louder feedback loop.
    }

    public void Stop() { lock (_gate) StopCore(); }
    private void StopCore()
    {
        var session = _session;
        _session = null;
        session?.Dispose();
    }

    public void SetOutput(int index, int delayMs, float volume)
    {
        if (index is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(index));
        AdaptiveAudioBuffer.ValidateSettings(delayMs, volume);
        lock (_gate) _session?.SetOutput(index, delayMs, volume);
    }

    public string Describe() { lock (_gate) return _session?.Describe() ?? "Передача остановлена."; }

    private void OnFault(Session session, string message)
    {
        ThreadPool.QueueUserWorkItem(_ =>
        {
            lock (_gate)
            {
                if (!ReferenceEquals(_session, session)) return;
                _session = null;
                session.Dispose();
            }
            Faulted?.Invoke(this, message);
        });
    }

    public void Dispose()
    {
        lock (_gate) { if (_disposed) return; _disposed = true; StopCore(); }
    }

    private sealed class Session : IDisposable
    {
        private readonly AudioRouter _owner;
        private readonly MMDeviceEnumerator _enumerator = new();
        private readonly List<MMDevice> _devices = [];
        private readonly Output?[] _outputs = new Output?[2];
        private readonly string[] _outputNames = new string[2];
        private readonly string _sourceName;
        private readonly WaveFormat _sourceFormat;
        private readonly StereoDecoder _decoder;
        private readonly SafeLoopbackCapture _capture;
        private readonly WasapiOut _keepAlive;
        private int _faulted;
        private int _stopping;
        private float _peak;
        private long _peakTimestamp;
        public bool IsFaulted => Volatile.Read(ref _faulted) != 0;
        public bool IsStopping => Volatile.Read(ref _stopping) != 0;
        public float Peak
        {
            get
            {
                double elapsed = (Stopwatch.GetTimestamp() - Volatile.Read(ref _peakTimestamp)) /
                    (double)Stopwatch.Frequency;
                return Math.Max(0, Volatile.Read(ref _peak) - (float)(elapsed * 1.8));
            }
        }

        public Session(AudioRouter owner, string sourceId, string firstId, string secondId,
            int firstDelay, int secondDelay, float firstVolume, float secondVolume)
        {
            _owner = owner;
            try
            {
                var source = OpenDevice(sourceId);
                _sourceName = source.FriendlyName;
                using (var client = source.AudioClient) _sourceFormat = client.MixFormat;
                _decoder = new(_sourceFormat);
                _capture = new SafeLoopbackCapture(source) { WaveFormat = _sourceFormat };
                _capture.DataAvailable += DataAvailable;
                _capture.RecordingStopped += CaptureStopped;
                // Windows can stop delivering loopback packets when every source stream
                // is inactive. A zero-only source stream keeps the audio timeline moving,
                // so even a short notification acquires its trailing silence and is played.
                // Captured audio is NEVER sent to this stream.
                _keepAlive = new WasapiOut(source, AudioClientShareMode.Shared, true, 80);
                _keepAlive.PlaybackStopped += PlaybackStopped;
                _keepAlive.Init(new ZeroProvider(_sourceFormat.SampleRate));
                string[] ids = [firstId, secondId];
                int[] delays = [firstDelay, secondDelay];
                float[] volumes = [firstVolume, secondVolume];
                for (int index = 0; index < 2; index++)
                {
                    if (!NeedsRender(sourceId, ids[index]))
                    {
                        _outputNames[index] = _sourceName;
                        continue;
                    }
                    var device = OpenDevice(ids[index]);
                    _outputNames[index] = device.FriendlyName;
                    var queue = new AdaptiveAudioBuffer(_sourceFormat.SampleRate, delays[index], volumes[index]);
                    var player = new WasapiOut(device, AudioClientShareMode.Shared, true, 80);
                    var output = new Output(device.FriendlyName, queue, player);
                    _outputs[index] = output;
                    player.PlaybackStopped += PlaybackStopped;
                    player.Init(queue);
                }
            }
            catch { Dispose(); throw; }
        }

        private MMDevice OpenDevice(string id)
        {
            var device = _enumerator.GetDevice(id);
            _devices.Add(device);
            if (device.State != DeviceState.Active || device.DataFlow != DataFlow.Render)
                throw new InvalidOperationException($"Устройство вывода недоступно: {device.FriendlyName}");
            return device;
        }

        public void Begin()
        {
            _keepAlive.Play();
            foreach (var output in _outputs) output?.Player.Play();
            _capture.StartRecording();
        }

        private void DataAvailable(object? sender, WaveInEventArgs e)
        {
            if (IsStopping || IsFaulted || e.BytesRecorded == 0) return;
            float[]? rented = null;
            try
            {
                int frames = e.BytesRecorded / _sourceFormat.BlockAlign;
                rented = ArrayPool<float>.Shared.Rent(checked(frames * 2));
                var samples = rented.AsSpan(0, frames * 2);
                _decoder.Decode(e.Buffer.AsSpan(0, frames * _sourceFormat.BlockAlign), samples);
                float peak = 0;
                foreach (float sample in samples) peak = Math.Max(peak, Math.Abs(sample));
                Volatile.Write(ref _peak, Math.Min(1, peak));
                Volatile.Write(ref _peakTimestamp, Stopwatch.GetTimestamp());
                foreach (var output in _outputs) output?.Queue.Write(samples);
            }
            catch (Exception ex) { Fail("Ошибка обработки звука", ex); }
            finally { if (rented != null) ArrayPool<float>.Shared.Return(rented); }
        }

        private void CaptureStopped(object? sender, StoppedEventArgs e)
        {
            if (!IsStopping) Fail("Источник звука остановлен или отключён", e.Exception);
        }
        private void PlaybackStopped(object? sender, StoppedEventArgs e)
        {
            if (!IsStopping) Fail("Устройство вывода остановлено или отключено", e.Exception);
        }
        private void Fail(string message, Exception? exception)
        {
            if (IsStopping || Interlocked.Exchange(ref _faulted, 1) != 0) return;
            _owner.OnFault(this, exception == null ? message : $"{message}: {exception.Message}");
        }

        public void SetOutput(int index, int delay, float volume) => _outputs[index]?.Queue.SetSettings(delay, volume);

        public string Describe()
        {
            var text = new StringBuilder();
            text.AppendLine($"Источник: {_sourceName}");
            text.AppendLine($"Захват: {_sourceFormat.SampleRate} Гц, {_sourceFormat.Channels} кан., {_sourceFormat.BitsPerSample} бит, {_sourceFormat.Encoding}; стереокопия float32.");
            text.AppendLine("Захват WASAPI 20 мс; вывод shared / event 80 мс; резерв очереди 40 мс + заданная задержка.");
            text.AppendLine("Нулевой поток источника поддерживает непрерывный отсчёт времени во время пауз.");
            for (int index = 0; index < 2; index++)
            {
                text.Append($"Выход {index + 1}: {_outputNames[index]}. ");
                var output = _outputs[index];
                if (output == null) text.AppendLine("Прямой звук Windows; программная копия отключена для защиты от обратной связи.");
                else
                {
                    var metrics = output.Queue.GetMetrics();
                    double milliseconds = 1000.0 * metrics.BufferedFrames / _sourceFormat.SampleRate;
                    text.AppendLine($"Очередь {milliseconds:F1} мс, коррекция {metrics.CorrectionPpm:F0} ppm, пропущено {metrics.DroppedFrames} кадров, опустошений {metrics.Underflows}; {(metrics.Primed ? "работает" : "ожидание звука / заполнение")}.");
                }
            }
            return text.ToString();
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _stopping, 1) != 0) return;
            if (_capture != null)
            {
                _capture.DataAvailable -= DataAvailable;
                _capture.RecordingStopped -= CaptureStopped;
                try { _capture.Dispose(); } catch { /* Device invalidation can race shutdown. */ }
            }
            if (_keepAlive != null)
            {
                _keepAlive.PlaybackStopped -= PlaybackStopped;
                try { _keepAlive.Dispose(); } catch { }
            }
            foreach (var output in _outputs)
            {
                if (output == null) continue;
                output.Queue.Close();
                output.Player.PlaybackStopped -= PlaybackStopped;
                try { output.Player.Dispose(); } catch { /* Continue releasing other endpoints. */ }
            }
            foreach (var device in _devices) device.Dispose();
            _enumerator.Dispose();
        }
    }

    private sealed record Output(string Name, AdaptiveAudioBuffer Queue, WasapiOut Player);

    private sealed class ZeroProvider(int sampleRate) : IWaveProvider
    {
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 2);
        public int Read(byte[] buffer, int offset, int count)
        {
            Array.Clear(buffer, offset, count);
            return count;
        }
    }
}

/// <summary>Converts the endpoint mix to stereo without depending on native audio codecs.</summary>
internal sealed class StereoDecoder
{
    private readonly int _channels;
    private readonly int _sampleBytes;
    private readonly bool _float;
    private readonly int _bits;
    private readonly float[] _left;
    private readonly float[] _right;
    public StereoDecoder(WaveFormat format)
    {
        _channels = format.Channels;
        _bits = format.BitsPerSample;
        _sampleBytes = _bits / 8;
        var encoding = format.Encoding;
        int mask = 0;
        if (format is WaveFormatExtensible extensible)
        {
            IntPtr pointer = WaveFormat.MarshalToPtr(extensible);
            try { mask = Marshal.ReadInt32(pointer, 20); }
            finally { Marshal.FreeHGlobal(pointer); }
            encoding = extensible.SubFormat == new Guid("00000003-0000-0010-8000-00aa00389b71")
                ? WaveFormatEncoding.IeeeFloat :
                extensible.SubFormat == new Guid("00000001-0000-0010-8000-00aa00389b71")
                    ? WaveFormatEncoding.Pcm : WaveFormatEncoding.Unknown;
        }
        _float = encoding == WaveFormatEncoding.IeeeFloat;
        if (_channels is < 1 or > 32 || format.BlockAlign != _sampleBytes * _channels ||
            !(_float && _bits is 32 or 64 || encoding == WaveFormatEncoding.Pcm && _bits is 8 or 16 or 24 or 32))
            throw new NotSupportedException($"Формат источника не поддерживается: {format}");
        _left = new float[_channels];
        _right = new float[_channels];
        if (_channels == 1) { _left[0] = 1; _right[0] = 1; return; }
        if (_channels == 2) { _left[0] = 1; _right[1] = 1; return; }
        if (mask == 0) mask = _channels switch { 4 => 0x33, 6 => 0x3f, 8 => 0x63f, _ => (1 << _channels) - 1 };
        int channel = 0;
        for (int bit = 0; bit < 31 && channel < _channels; bit++)
        {
            if ((mask & (1 << bit)) == 0) continue;
            (_left[channel], _right[channel]) = bit switch
            {
                0 => (1f, 0f), 1 => (0f, 1f), 2 => (0.7071f, 0.7071f),
                3 => (0f, 0f), // Do not add the LFE channel to headphone playback.
                4 or 6 or 9 => (0.7071f, 0f),
                5 or 7 or 10 => (0f, 0.7071f),
                _ => (0.5f, 0.5f)
            };
            channel++;
        }
        float scale = Math.Max(1, Math.Max(_left.Sum(), _right.Sum()));
        for (int i = 0; i < _channels; i++) { _left[i] /= scale; _right[i] /= scale; }
    }

    public void Decode(ReadOnlySpan<byte> input, Span<float> output)
    {
        int frames = input.Length / (_channels * _sampleBytes);
        if (output.Length != frames * 2) throw new ArgumentException("Decoded frame count mismatch.");
        for (int frame = 0; frame < frames; frame++)
        {
            float left = 0, right = 0;
            for (int channel = 0; channel < _channels; channel++)
            {
                var sample = input.Slice((frame * _channels + channel) * _sampleBytes, _sampleBytes);
                float value = _float
                    ? _bits == 32 ? BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(sample))
                        : (float)BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(sample))
                    : _bits switch
                    {
                        8 => (sample[0] - 128) / 128f,
                        16 => BinaryPrimitives.ReadInt16LittleEndian(sample) / 32768f,
                        24 => ((sample[0] | sample[1] << 8 | sample[2] << 16) << 8 >> 8) / 8388608f,
                        32 => BinaryPrimitives.ReadInt32LittleEndian(sample) / 2147483648f,
                        _ => 0
                    };
                if (!float.IsFinite(value)) value = 0;
                value = Math.Clamp(value, -1, 1);
                left += value * _left[channel]; right += value * _right[channel];
            }
            output[frame * 2] = left;
            output[frame * 2 + 1] = right;
        }
    }
}
