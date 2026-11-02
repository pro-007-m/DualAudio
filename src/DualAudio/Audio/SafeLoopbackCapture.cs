using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace DualAudio.Audio;

/// <summary>
/// Polling WASAPI loopback with guarded native shutdown. In particular, Stop() may
/// itself fail after a Bluetooth endpoint disappears; that must not escape a worker
/// thread's finally block and terminate the application.
/// </summary>
internal sealed class SafeLoopbackCapture : IWaveIn
{
    private readonly object _gate = new();
    private readonly AudioClient _client;
    private readonly ManualResetEventSlim _stop = new(false);
    private Thread? _thread;
    private bool _initialized;
    private bool _disposed;
    private WaveFormat _format;
    public event EventHandler<WaveInEventArgs>? DataAvailable;
    public event EventHandler<StoppedEventArgs>? RecordingStopped;

    public WaveFormat WaveFormat
    {
        get => _format;
        set
        {
            lock (_gate)
            {
                if (_initialized) throw new InvalidOperationException("Capture format is already initialized.");
                _format = value ?? throw new ArgumentNullException(nameof(value));
            }
        }
    }

    public SafeLoopbackCapture(MMDevice device)
    {
        _client = device.AudioClient;
        try { _format = _client.MixFormat; }
        catch { _client.Dispose(); throw; }
    }

    public void StartRecording()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_thread != null) throw new InvalidOperationException("Capture has already started.");
            _client.Initialize(AudioClientShareMode.Shared,
                AudioClientStreamFlags.Loopback | AudioClientStreamFlags.AutoConvertPcm |
                AudioClientStreamFlags.SrcDefaultQuality, 20 * 10_000L, 0, _format, Guid.Empty);
            _initialized = true;
            var capture = _client.AudioCaptureClient;
            int bufferBytes = checked(Math.Max(1, _client.BufferSize) * _format.BlockAlign);
            _thread = new Thread(() => Capture(capture, bufferBytes))
            {
                IsBackground = true,
                Name = "DualAudio loopback"
            };
            _thread.Start();
        }
    }

    private void Capture(AudioCaptureClient capture, int initialBytes)
    {
        Exception? error = null;
        byte[] buffer = new byte[initialBytes];
        try
        {
            _client.Start();
            while (!_stop.Wait(10))
            {
                int packetFrames = capture.GetNextPacketSize();
                while (packetFrames > 0 && !_stop.IsSet)
                {
                    IntPtr native = capture.GetBuffer(out int frames, out AudioClientBufferFlags flags);
                    try
                    {
                        int bytes = checked(frames * _format.BlockAlign);
                        if (buffer.Length < bytes) buffer = new byte[bytes];
                        if ((flags & AudioClientBufferFlags.Silent) != 0)
                        {
                            // 8-bit unsigned PCM represents digital silence as 128.
                            if (_format.BitsPerSample == 8)
                                Array.Fill(buffer, (byte)128, 0, bytes);
                            else Array.Clear(buffer, 0, bytes);
                        }
                        else Marshal.Copy(native, buffer, 0, bytes);
                        if (bytes != 0) DataAvailable?.Invoke(this, new WaveInEventArgs(buffer, bytes));
                    }
                    finally { capture.ReleaseBuffer(frames); }
                    packetFrames = capture.GetNextPacketSize();
                }
            }
        }
        catch (Exception ex) { error = ex; }
        finally
        {
            // The endpoint can be invalid before this call. Cleanup is intentionally
            // guarded as carefully as acquisition, so unplugging a device is recoverable.
            try { _client.Stop(); }
            catch (Exception ex) { if (!_stop.IsSet) error ??= ex; }
        }
        RecordingStopped?.Invoke(this, new StoppedEventArgs(error));
    }

    public void StopRecording()
    {
        lock (_gate) { if (!_disposed) _stop.Set(); }
    }

    public void Dispose()
    {
        Thread? thread;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _stop.Set();
            thread = _thread;
        }
        if (thread != null && thread != Thread.CurrentThread) thread.Join();
        try { _client.Dispose(); }
        finally { _stop.Dispose(); }
    }
}
