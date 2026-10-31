using System.Media;
using System.Windows.Media;

namespace LocalBell.App.Services;

/// <summary>
/// The ring sounds, made by the app (no sound file to ship, no rights to clear): a bell is a few sine waves that fade,
/// at frequencies that are not multiples of each other. The volume is applied to the samples, so it stays apart from
/// the volume of Windows. The user's own file (WAV or MP3) plays through the WPF media player, at the same volume.
/// </summary>
public static class BellSound
{
    public const string CounterBell = "counter-bell";
    public const string DoubleDing = "double-ding";
    public const string Chime = "chime";
    public const string MusicBox = "music-box";

    /// <summary>The user's own file (<see cref="AppSettings.SoundFile"/>).</summary>
    public const string OwnFile = "file";

    /// <summary>The sounds made by the app, with the key of their name.</summary>
    public static readonly (string Sound, string Key)[] BuiltIn =
        [(CounterBell, "SoundCounterBell"), (DoubleDing, "SoundDoubleDing"), (Chime, "SoundChime"), (MusicBox, "SoundMusicBox")];

    private const int Rate = 44100;

    // One stroke of a counter bell: its partials (ratio to the fundamental, loudness, how fast it fades).
    private static readonly (double Ratio, double Level, double Fade)[] BellPartials =
        [(1, 1, 3.2), (2.76, 0.45, 4.8), (5.40, 0.22, 7.5), (8.93, 0.10, 11)];

    // A softer, nearly harmonic tone for the chime.
    private static readonly (double Ratio, double Level, double Fade)[] ChimePartials =
        [(1, 1, 2.2), (2, 0.28, 3.5), (3, 0.08, 5)];

    // A music box tine: nearly a pure tone, a faint octave, a high ping that dies at once.
    private static readonly (double Ratio, double Level, double Fade)[] TinePartials =
        [(1, 1, 3.5), (2, 0.18, 6), (5.4, 0.12, 14)];

    // The music box's tune, a falling line in A minor (Hz). Halloween, the day the app was made (October 31, 2026).
    private static readonly double[] MusicBoxTune = [1318.5, 1046.5, 987.8, 830.6, 880.0, 659.3];

    private static SoundPlayer? player;
    private static MediaPlayer? media;

    /// <summary>
    /// The user's file is open and its length known (<see cref="Length"/> said 3 s until then): the bell waits for the
    /// whole file before ringing again (a longer file was cut after 4 s, October 31).
    /// </summary>
    public static event EventHandler<TimeSpan>? FileLengthKnown;

    /// <summary>How long the sound lasts (a file: its length once opened, else 3 s).</summary>
    public static TimeSpan Length(string sound) => sound switch
    {
        Chime => TimeSpan.FromSeconds(1.9),
        MusicBox => TimeSpan.FromSeconds(3),
        OwnFile => media?.NaturalDuration is { HasTimeSpan: true } d ? d.TimeSpan : TimeSpan.FromSeconds(3),
        _ => TimeSpan.FromSeconds(1.3),
    };

    /// <summary>Plays the sound once, at a volume from 0 to 100; it stops the one playing.</summary>
    public static void Play(string sound, string? file, int volume)
    {
        Stop();
        if (volume <= 0) return;
        if (sound == OwnFile && File.Exists(file))
        {
            var opened = media = new MediaPlayer { Volume = Loudness(volume) };
            opened.MediaOpened += (_, _) =>
            {
                if (ReferenceEquals(media, opened) && opened.NaturalDuration.HasTimeSpan)
                    FileLengthKnown?.Invoke(null, opened.NaturalDuration.TimeSpan);
            };
            opened.Open(new Uri(file));
            opened.Play();
            return;
        }
        player = new SoundPlayer(new MemoryStream(Wav(sound, volume)));
        try { player.Play(); }
        catch (InvalidOperationException) { } // no sound device
    }

    public static void Stop()
    {
        player?.Stop();
        player?.Dispose();
        player = null;
        media?.Stop();
        media?.Close();
        media = null;
    }

    // Loudness grows faster than the slider at the bottom of it, as volume sliders do.
    private static double Loudness(int volume) => Math.Pow(Math.Clamp(volume, 0, 100) / 100.0, 1.6);

    /// <summary>The sound as a 16-bit mono WAV file.</summary>
    public static byte[] Wav(string sound, int volume)
    {
        var samples = new double[(int)(Length(sound == OwnFile ? CounterBell : sound).TotalSeconds * Rate)];
        switch (sound)
        {
            case DoubleDing:
                Strike(samples, 0, 1568, 1, BellPartials);
                Strike(samples, 0.16, 1568, 0.9, BellPartials);
                break;
            case Chime:
                Strike(samples, 0, 1318.5, 1, ChimePartials);
                Strike(samples, 0.55, 1046.5, 1, ChimePartials);
                break;
            case MusicBox:
                // Each note a little later and a little lower than the last: a spring running down.
                double at = 0, gap = 0.26, pitch = 1;
                foreach (double note in MusicBoxTune)
                {
                    Strike(samples, at, note * pitch, 0.8, TinePartials);
                    Strike(samples, at + 0.004, note * pitch * 1.004, 0.25, TinePartials); // a second tine, a hair off tune
                    at += gap;
                    gap *= 1.17;
                    pitch *= 0.992;
                }
                Echo(samples, 0.09, 0.3);
                break;
            default:
                Strike(samples, 0, 1568, 1, BellPartials);
                break;
        }

        double peak = samples.Max(Math.Abs);
        double gain = peak > 0 ? 0.9 / peak * Loudness(volume) : 0;
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.ASCII, leaveOpen: true))
        {
            int dataBytes = samples.Length * 2;
            writer.Write("RIFF"u8);
            writer.Write(36 + dataBytes);
            writer.Write("WAVEfmt "u8);
            writer.Write(16);
            writer.Write((short)1); // PCM
            writer.Write((short)1); // mono
            writer.Write(Rate);
            writer.Write(Rate * 2);
            writer.Write((short)2);
            writer.Write((short)16);
            writer.Write("data"u8);
            writer.Write(dataBytes);
            foreach (double sample in samples) writer.Write((short)Math.Round(sample * gain * short.MaxValue));
        }
        return stream.ToArray();
    }

    private static void Strike(double[] samples, double atSeconds, double frequency, double level,
        (double Ratio, double Level, double Fade)[] partials)
    {
        int start = (int)(atSeconds * Rate);
        for (int i = start; i < samples.Length; i++)
        {
            double t = (double)(i - start) / Rate;
            double attack = Math.Min(1, t / 0.003); // 3 ms, no click
            double value = 0;
            foreach (var (ratio, partialLevel, fade) in partials)
                value += partialLevel * Math.Exp(-fade * t) * Math.Sin(2 * Math.PI * frequency * ratio * t);
            samples[i] += level * attack * value;
        }
    }

    // A faint echo of the room, which feeds on itself.
    private static void Echo(double[] samples, double delaySeconds, double level)
    {
        int delay = (int)(delaySeconds * Rate);
        for (int i = delay; i < samples.Length; i++) samples[i] += level * samples[i - delay];
    }
}
