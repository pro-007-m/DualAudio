using System.Runtime.InteropServices;
using System.Diagnostics;
using NAudio.Wave;

namespace DualAudio.Audio;

/// <summary>Deterministic checks. Does not open, render to, or change any audio device.</summary>
public static class EngineTests
{
    public static string Run()
    {
        var passed = new List<string>();
        Check("underflow clears output and never repeats old samples", TestUnderflow);
        Check("bounded overflow preserves newest complete stereo frames", TestOverflow);
        Check("ring wrap preserves frame and channel order", TestWrap);
        Check("short notification survives delay followed by source silence", TestShortNotification);
        Check("interrupted startup does not replay a stale notification", TestInterruptedStartup);
        Check("live delay change inserts silence or skips whole frames", TestDelayChange);
        Check("independent output queues do not affect each other", TestIndependence);
        Check("positive and negative device clock drift remain bounded", TestDrift);
        Check("PCM16, PCM24, float mono and nonfinite conversion", TestDecoder);
        Check("source endpoint is never selected for mirrored rendering", TestRoutingSafety);
        return $"PASS: {passed.Count} audio-engine checks\n" + string.Join("\n", passed.Select(s => $"  PASS {s}"));

        void Check(string name, Action action)
        {
            try { action(); passed.Add(name); }
            catch (Exception ex) { throw new InvalidOperationException($"FAIL: {name}: {ex.Message}", ex); }
        }
    }

    private static AdaptiveAudioBuffer Buffer(int delay = 0, int capacity = 1000) =>
        new(1000, delay, 1, safetyMs: 0, capacityMs: capacity, correctClock: false);

    private static float[] Frames(int start, int count)
    {
        var result = new float[count * 2];
        for (int frame = 0; frame < count; frame++)
        {
            result[frame * 2] = start + frame;
            result[frame * 2 + 1] = -(start + frame);
        }
        return result;
    }

