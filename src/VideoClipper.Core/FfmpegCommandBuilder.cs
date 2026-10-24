namespace VideoClipper.Core;

/// <summary>
/// Builds ffmpeg argument lists (for <see cref="System.Diagnostics.ProcessStartInfo.ArgumentList"/>,
/// so paths never need quoting). A size-limited re-encode is two passes; everything else is one.
/// </summary>
public static class FfmpegCommandBuilder
{
    public const int AudioKbps = 160;

    /// <summary>Kept below the limit to leave room for container overhead and rate-control drift.</summary>
    public const double SizeSafetyFactor = 0.95;

    private const int MinVideoKbps = 50;

    public static int Crf(ExportQuality q) => q switch
    {
        ExportQuality.High => 18,
        ExportQuality.Small => 28,
        _ => 23,
    };

    /// <summary>
    /// Stream copy can't retime frames, so a clip with a speed change is re-encoded even in Fast mode.
    /// </summary>
    public static bool Reencodes(Clip clip, ExportOptions options) => options.Mode == ExportMode.Reencode || clip.ChangesSpeed;

    /// <summary>Stream copy keeps the source container; re-encoding always writes MP4.</summary>
    public static string OutputExtension(string inputPath, Clip clip, ExportOptions options)
    {
        if (Reencodes(clip, options)) return ".mp4";
        var ext = Path.GetExtension(inputPath);
        return string.IsNullOrEmpty(ext) ? ".mp4" : ext.ToLowerInvariant();
    }

    /// <summary>Video and audio bitrates (kbit/s) that make <paramref name="duration"/> fit in <paramref name="maxSizeMb"/>.</summary>
    /// <param name="withAudio">False for muted clips: the whole budget goes to video.</param>
    public static (int VideoKbps, int AudioKbps) BitratesForSize(double maxSizeMb, TimeSpan duration, bool withAudio = true)
    {
        double seconds = Math.Max(duration.TotalSeconds, 0.1);
        double totalKbps = maxSizeMb * 8 * 1024 * SizeSafetyFactor / seconds;
        if (!withAudio) return ((int)Math.Max(totalKbps, MinVideoKbps), 0);
        // Audio takes at most a quarter of the budget, so tiny limits still get watchable video.
        int audio = (int)Math.Clamp(totalKbps / 4, 32, AudioKbps);
        int video = (int)Math.Max(totalKbps - audio, MinVideoKbps);
        return (video, audio);
    }

    /// <param name="passLogPrefix">Where two-pass encoding keeps its stats file; unused otherwise.</param>
    public static IReadOnlyList<IReadOnlyList<string>> Build(
        string input, string output, Clip clip, ExportOptions options, string passLogPrefix)
    {
        var seek = new List<string> { "-hide_banner", "-loglevel", "error", "-nostats", "-progress", "pipe:1" };
        // Input seeking: fast, and frame-exact when re-encoding. -t goes before -i so it limits how much
        // source is read; after -i it would cap the output, cutting a slowed-down clip short.
        seek.AddRange(["-ss", TimeFormat.Ffmpeg(clip.Start), "-t", TimeFormat.Ffmpeg(clip.Duration), "-i", input]);

        if (!Reencodes(clip, options))
        {
            return [[
                .. seek,
                "-map", "0", "-dn", .. (options.Mute ? ["-an"] : Array.Empty<string>()),
                "-c", "copy", "-map_metadata", "0",
                "-avoid_negative_ts", "make_zero",
                "-n", output,
            ]];
        }

        var video = new List<string> { "-map", "0:v:0", "-c:v", "libx264", "-preset", "medium", "-pix_fmt", "yuv420p" };
        if (clip.ChangesSpeed) video.AddRange(["-filter:v", PlaybackSpeed.VideoFilter(clip.Speed)]);

        string[] Audio(int kbps) => options.Mute
            ? ["-an"]
            : [
                "-map", "0:a:0?", "-c:a", "aac", "-b:a", $"{kbps}k",
                .. (clip.ChangesSpeed ? ["-filter:a", PlaybackSpeed.AudioFilter(clip.Speed)] : Array.Empty<string>()),
            ];

        // A speed clip forced out of Fast mode has no quality picked (the box is disabled), so keep it near-lossless.
        bool fastFallback = options.Mode == ExportMode.Fast;
        if (fastFallback || !options.HasSizeLimit)
        {
            return [[
                .. seek, .. video,
                "-crf", Crf(fastFallback ? ExportQuality.High : options.Quality).ToString(),
                .. Audio(AudioKbps),
                "-movflags", "+faststart", "-n", output,
            ]];
        }

        // The budget is spread over the exported length, not the source range.
        var (videoKbps, audioKbps) = BitratesForSize(options.MaxSizeMb!.Value, clip.OutputDuration, withAudio: !options.Mute);
        var rate = new[] { "-b:v", $"{videoKbps}k", "-maxrate", $"{videoKbps * 3 / 2}k", "-bufsize", $"{videoKbps * 2}k" };
        return
        [
            [.. seek, .. video, .. rate, "-pass", "1", "-passlogfile", passLogPrefix, "-an", "-f", "null", "-y", "NUL"],
            [
                .. seek, .. video, .. rate, "-pass", "2", "-passlogfile", passLogPrefix,
                .. Audio(audioKbps),
                "-movflags", "+faststart", "-n", output,
            ],
        ];
    }
}
