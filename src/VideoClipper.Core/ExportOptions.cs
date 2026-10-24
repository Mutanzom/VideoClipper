namespace VideoClipper.Core;

public enum ExportMode
{
    /// <summary>Stream copy: instant and lossless, but the cut snaps to the keyframe before the start.</summary>
    Fast,
    /// <summary>H.264/AAC MP4: frame-exact, optionally squeezed under a size limit.</summary>
    Reencode,
}

public enum ExportQuality
{
    High,
    Medium,
    Small,
}

/// <param name="Mute">Drop the audio from the exported clips.</param>
public sealed record ExportOptions(ExportMode Mode, ExportQuality Quality = ExportQuality.Medium, double? MaxSizeMb = null, bool Mute = false)
{
    public bool HasSizeLimit => Mode == ExportMode.Reencode && MaxSizeMb is > 0;
}
