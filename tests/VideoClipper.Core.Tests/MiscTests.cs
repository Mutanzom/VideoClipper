using VideoClipper.Core;

namespace VideoClipper.Core.Tests;

public class MiscTests
{
    [Fact]
    public void NamingSkipsExistingFiles()
    {
        var taken = new HashSet<string> { @"C:\out\trip_clip02.mp4", @"C:\out\trip_clip02 (2).mp4" };

        Assert.Equal(@"C:\out\trip_clip01.mp4", OutputNaming.ClipPath(@"C:\out", @"D:\v\trip.mov", 1, ".mp4", taken.Contains));
        Assert.Equal(@"C:\out\trip_clip02 (3).mp4", OutputNaming.ClipPath(@"C:\out", @"D:\v\trip.mov", 2, ".mp4", taken.Contains));
    }

    [Theory]
    [InlineData("30000/1001", 29.97)]
    [InlineData("60/1", 60)]
    [InlineData("25", 25)]
    [InlineData("0/0", 0)]
    [InlineData(null, 0)]
    public void ParsesFrameRate(string? rate, double fps) => Assert.Equal(fps, MediaInfo.ParseRate(rate), 2);

    [Fact]
    public void ParsesFfprobeJson()
    {
        const string json = """
            {
              "streams": [
                { "codec_type": "video", "codec_name": "mjpeg", "avg_frame_rate": "0/0", "disposition": { "attached_pic": 1 } },
                { "codec_type": "video", "codec_name": "h264", "avg_frame_rate": "30000/1001", "r_frame_rate": "30000/1001", "disposition": { "attached_pic": 0 } },
                { "codec_type": "audio", "codec_name": "aac" }
              ],
              "format": { "duration": "123.456000" }
            }
            """;

        var info = MediaInfo.Parse(json);
        Assert.Equal(TimeSpan.FromSeconds(123.456), info.Duration);
        Assert.Equal("h264", info.VideoCodec);
        Assert.Equal(29.97, info.Fps, 2);
        Assert.True(info.HasAudio);
    }

    [Theory]
    [InlineData("out_time_us=5000000", 0.5)]
    [InlineData("out_time_ms=2500000", 0.25)]
    [InlineData("out_time_us=99000000", 1)]
    [InlineData("progress=end", 1)]
    public void ParsesProgress(string line, double expected) =>
        Assert.Equal(expected, Ffmpeg.ParseProgressLine(line, TimeSpan.FromSeconds(10)));

    [Fact]
    public void IgnoresOtherProgressLines()
    {
        Assert.Null(Ffmpeg.ParseProgressLine("frame=12", TimeSpan.FromSeconds(10)));
        Assert.Null(Ffmpeg.ParseProgressLine("out_time_us=N/A", TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public void ClipValidation()
    {
        var len = TimeSpan.FromSeconds(30);
        Assert.Null(new Clip(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2)).Validate(len));
        Assert.NotNull(new Clip(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2)).Validate(len));
        Assert.NotNull(new Clip(TimeSpan.FromSeconds(31), TimeSpan.FromSeconds(32)).Validate(len));
    }
}
