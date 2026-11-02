using System.Text.Json;
using DualAudio.Audio;
using DualAudio.Platform;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace DualAudio;

internal static class Diagnostics
{
    public static int Run(string[] args)
    {
        string destination = args.Length > 1 ? args[1] : Path.Combine(AppSettings.Folder, "diagnostics.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destination))!);
        switch (args[0])
        {
            case "--licenses":
                using (var stream = typeof(Program).Assembly.GetManifestResourceStream("DualAudio.ThirdPartyNotices.txt")!)
                using (var reader = new StreamReader(stream)) File.WriteAllText(destination, reader.ReadToEnd());
                return 0;
            case "--self-test":
                File.WriteAllText(destination, EngineTests.Run()); return 0;
            case "--devices":
                using (var devices = new MMDeviceEnumerator())
                {
                    string console = devices.GetDefaultAudioEndpoint(DataFlow.Render, Role.Console).ID;
                    string multimedia = devices.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia).ID;
                    var list = new List<object>();
                    foreach (var d in devices.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
                    {
                        using (d)
                        {
                            string format;
                            try { using var client = d.AudioClient; format = client.MixFormat.ToString(); }
                            catch (Exception ex) { format = ex.Message; }
                            list.Add(new { d.ID, d.FriendlyName, Format = format, IsConsole = d.ID == console, IsMultimedia = d.ID == multimedia,
                                Volume = d.AudioEndpointVolume.MasterVolumeLevelScalar, Muted = d.AudioEndpointVolume.Mute });
                        }
                    }
                    File.WriteAllText(destination, JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true }));
                }
                return 0;
            case "--ui-smoke":
                using (var form = new MainForm(false))
                {
                    form.Show(); Application.DoEvents();
                    form.VerifyLayout();
                    using var picture = new Bitmap(form.Width, form.Height);
                    form.DrawToBitmap(picture, new Rectangle(Point.Empty, picture.Size));
                    picture.Save(destination, System.Drawing.Imaging.ImageFormat.Png);
                    form.Close();
                }
                return 0;
            case "--live-test":
                if (args.Length < 4) throw new ArgumentException("--live-test report.txt sourceId targetId");
                LiveTest(destination, args[2], args[3]); return 0;
            case "--lease-test":
                if (args.Length < 3) throw new ArgumentException("--lease-test report.txt sourceId");
                TestLease(destination, args[2]); return 0;
            case "--tone":
                if (args.Length < 3) throw new ArgumentException("--tone report.txt outputId");
                using (var devices = new MMDeviceEnumerator())
                using (var device = devices.GetDevice(args[2]))
                using (var player = new WasapiOut(device, AudioClientShareMode.Shared, true, 80))
                {
                    var signal = new SignalGenerator(48000, 2) { Frequency = 440, Gain = .1, Type = SignalGeneratorType.Sin };
                    player.Init(new SampleToWaveProvider(signal.Take(TimeSpan.FromSeconds(3))));
                    player.Play(); Thread.Sleep(3400); player.Stop();
                    File.WriteAllText(destination, $"Direct tone rendered to: {device.FriendlyName}; endpoint volume={device.AudioEndpointVolume.MasterVolumeLevelScalar}; mute={device.AudioEndpointVolume.Mute}. Acoustic confirmation required.");
                }
                return 0;
            default: throw new ArgumentException("Unknown diagnostic command.");
        }
    }

    private static void TestLease(string destination, string sourceId)
    {
        using var devices = new MMDeviceEnumerator();
        string beforeConsole = devices.GetDefaultAudioEndpoint(DataFlow.Render, Role.Console).ID;
        string beforeMultimedia = devices.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia).ID;
        string beforeCommunications = devices.GetDefaultAudioEndpoint(DataFlow.Render, Role.Communications).ID;
        var lease = DefaultOutputLease.Acquire(sourceId);
        try
        {
            if (devices.GetDefaultAudioEndpoint(DataFlow.Render, Role.Console).ID != sourceId ||
                devices.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia).ID != sourceId)
                throw new Exception("Source was not selected.");
        }
        finally { lease.Dispose(); }
        if (lease.RestoreError != null) throw lease.RestoreError;
        if (devices.GetDefaultAudioEndpoint(DataFlow.Render, Role.Console).ID != beforeConsole ||
            devices.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia).ID != beforeMultimedia ||
            devices.GetDefaultAudioEndpoint(DataFlow.Render, Role.Communications).ID != beforeCommunications)
            throw new Exception("Windows output roles were not restored.");
        File.WriteAllText(destination, "PASS: Console and Multimedia switched and restored; Communications preserved.");
    }

    private static void LiveTest(string destination, string sourceId, string targetId)
    {
        using var devices = new MMDeviceEnumerator();
        using var sourceDevice = devices.GetDevice(sourceId);
        using var targetDevice = devices.GetDevice(targetId);
        using var router = new AudioRouter();
        string? error = null; router.Faulted += (_, message) => error = message;
        using var observed = new SafeLoopbackCapture(targetDevice);
        long bytes = 0; double max = 0;
        var samples = new List<float>();
        var format = observed.WaveFormat;
        var decoder = new StereoDecoder(format);
        observed.RecordingStopped += (_, e) => { if (e.Exception != null) error = e.Exception.Message; };
        observed.DataAvailable += (_, e) =>
        {
            Interlocked.Add(ref bytes, e.BytesRecorded);
            int frames = e.BytesRecorded / format.BlockAlign;
            var stereo = new float[frames * 2];
            decoder.Decode(e.Buffer.AsSpan(0, frames * format.BlockAlign), stereo);
            for (int i = 0; i < stereo.Length; i += 2)
            {
                float sample = stereo[i];
                max = Math.Max(max, Math.Abs(sample));
                samples.Add(sample);
            }
        };
        observed.StartRecording();
        router.Start(sourceId, targetId, sourceId, 120, 0, .7f, 1);
        using var output = new WasapiOut(sourceDevice, AudioClientShareMode.Shared, true, 80);
        var signal = new SignalGenerator(48000, 2) { Frequency = 523.25, Gain = .06, Type = SignalGeneratorType.Sin };
        output.Init(new SampleToWaveProvider(signal.Take(TimeSpan.FromSeconds(1.5))));
        output.Play();
        Thread.Sleep(2200);
        string description = router.Describe(); float sourcePeak = router.Peak;
        output.Stop(); router.Stop(); observed.StopRecording();
        // Join the capture worker before reading the sample list.
        observed.Dispose();
        if (error != null) throw new Exception(error);
        double tone = ToneAmplitude(samples, format.SampleRate);
        if (bytes == 0 || max < .0001 || tone < .0001) throw new Exception($"No generated tone observed at target. Bytes={bytes}; peak={max}; tone={tone}");
        File.WriteAllText(destination, $"PASS: real loopback -> target playback -> target loopback; generated 523.25 Hz tone detected.\nSource: {sourceDevice.FriendlyName}\nTarget: {targetDevice.FriendlyName}\nCaptured bytes: {bytes}\nTarget peak: {max:F6}\n523 Hz tone amplitude: {tone:F6}\nSource final peak: {sourcePeak:F6}\n{description}\nNo Windows defaults changed by this audio test.\n");
    }

    private static double ToneAmplitude(List<float> samples, int rate)
    {
        double best = 0;
        if (samples.Count == 0) return 0;
        // Allow small clock correction and independent endpoint oscillator drift.
        for (double hz = 520; hz <= 527; hz += .25)
        {
            double cosine = Math.Cos(2 * Math.PI * hz / rate);
            double coefficient = 2 * cosine, previous = 0, older = 0;
            foreach (float sample in samples)
            { double next = sample + coefficient * previous - older; older = previous; previous = next; }
            double power = Math.Max(0, previous * previous + older * older - coefficient * previous * older);
            best = Math.Max(best, 2 * Math.Sqrt(power) / samples.Count);
        }
        return best;
    }
}