    private static float[] Read(AdaptiveAudioBuffer buffer, int frames)
    {
        var result = Enumerable.Repeat(123f, frames * 2).ToArray();
        buffer.ReadFrames(result);
        return result;
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
    private static void Near(float actual, float expected, string message)
    {
        Assert(Math.Abs(actual - expected) < 0.0001f, $"{message}: expected {expected}, got {actual}");
    }

    private static void TestUnderflow()
    {
        var buffer = Buffer();
        Assert(Read(buffer, 10).All(s => s == 0), "Fresh empty read was not silent.");
        buffer.Write(Frames(1, 10));
        var first = Read(buffer, 10);
        Near(first[0], 1, "First left channel");
        Near(first[1], -1, "First right channel");
        Near(first[^2], 10, "Last sample");
        Assert(Read(buffer, 10).All(s => s == 0), "Underflow repeated old data.");
        buffer.Write(Frames(100, 10));
        Near(Read(buffer, 10)[0], 100, "Resume replayed stale data");
        var bytes = Enumerable.Repeat((byte)255, 96).ToArray();
        buffer.Read(bytes, 8, 80);
        Assert(bytes.AsSpan(8, 80).ToArray().All(b => b == 0), "Byte provider did not clear underflow.");
        Assert(bytes[0] == 255 && bytes[^1] == 255, "Read wrote outside supplied bounds.");
    }

    private static void TestOverflow()
    {
        var buffer = Buffer(capacity: 100);
        buffer.Write(Frames(0, 160));
        var metrics = buffer.GetMetrics();
        Assert(metrics.BufferedFrames == 100 && metrics.DroppedFrames == 60, "Overflow accounting or capacity.");
        var result = Read(buffer, 20);
        // The initial five frames fade in after a discontinuity.
        Near(result[20], 70, "Newest frames were not preserved");
        Near(result[21], -70, "Stereo alignment was lost");
        Assert(buffer.GetMetrics().BufferedFrames == 80, "Overflow read count.");
        buffer.Close();
        buffer.Write(Frames(500, 10));
        Assert(Read(buffer, 10).All(s => s == 0), "Closed queue emitted data.");
    }

    private static void TestWrap()
    {
        var buffer = Buffer(capacity: 100);
        buffer.Write(Frames(0, 80));
        Read(buffer, 60);
        buffer.Write(Frames(80, 70));
        var result = Read(buffer, 90);
        for (int frame = 0; frame < 90; frame++)
        {
            Near(result[frame * 2], frame + 60, "Ring wrap left");
            Near(result[frame * 2 + 1], -(frame + 60), "Ring wrap right");
        }
    }

    private static void TestShortNotification()
    {
        var buffer = Buffer(delay: 120);
        var received = new List<float>();
        for (int time = 0; time < 400; time += 10)
        {
            var input = new float[20];
            if (time == 0) Array.Fill(input, 0.25f); // A 10 ms sound, much shorter than delay.
            buffer.Write(input); // The source's silent keepalive supplies the trailing silence.
            received.AddRange(Read(buffer, 10));
        }
        int first = received.FindIndex(s => s != 0) / 2;
        Assert(first == 120, $"Short sound began at {first} ms instead of 120 ms.");
        Assert(received.Count(s => s != 0) == 20, "Short sound was lost or repeated.");
        Assert(received.Skip((120 + 10) * 2).All(s => s == 0), "Trailing silence replayed old audio.");
    }

    private static void TestDelayChange()
    {
        var buffer = Buffer();
        buffer.Write(Frames(1, 60));
        Near(Read(buffer, 10)[0], 1, "Initial output");
        buffer.SetSettings(20, 1);
        Assert(Read(buffer, 10).All(s => s == 0), "Increasing delay did not insert first silence.");
        Assert(Read(buffer, 10).All(s => s == 0), "Increasing delay did not insert second silence.");
        buffer.Write(Frames(61, 20));
        Near(Read(buffer, 10)[0], 11, "Increasing delay discarded queued audio");
        buffer.SetSettings(0, 0.5f);
        var output = Read(buffer, 10);
        Near(output[12], 47 * 0.5f, "Decreased delay skipped wrong frame count or wrong gain");
        Near(output[13], -47 * 0.5f, "Decreased delay lost channel alignment");
    }

    private static void TestInterruptedStartup()
    {
        var buffer = Buffer(delay: 120);
        buffer.WriteAt(Frames(1, 10), 1);
        Assert(Read(buffer, 10).All(s => s == 0), "Short sound should still be priming.");
        var later = new float[260];
        later[0] = 0.25f; later[1] = -0.25f;
        buffer.WriteAt(later, Stopwatch.Frequency + 1);
        var output = Read(buffer, 10);
        Near(output[0], 0.25f, "Interrupted startup replayed an old short sound");
        Near(output[1], -0.25f, "Interrupted startup lost stereo alignment");
        Assert(buffer.GetMetrics().DroppedFrames == 10, "Stale priming frames were not accounted.");
    }

    private static void TestIndependence()
    {
        var first = Buffer(capacity: 100);
        var second = Buffer(capacity: 100);
        var input = Frames(0, 80);
        first.Write(input); second.Write(input);
        Read(first, 70);
        input = Frames(80, 80);
        first.Write(input); second.Write(input);
        Assert(first.GetMetrics().DroppedFrames == 0, "One consumer caused drops on another.");
        Assert(second.GetMetrics().DroppedFrames == 60, "Slow consumer's overflow was not bounded.");
        Near(Read(first, 10)[0], 70, "Independent consumer lost its position");
    }

    private static void TestDrift()
    {
        foreach (int direction in new[] { -1, 1 })
        {
            var buffer = new AdaptiveAudioBuffer(1000, 0, 1, 40, 1000);
            buffer.Write(new float[120]);
            Read(buffer, 10);
            var ten = new float[20];
            var changed = new float[(10 + direction) * 2];
            var output = new float[20];
            for (int block = 1; block <= 40000; block++)
            {
                buffer.Write(block % 100 == 0 ? changed : ten); // Simulated +/- 1000 ppm.
                buffer.ReadFrames(output);
            }
            var metrics = buffer.GetMetrics();
            Assert(metrics.Underflows == 0 && metrics.DroppedFrames == 0, $"Clock {direction}: dropped or underflowed.");
            Assert(metrics.BufferedFrames is > 5 and < 150, $"Clock {direction}: queue drifted out of bounds ({metrics.BufferedFrames}).");
            Assert(Math.Sign(metrics.CorrectionPpm) == direction, "Clock correction had wrong direction.");
        }
    }

    private static void TestDecoder()
    {
        var pcm16 = new StereoDecoder(new WaveFormat(48000, 16, 2));
        var decoded = new float[2];
        pcm16.Decode([0x00, 0x40, 0x00, 0xC0], decoded);
        Near(decoded[0], 0.5f, "PCM16 left"); Near(decoded[1], -0.5f, "PCM16 right");
        var pcm24 = new StereoDecoder(new WaveFormat(48000, 24, 2));
        pcm24.Decode([0x00, 0x00, 0x40, 0x00, 0x00, 0xC0], decoded);
        Near(decoded[0], 0.5f, "PCM24 sign extension"); Near(decoded[1], -0.5f, "PCM24 negative");
        var mono = new StereoDecoder(WaveFormat.CreateIeeeFloatWaveFormat(44100, 1));
        mono.Decode(BitConverter.GetBytes(0.25f), decoded);
        Near(decoded[0], 0.25f, "Mono left"); Near(decoded[1], 0.25f, "Mono right");
        mono.Decode(BitConverter.GetBytes(float.NaN), decoded);
        Assert(decoded.All(s => s == 0), "Non-finite sample was not sanitized.");
        var extensible = new StereoDecoder(new WaveFormatExtensible(48000, 32, 2));
        extensible.Decode(MemoryMarshal.AsBytes(new float[] { 0.5f, -0.25f }.AsSpan()), decoded);
        Near(decoded[0], 0.5f, "Extensible float left"); Near(decoded[1], -0.25f, "Extensible float right");
    }

    private static void TestRoutingSafety()
    {
        Assert(!AudioRouter.NeedsRender("source", "SOURCE"), "Direct source was selected for mirrored render.");
        Assert(AudioRouter.NeedsRender("source", "other"), "Other output was not selected for mirror.");
        AudioRouter.ValidateRoute("source", "source", "other");
        AudioRouter.ValidateRoute("source", "other", "source");
        AudioRouter.ValidateRoute("virtual", "one", "two");
        bool rejected = false;
        try { AudioRouter.ValidateRoute("source", "same", "SAME"); }
        catch (ArgumentException) { rejected = true; }
        Assert(rejected, "Duplicate outputs were accepted.");
    }
}
