using VideoClipper.Core;

namespace VideoClipper.Core.Tests;

public class FfmpegCommandBuilderTests
{
    private static readonly Clip TenToTwenty = new(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(20.5));

    private static string Join(IReadOnlyList<string> args) => string.Join(" ", args);

    [Fact]
    public void FastIsOneStreamCopyPass()
    {
        var passes = FfmpegCommandBuilder.Build(@"C:\v\in.mkv", @"C:\v\out.mkv", TenToTwenty, new ExportOptions(ExportMode.Fast), "log");

        var cmd = Join(Assert.Single(passes));
        Assert.Contains(@"-ss 10.000 -t 10.500 -i C:\v\in.mkv", cmd);
        Assert.Contains("-c copy", cmd);
        Assert.DoesNotContain("libx264", cmd);
        Assert.EndsWith(@"-n C:\v\out.mkv", cmd);
    }

    [Theory]
    [InlineData(ExportQuality.High, "-crf 18")]
    [InlineData(ExportQuality.Medium, "-crf 23")]
    [InlineData(ExportQuality.Small, "-crf 28")]
    public void ReencodeUsesCrfForQuality(ExportQuality q, string crf)
    {
        var passes = FfmpegCommandBuilder.Build("in.mov", "out.mp4", TenToTwenty, new ExportOptions(ExportMode.Reencode, q), "log");

        var cmd = Join(Assert.Single(passes));
        Assert.Contains("-c:v libx264", cmd);
        Assert.Contains(crf, cmd);
        Assert.Contains("-map 0:a:0?", cmd);
        Assert.DoesNotContain("-pass", cmd);
    }

    [Fact]
    public void SizeLimitIsTwoPassWithoutCrf()
    {
        var passes = FfmpegCommandBuilder.Build("in.mp4", "out.mp4", TenToTwenty, new ExportOptions(ExportMode.Reencode, MaxSizeMb: 10), "C:\\tmp\\log");

        Assert.Equal(2, passes.Count);
        var first = Join(passes[0]);
        var second = Join(passes[1]);
        Assert.Contains("-pass 1 -passlogfile C:\\tmp\\log", first);
        Assert.Contains("-an -f null -y NUL", first);
        Assert.Contains("-pass 2", second);
        Assert.EndsWith("-n out.mp4", second);
        Assert.DoesNotContain("-crf", first + second);
    }

    [Theory]
    [InlineData(ExportMode.Fast, null)]
    [InlineData(ExportMode.Reencode, null)]
    [InlineData(ExportMode.Reencode, 10.0)]
    public void MuteDropsAudio(ExportMode mode, double? maxMb)
    {
        var passes = FfmpegCommandBuilder.Build("in.mp4", "out.mp4", TenToTwenty, new ExportOptions(mode, MaxSizeMb: maxMb, Mute: true), "log");

        var last = Join(passes[^1]);
        Assert.Contains("-an", last);
        Assert.DoesNotContain("-c:a", last);
        Assert.DoesNotContain("0:a", last);
    }

    [Fact]
    public void MutedSizeLimitGivesAudioBudgetToVideo()
    {
        var duration = TimeSpan.FromSeconds(60);
        var (withAudio, _) = FfmpegCommandBuilder.BitratesForSize(10, duration);
        var (muted, audio) = FfmpegCommandBuilder.BitratesForSize(10, duration, withAudio: false);

        Assert.Equal(0, audio);
        int expected = withAudio + FfmpegCommandBuilder.AudioKbps;
        Assert.InRange(muted, expected - 1, expected + 1);
    }

    [Fact]
    public void SizeLimitIgnoredInFastMode()
    {
        Assert.False(new ExportOptions(ExportMode.Fast, MaxSizeMb: 10).HasSizeLimit);
        Assert.False(new ExportOptions(ExportMode.Reencode, MaxSizeMb: 0).HasSizeLimit);
    }

    [Fact]
    public void BitratesFitTheLimit()
    {
        var duration = TimeSpan.FromSeconds(60);
        var (video, audio) = FfmpegCommandBuilder.BitratesForSize(10, duration);

        double sizeMb = (video + audio) * duration.TotalSeconds / 8 / 1024;
        Assert.InRange(sizeMb, 9.0, 10.0);
        Assert.Equal(FfmpegCommandBuilder.AudioKbps, audio);
    }

    [Fact]
    public void TinyLimitShrinksAudioFirst()
    {
        var (video, audio) = FfmpegCommandBuilder.BitratesForSize(1, TimeSpan.FromSeconds(60));
        Assert.True(audio < FfmpegCommandBuilder.AudioKbps);
        Assert.True(video >= audio);
    }

    [Theory]
    [InlineData(ExportMode.Fast, @"C:\a\b.MKV", ".mkv")]
    [InlineData(ExportMode.Fast, @"C:\a\noext", ".mp4")]
    [InlineData(ExportMode.Reencode, @"C:\a\b.mkv", ".mp4")]
    public void OutputExtension(ExportMode mode, string input, string ext) =>
        Assert.Equal(ext, FfmpegCommandBuilder.OutputExtension(input, TenToTwenty, new ExportOptions(mode)));

    [Fact]
    public void SpeedForcesReencodeInFastMode()
    {
        var slow = TenToTwenty with { Speed = 0.25 };
        var options = new ExportOptions(ExportMode.Fast, MaxSizeMb: 2);

        var cmd = Join(Assert.Single(FfmpegCommandBuilder.Build("in.mkv", "out.mp4", slow, options, "log")));
        Assert.Equal(".mp4", FfmpegCommandBuilder.OutputExtension("in.mkv", slow, options));
        Assert.Contains("-c:v libx264", cmd);
        Assert.Contains("-crf 18", cmd); // Fast mode has no quality choice: stay near-lossless
        Assert.Contains("-filter:v setpts=PTS/0.25", cmd);
        Assert.Contains("-filter:a atempo=0.5,atempo=0.5", cmd);
        Assert.DoesNotContain("-c copy", cmd);
        // The input range is still the marked 10.5 s, read as an input limit so the longer output isn't capped.
        Assert.Contains("-t 10.500 -i in.mkv", cmd);
    }

    [Fact]
    public void MutedSpeedClipHasNoAudioFilter()
    {
        var fast = TenToTwenty with { Speed = 2.3 };
        var cmd = Join(Assert.Single(FfmpegCommandBuilder.Build("in.mp4", "out.mp4", fast, new ExportOptions(ExportMode.Reencode, Mute: true), "log")));
        Assert.Contains("setpts=PTS/2.3", cmd);
        Assert.DoesNotContain("atempo", cmd);
    }

    [Fact]
    public void SizeBudgetUsesExportedLength()
    {
        var slow = TenToTwenty with { Speed = 0.5 };
        var passes = FfmpegCommandBuilder.Build("in.mp4", "out.mp4", slow, new ExportOptions(ExportMode.Reencode, MaxSizeMb: 10), "log");

        var (expected, _) = FfmpegCommandBuilder.BitratesForSize(10, TimeSpan.FromSeconds(21));
        Assert.Contains($"-b:v {expected}k", Join(passes[1]));
    }
}
