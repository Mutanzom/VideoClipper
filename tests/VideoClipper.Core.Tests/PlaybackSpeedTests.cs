using System.Globalization;
using VideoClipper.Core;

namespace VideoClipper.Core.Tests;

public class PlaybackSpeedTests
{
    [Theory]
    [InlineData("0.1", 0.1)]
    [InlineData("0,15", 0.15)]
    [InlineData("1.8x", 1.8)]
    [InlineData(" 2.3 × ", 2.3)]
    [InlineData("2", 2)]
    public void Parses(string text, double expected)
    {
        Assert.True(PlaybackSpeed.TryParse(text, out var s));
        Assert.Equal(expected, s, 6);
    }

    [Theory]
    [InlineData("")]
    [InlineData("fast")]
    [InlineData("0")]
    [InlineData("-1")]
    public void RejectsJunk(string text) => Assert.False(PlaybackSpeed.TryParse(text, out _));

    [Fact]
    public void ValidatesRange()
    {
        Assert.Null(PlaybackSpeed.Validate(0.1));
        Assert.Null(PlaybackSpeed.Validate(16));
        Assert.NotNull(PlaybackSpeed.Validate(0.01));
        Assert.NotNull(PlaybackSpeed.Validate(20));
    }

    [Theory]
    [InlineData(1, "atempo=1")]
    [InlineData(0.5, "atempo=0.5")]
    [InlineData(1.8, "atempo=1.8")]
    [InlineData(0.25, "atempo=0.5,atempo=0.5")]
    [InlineData(5, "atempo=2,atempo=2,atempo=1.25")]
    public void AudioFilterChains(double speed, string expected) =>
        Assert.Equal(expected, PlaybackSpeed.AudioFilter(speed));

    [Theory]
    [InlineData(0.1)]
    [InlineData(0.15)]
    [InlineData(2.3)]
    [InlineData(16)]
    public void AudioChainMultipliesToSpeedWithinLimits(double speed)
    {
        var factors = PlaybackSpeed.AudioFilter(speed).Split(',')
            .Select(f => double.Parse(f["atempo=".Length..], CultureInfo.InvariantCulture)).ToList();
        Assert.All(factors, f => Assert.InRange(f, 0.5, 2));
        Assert.Equal(speed, factors.Aggregate(1.0, (a, b) => a * b), 4);
    }

    [Fact]
    public void FormatIsInvariantAndShort()
    {
        var saved = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("tr-TR");
        try
        {
            Assert.Equal("0.15", PlaybackSpeed.Format(0.15));
            Assert.Equal("1", PlaybackSpeed.Format(1));
            Assert.Equal("setpts=PTS/2.3", PlaybackSpeed.VideoFilter(2.3));
        }
        finally
        {
            CultureInfo.CurrentCulture = saved;
        }
    }

    [Fact]
    public void ClipOutputDuration()
    {
        var clip = new Clip(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(12), 0.1);
        Assert.Equal(TimeSpan.FromSeconds(20), clip.OutputDuration);
        Assert.True(clip.ChangesSpeed);
        Assert.False(new Clip(TimeSpan.Zero, TimeSpan.FromSeconds(1)).ChangesSpeed);
    }
}
