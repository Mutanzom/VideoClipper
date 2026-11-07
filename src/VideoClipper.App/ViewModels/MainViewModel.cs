using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using VideoClipper.App.Services;
using VideoClipper.Core;

namespace VideoClipper.App.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    public static readonly string[] VideoExtensions =
        [".mp4", ".mkv", ".mov", ".avi", ".webm", ".m4v", ".wmv", ".ts", ".m2ts", ".mts", ".flv", ".3gp"];

    public static string[] QualityLabels { get; } = ["High quality", "Medium quality", "Small file"];

    public ObservableCollection<ClipItem> Clips { get; } = [];

    [ObservableProperty] private string? _videoPath;
    [ObservableProperty] private MediaInfo? _info;
    [ObservableProperty] private TimeSpan _position;
    [ObservableProperty] private TimeSpan? _pendingStart;
    [ObservableProperty] private string? _outputFolder;
    [ObservableProperty] private bool _reencode;
    [ObservableProperty] private int _qualityIndex = 1;
    [ObservableProperty] private double _maxSizeMb = double.NaN;
    [ObservableProperty] private bool _mute;
    [ObservableProperty] private bool _isExporting;

    public MainViewModel(AppSettings settings)
    {
        _reencode = settings.Reencode;
        _mute = settings.Mute;
        _qualityIndex = Math.Clamp(settings.QualityIndex, 0, QualityLabels.Length - 1);
        _maxSizeMb = settings.MaxSizeMb ?? double.NaN;
        Clips.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasClips));
            OnPropertyChanged(nameof(CanExport));
            OnPropertyChanged(nameof(ClipCountText));
        };
    }

    public void SaveTo(AppSettings settings)
    {
        settings.Reencode = Reencode;
        settings.Mute = Mute;
        settings.QualityIndex = QualityIndex;
        settings.MaxSizeMb = double.IsNaN(MaxSizeMb) || MaxSizeMb <= 0 ? null : MaxSizeMb;
    }

    public TimeSpan Duration => Info?.Duration ?? TimeSpan.Zero;
    public bool HasVideo => VideoPath != null;
    public bool HasClips => Clips.Count > 0;
    public string FileName => VideoPath == null ? "No video open" : Path.GetFileName(VideoPath);
    public string PositionText => $"{TimeFormat.Display(Position)} / {TimeFormat.Display(Duration)}";
    public string OutputFolderText => OutputFolder ?? "";
    public string ClipCountText => Clips.Count == 1 ? "1 clip" : $"{Clips.Count} clips";
    public bool CanExport => HasClips && !IsExporting;
    public bool CanEditSettings => !IsExporting;
    public bool CanEditReencodeSettings => Reencode && !IsExporting;

    public string MarkHint => PendingStart is TimeSpan s
        ? $"Start marked at {TimeFormat.Display(s)}. Move to the end and press R (or Mark end)."
        : HasVideo ? "Press E (or Mark start) at the first frame of a clip." : "";

    public ExportOptions Options => new(
        Reencode ? ExportMode.Reencode : ExportMode.Fast,
        (ExportQuality)QualityIndex,
        double.IsNaN(MaxSizeMb) || MaxSizeMb <= 0 ? null : MaxSizeMb,
        Mute);

    partial void OnVideoPathChanged(string? value)
    {
        OnPropertyChanged(nameof(HasVideo));
        OnPropertyChanged(nameof(FileName));
        OnPropertyChanged(nameof(MarkHint));
    }

    partial void OnInfoChanged(MediaInfo? value)
    {
        OnPropertyChanged(nameof(Duration));
        OnPropertyChanged(nameof(PositionText));
    }

    partial void OnPositionChanged(TimeSpan value) => OnPropertyChanged(nameof(PositionText));
    partial void OnPendingStartChanged(TimeSpan? value) => OnPropertyChanged(nameof(MarkHint));
    partial void OnOutputFolderChanged(string? value) => OnPropertyChanged(nameof(OutputFolderText));
    partial void OnReencodeChanged(bool value) => OnPropertyChanged(nameof(CanEditReencodeSettings));

    partial void OnIsExportingChanged(bool value)
    {
        OnPropertyChanged(nameof(CanEditSettings));
        OnPropertyChanged(nameof(CanEditReencodeSettings));
        OnPropertyChanged(nameof(CanExport));
    }

    public void LoadVideo(string path, MediaInfo info)
    {
        Clips.Clear();
        PendingStart = null;
        Position = TimeSpan.Zero;
        Info = info;
        VideoPath = path;
        OutputFolder = Path.GetDirectoryName(path);
    }

    public void MarkStart() => PendingStart = Position;

    /// <summary>Closes the pending clip at the playhead. Returns an error message, or null.</summary>
    /// <param name="speed">Export speed for the new clip: the speed it was being watched at.</param>
    public string? MarkEnd(double speed = 1)
    {
        if (PendingStart is not TimeSpan start) return "Mark a start point first (E).";
        var clip = new Clip(start, Position, speed);
        if (clip.Validate(Duration) is string error) return error;

        AddClip(new ClipItem(clip.Start, clip.End) { Speed = clip.Speed });
        PendingStart = null;
        return null;
    }

    public void CancelPending() => PendingStart = null;

    public void Remove(ClipItem item)
    {
        Clips.Remove(item);
        Renumber();
    }

    /// <summary>Applies an edited start or end time. Returns an error message, or null.</summary>
    public string? Edit(ClipItem item, bool isStart, string text)
    {
        if (!TimeFormat.TryParse(text, out var t)) return "Use a time like 1:05.250 or 65.25.";
        var clip = isStart ? new Clip(t, item.End) : new Clip(item.Start, t);
        if (clip.Validate(Duration) is string error) return error;

        item.Start = clip.Start;
        item.End = Duration > TimeSpan.Zero && clip.End > Duration ? Duration : clip.End;
        Reorder(item);
        return null;
    }

    /// <summary>The shortest clip a timeline drag can leave: one frame.</summary>
    private TimeSpan MinClipLength => TimeSpan.FromSeconds(Info is { Fps: > 0 } info ? 1.0 / info.Fps : 0.05);

    /// <summary>
    /// Moves one edge of a clip to <paramref name="t"/> (timeline drag), clamped to the video and so the
    /// clip keeps at least a frame. The list isn't re-sorted until <see cref="Reorder"/>, so the clip
    /// doesn't jump rows under the pointer mid-drag.
    /// </summary>
    public void DragEdge(ClipItem item, bool isStart, TimeSpan t)
    {
        if (isStart)
        {
            item.Start = Clamp(t, TimeSpan.Zero, item.End - MinClipLength);
        }
        else
        {
            var max = Duration > TimeSpan.Zero ? Duration : TimeSpan.MaxValue;
            item.End = Clamp(t, item.Start + MinClipLength, max);
        }
    }

    private static TimeSpan Clamp(TimeSpan t, TimeSpan min, TimeSpan max) =>
        t > max ? (max < min ? min : max) : t < min ? min : t;

    /// <summary>Puts a clip back in time order after its start moved.</summary>
    public void Reorder(ClipItem item)
    {
        int i = Clips.IndexOf(item);
        if (i < 0) return;
        bool inOrder = (i == 0 || Clips[i - 1].Start <= item.Start) && (i == Clips.Count - 1 || item.Start <= Clips[i + 1].Start);
        if (inOrder) return;
        Clips.Remove(item);
        AddClip(item);
    }

    /// <summary>Applies an edited export speed. Returns an error message, or null.</summary>
    public string? EditSpeed(ClipItem item, string text)
    {
        if (!PlaybackSpeed.TryParse(text, out var speed)) return "Type a speed like 0.25, 1.5 or 2.";
        if (PlaybackSpeed.Validate(speed) is string error) return error;
        item.Speed = speed;
        return null;
    }

    /// <summary>Clips stay in time order so clip01, clip02… follow the video.</summary>
    private void AddClip(ClipItem item)
    {
        int i = 0;
        while (i < Clips.Count && Clips[i].Start <= item.Start) i++;
        Clips.Insert(i, item);
        Renumber();
    }

    private void Renumber()
    {
        for (int i = 0; i < Clips.Count; i++) Clips[i].Number = i + 1;
    }

    /// <summary>Exports every clip in order. Returns how many were saved.</summary>
    public async Task<(int Saved, int Failed)> ExportAllAsync(Ffmpeg ffmpeg, CancellationToken ct)
    {
        if (VideoPath == null || OutputFolder == null) return (0, 0);
        var options = Options;
        var items = Clips.ToList();
        foreach (var item in items)
        {
            item.State = ClipState.Queued;
            item.Progress = 0;
            item.Message = null;
        }

        IsExporting = true;
        int saved = 0, failed = 0;
        try
        {
            Directory.CreateDirectory(OutputFolder);
            foreach (var item in items)
            {
                if (ct.IsCancellationRequested)
                {
                    item.State = ClipState.Cancelled;
                    continue;
                }

                var ext = FfmpegCommandBuilder.OutputExtension(VideoPath, item.Clip, options);
                var output = OutputNaming.ClipPath(OutputFolder, VideoPath, item.Number, ext);
                item.State = ClipState.Exporting;
                try
                {
                    await ffmpeg.ExportClipAsync(VideoPath, output, item.Clip, options,
                        new Progress<double>(p => item.Progress = p), ct);
                    item.State = ClipState.Done;
                    item.Message = output;
                    saved++;
                }
                catch (OperationCanceledException)
                {
                    item.State = ClipState.Cancelled;
                }
                catch (Exception e)
                {
                    item.State = ClipState.Failed;
                    item.Message = e.Message;
                    failed++;
                }
            }
        }
        finally
        {
            IsExporting = false;
        }
        return (saved, failed);
    }
}
