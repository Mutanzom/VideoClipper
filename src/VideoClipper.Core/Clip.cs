namespace VideoClipper.Core;

/// <summary>A start/end range inside a video, exported at <paramref name="Speed"/> (1 = normal).</summary>
public readonly record struct Clip(TimeSpan Start, TimeSpan End, double Speed = 1)
{
    /// <summary>Length of the range in the source video.</summary>
    public TimeSpan Duration => End - Start;

    /// <summary>Length of the exported file: 0.5× makes it twice as long.</summary>
    public TimeSpan OutputDuration => Speed > 0 ? Duration / Speed : Duration;

    public bool ChangesSpeed => !PlaybackSpeed.IsNormal(Speed);

    /// <summary>Null when the clip is usable, otherwise why it isn't.</summary>
    public string? Validate(TimeSpan videoDuration)
    {
        if (Start < TimeSpan.Zero) return "Start is before the beginning of the video.";
        if (End <= Start) return "End must be after start.";
        if (videoDuration > TimeSpan.Zero && Start >= videoDuration) return "Start is past the end of the video.";
        return PlaybackSpeed.Validate(Speed);
    }
}
