using System.Globalization;
using VideoClipper.Core;

namespace VideoClipper.Core.Tests;

public class TimeFormatTests
{
    [Theory]
    [InlineData("75", 75_000)]
    [InlineData("75.5", 75_500)]
    [InlineData("75,5", 75_500)]
    [InlineData("1:15", 75_000)]
    [InlineData("01:15.250", 75_250)]
    [InlineData("0:01:15.250", 75_250)]
    [InlineData(" 2:00:00 ", 7_200_000)]
    public void Parses(string text, int ms)
    {
        Assert.True(TimeFormat.TryParse(text, out var t));
        Assert.Equal(TimeSpan.FromMilliseconds(ms), t);
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("1:75")]
    [InlineData("1.5:00")]
    [InlineData("1:2:3:4")]
    [InlineData("-5")]
    public void RejectsJunk(string text) => Assert.False(TimeFormat.TryParse(text, out _));

    [Fact]
    public void DisplayIsShortUnderAnHour()
    {
        Assert.Equal("01:15.250", TimeFormat.Display(TimeSpan.FromMilliseconds(75_250)));
        Assert.Equal("1:00:00.000", TimeFormat.Display(TimeSpan.FromHours(1)));
    }

    [Fact]
    public void IgnoresTurkishLocale()
    {
        var saved = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("tr-TR");
        try
        {
            Assert.Equal("1.500", TimeFormat.Ffmpeg(TimeSpan.FromMilliseconds(1500)));
            Assert.Equal("00:01.500", TimeFormat.Display(TimeSpan.FromMilliseconds(1500)));
            Assert.True(TimeFormat.TryParse("1.5", out var t));
            Assert.Equal(TimeSpan.FromMilliseconds(1500), t);
        }
        finally
        {
            CultureInfo.CurrentCulture = saved;
        }
    }
}
