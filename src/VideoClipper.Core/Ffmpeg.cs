using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace VideoClipper.Core;

public sealed class FfmpegException(string message) : Exception(message);

/// <summary>Finds and runs ffmpeg/ffprobe.</summary>
public sealed class Ffmpeg
{
    public string FfmpegPath { get; }
    public string FfprobePath { get; }

    private Ffmpeg(string ffmpeg, string ffprobe)
    {
        FfmpegPath = ffmpeg;
        FfprobePath = ffprobe;
    }

    /// <summary>
    /// Looks in <c>tools\ffmpeg</c> next to the app, then next to the app itself, then on PATH.
    /// Null when either executable is missing.
    /// </summary>
    public static Ffmpeg? Locate(string appDirectory)
    {
        var dirs = new List<string> { Path.Combine(appDirectory, "tools", "ffmpeg"), appDirectory };
        dirs.AddRange((Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries));

        foreach (var dir in dirs)
        {
            try
            {
                var ffmpeg = Path.Combine(dir.Trim('"'), "ffmpeg.exe");
                var ffprobe = Path.Combine(dir.Trim('"'), "ffprobe.exe");
                if (File.Exists(ffmpeg) && File.Exists(ffprobe)) return new Ffmpeg(ffmpeg, ffprobe);
            }
            catch (ArgumentException)
            {
                // Malformed PATH entry.
            }
        }
        return null;
    }

    public async Task<MediaInfo> ProbeAsync(string input, CancellationToken ct = default)
    {
        var (exit, stdout, stderr) = await RunAsync(FfprobePath,
            ["-v", "error", "-show_format", "-show_streams", "-of", "json", input], null, ct);
        if (exit != 0) throw new FfmpegException(FirstLine(stderr) ?? "ffprobe could not read this file.");
        return MediaInfo.Parse(stdout);
    }

    /// <summary>
    /// Exports one clip. On failure or cancellation the partial output file is deleted.
    /// </summary>
    /// <param name="progress">0..1 across all passes.</param>
    public async Task ExportClipAsync(string input, string output, Clip clip, ExportOptions options,
        IProgress<double>? progress, CancellationToken ct)
    {
        var passLog = Path.Combine(Path.GetTempPath(), $"videoclipper-{Guid.NewGuid():N}");
        var passes = FfmpegCommandBuilder.Build(input, output, clip, options, passLog);
        // "-n" makes ffmpeg refuse an existing file; that file is not ours to clean up.
        bool existedBefore = File.Exists(output);
        try
        {
            for (int i = 0; i < passes.Count; i++)
            {
                int pass = i;
                var passProgress = progress == null ? null : new Progress<double>(p => progress.Report((pass + p) / passes.Count));
                var (exit, _, stderr) = await RunAsync(FfmpegPath, passes[i],
                    line => ReportProgress(line, clip.OutputDuration, passProgress), ct);
                if (exit != 0) throw new FfmpegException(FirstLine(stderr) ?? $"ffmpeg exited with code {exit}.");
            }
            progress?.Report(1);
        }
        catch
        {
            if (!existedBefore) TryDelete(output);
            throw;
        }
        finally
        {
            foreach (var f in Directory.EnumerateFiles(Path.GetTempPath(), Path.GetFileName(passLog) + "*")) TryDelete(f);
        }
    }

    /// <summary>Parses a <c>-progress</c> line such as <c>out_time_us=1500000</c>.</summary>
    public static double? ParseProgressLine(string line, TimeSpan duration)
    {
        if (line == "progress=end") return 1;
        // out_time_ms is also microseconds (a long-standing ffmpeg naming bug).
        foreach (var key in new[] { "out_time_us=", "out_time_ms=" })
        {
            if (line.StartsWith(key, StringComparison.Ordinal)
                && long.TryParse(line.AsSpan(key.Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out long us)
                && duration > TimeSpan.Zero)
            {
                return Math.Clamp(us / 1_000_000.0 / duration.TotalSeconds, 0, 1);
            }
        }
        return null;
    }

    private static void ReportProgress(string line, TimeSpan duration, IProgress<double>? progress)
    {
        if (progress != null && ParseProgressLine(line, duration) is double p) progress.Report(p);
    }

    private static async Task<(int Exit, string Stdout, string Stderr)> RunAsync(
        string exe, IReadOnlyList<string> args, Action<string>? onStdoutLine, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        proc.OutputDataReceived += (_, e) =>
        {
            if (e.Data == null) return;
            if (onStdoutLine != null) onStdoutLine(e.Data);
            else stdout.AppendLine(e.Data);
        };
        proc.ErrorDataReceived += (_, e) => { if (e.Data != null) stderr.AppendLine(e.Data); };

        proc.Start();
        proc.StandardInput.Close();
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        try
        {
            await proc.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            await proc.WaitForExitAsync(CancellationToken.None);
            throw;
        }
        // The no-arg wait drains the redirected streams.
        proc.WaitForExit();
        return (proc.ExitCode, stdout.ToString(), stderr.ToString());
    }

    private static string? FirstLine(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
