using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;
using VideoClipper.Core;

namespace VideoClipper.App.ViewModels;

public enum ClipState
{
    Ready,
    Queued,
    Exporting,
    Done,
    Failed,
    Cancelled,
}

/// <summary>One row of the clip list.</summary>
public sealed partial class ClipItem : ObservableObject
{
    [ObservableProperty] private int _number;
    [ObservableProperty] private TimeSpan _start;
    [ObservableProperty] private TimeSpan _end;
    [ObservableProperty] private double _speed = 1;
    [ObservableProperty] private ClipState _state;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private string? _message;

    public ClipItem(TimeSpan start, TimeSpan end)
    {
        _start = start;
        _end = end;
    }

    public Clip Clip => new(Start, End, Speed);

    public string StartText => TimeFormat.Display(Start);
    public string EndText => TimeFormat.Display(End);
    /// <summary>Length of the exported file, so a 0.5× clip shows twice its marked range.</summary>
    public string DurationText => TimeFormat.Display(Clip.OutputDuration);
    public string SpeedText => PlaybackSpeed.Format(Speed);
    public string DurationToolTip => Clip.ChangesSpeed
        ? $"Exported length at {SpeedText}× (marked range {TimeFormat.Display(End - Start)})"
        : "Exported length";
    public string Title => $"Clip {Number}";

    public Visibility ProgressVisibility => State is ClipState.Exporting ? Visibility.Visible : Visibility.Collapsed;

    public string StatusText => State switch
    {
        ClipState.Queued => "Waiting…",
        // Not "P0": the Turkish locale writes it as "%50".
        ClipState.Exporting => $"{(int)(Progress * 100)}%",
        ClipState.Done => "✓ Saved",
        ClipState.Failed => "Failed",
        ClipState.Cancelled => "Cancelled",
        _ => "",
    };

    partial void OnNumberChanged(int value) => OnPropertyChanged(nameof(Title));

    partial void OnStartChanged(TimeSpan value) => TimesChanged();
    partial void OnEndChanged(TimeSpan value) => TimesChanged();
    partial void OnSpeedChanged(double value) => TimesChanged();

    partial void OnStateChanged(ClipState value)
    {
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(ProgressVisibility));
    }

    partial void OnProgressChanged(double value) => OnPropertyChanged(nameof(StatusText));

    private void TimesChanged()
    {
        OnPropertyChanged(nameof(StartText));
        OnPropertyChanged(nameof(EndText));
        OnPropertyChanged(nameof(DurationText));
        OnPropertyChanged(nameof(DurationToolTip));
        OnPropertyChanged(nameof(SpeedText));
        // Editing a finished clip makes it a new clip as far as export is concerned.
        if (State is ClipState.Done or ClipState.Failed or ClipState.Cancelled) State = ClipState.Ready;
    }
}
