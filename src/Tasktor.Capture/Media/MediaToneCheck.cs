using System.Runtime.InteropServices;
using Tasktor.Capture.Deep;

namespace Tasktor.Capture.Media;

/// <summary>
/// The #9 real-PC check (<c>tasktor-capture --check-media</c>): plays a tone it writes itself, then the same length of
/// silence, through this process's own audio session, and reads its own session's meter only. It checks that audible
/// rises and falls for the tone, that the silent run never becomes audible (C20 acceptance 2), and counts other apps'
/// sessions it ignored. Prints pass / fail and counts; records nothing.
/// </summary>
public static partial class MediaToneCheck
{
    public static int Run(TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (!OperatingSystem.IsWindows())
        {
            output.WriteLine("check-media: Windows only");
            return 2;
        }

        var dir = Path.Combine(Path.GetTempPath(), "tasktor-tone-" + Environment.ProcessId);
        Directory.CreateDirectory(dir);
        try
        {
            var tone = Path.Combine(dir, "tone.wav");
            var silence = Path.Combine(dir, "silence.wav");
            File.WriteAllBytes(tone, Wav(seconds: 6, amplitude: 0.5));
            File.WriteAllBytes(silence, Wav(seconds: 6, amplitude: 0));
            var (toneRose, toneFell, ignoredA) = Play(tone);
            var (silentRose, _, ignoredB) = Play(silence);
            output.WriteLine($"tone: audible rose {(toneRose ? "pass" : "FAIL")}, fell after it ended {(toneFell ? "pass" : "FAIL")}");
            output.WriteLine($"muted run never audible: {(silentRose ? "FAIL" : "pass")}");
            output.WriteLine($"other apps' session samples ignored: {ignoredA + ignoredB}");
            return toneRose && toneFell && !silentRose ? 0 : 1;
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static (bool Rose, bool Fell, int Ignored) Play(string file)
    {
        var self = (uint)Environment.ProcessId;
        var states = new List<bool>();
        var ignored = 0;
        var media = new MediaSessionLogic(s => states.Add(s.Reveal().Audible));
        media.Metadata("tasktor-check", null, null, MediaStatus.Playing, 0);
        var meters = new AudioMeterLogic(pid => pid == self ? "tasktor-check" : null, (app, at, p) => media.Peak(app, at, p), app => media.AudioStopped(app));
        PlaySoundW(file, 0, 0x00020000 | 0x0001); // SND_FILENAME | SND_ASYNC
        var start = DateTimeOffset.UtcNow;
        for (var s = 0; s < 12; s++)
        {
            Thread.Sleep(1000);
            var at = start.AddSeconds(s + 1);
            var sessions = WasapiMeters.Read();
            ignored += sessions.Count(x => x.Pid != self);
            meters.Sample(at, sessions);
            media.Tick(at);
        }

        PlaySoundW(null, 0, 0);
        var rose = states.Contains(true);
        return (rose, rose && states[^1] == false, ignored);
    }

    // 16-bit mono PCM at 44.1 kHz, 440 Hz.
    private static byte[] Wav(int seconds, double amplitude)
    {
        const int rate = 44100;
        var samples = rate * seconds;
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write("RIFF"u8);
        w.Write(36 + (samples * 2));
        w.Write("WAVEfmt "u8);
        w.Write(16);
        w.Write((short)1);
        w.Write((short)1);
        w.Write(rate);
        w.Write(rate * 2);
        w.Write((short)2);
        w.Write((short)16);
        w.Write("data"u8);
        w.Write(samples * 2);
        for (var i = 0; i < samples; i++) w.Write((short)(amplitude * short.MaxValue * Math.Sin(2 * Math.PI * 440 * i / rate)));
        w.Flush();
        return ms.ToArray();
    }

    [LibraryImport("winmm.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int PlaySoundW(string? sound, nint module, uint flags);
}
