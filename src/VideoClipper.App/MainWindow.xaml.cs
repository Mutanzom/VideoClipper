using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using VideoClipper.App.Services;
using VideoClipper.App.ViewModels;
using VideoClipper.Core;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.System;
using Windows.UI.Core;
using WinRT.Interop;
using DispatcherQueueTimer = Microsoft.UI.Dispatching.DispatcherQueueTimer;

namespace VideoClipper.App;

public sealed partial class MainWindow : Window
{
    public MainViewModel ViewModel { get; }

    private readonly AppSettings _settings;
    private readonly IntPtr _hwnd;
    private readonly Ffmpeg? _ffmpeg;
    private readonly MediaPlayer _player = new() { AutoPlay = false };
    private readonly DispatcherQueueTimer _tick;
    private readonly DispatcherQueueTimer _noticeTimer;

    // Timeline shapes that move every tick; the clip ranges are rebuilt in RedrawTimeline.
    private readonly Rectangle _playhead = new() { Width = 2, IsHitTestVisible = false };
    private readonly Rectangle _pendingRange = new() { IsHitTestVisible = false, Opacity = 0.35 };
    private bool _scrubbing;

    /// <summary>Set by "play this clip": playback pauses when it reaches this point.</summary>
    private TimeSpan? _stopAt;

    /// <summary>Speed for watching, from the speed box. Clip previews use the clip's own speed.</summary>
    private double _viewSpeed = 1;

    /// <summary>
    /// The player's position for the video's first frame. Media Foundation ignores MP4 edit lists,
    /// so B-frame H.264 files (most phone/OBS recordings) start at e.g. 0.066 s in the player but
    /// at 0 in ffmpeg. All times in the view model are ffmpeg times; convert at the player boundary.
    /// </summary>
    private TimeSpan _origin;
    private DateTime? _originMeasureAt;

    private CancellationTokenSource? _exportCts;
    private Task? _exportTask;
    private bool _closeAfterExport;

    public MainWindow(string[] args)
    {
        _settings = AppSettings.Load();
        ViewModel = new MainViewModel(_settings);
        InitializeComponent();

        _noticeTimer = DispatcherQueue.CreateTimer();
        _noticeTimer.IsRepeating = false;
        _noticeTimer.Tick += (_, _) => Notice.IsOpen = false;

        _hwnd = WindowNative.GetWindowHandle(this);
        SetUpChrome();

        _ffmpeg = Ffmpeg.Locate(AppContext.BaseDirectory);
        if (_ffmpeg == null)
        {
            ShowError("FFmpeg not found",
                $"Put ffmpeg.exe and ffprobe.exe in {System.IO.Path.Combine(AppContext.BaseDirectory, "tools", "ffmpeg")} or on PATH, then restart.");
        }

        Player.SetMediaPlayer(_player);
        _player.MediaFailed += (_, e) => DispatcherQueue.TryEnqueue(() =>
            ShowNotice(InfoBarSeverity.Warning, "Windows can't preview this video",
                $"{e.ErrorMessage} You can still mark times by typing them into a clip. For HEVC/H.265 videos, install \"HEVC Video Extensions\" from the Microsoft Store."));
        _player.MediaOpened += (_, _) => DispatcherQueue.TryEnqueue(() =>
        {
            // ffprobe can't always tell the length (some .ts/.webm files); the player can.
            if (ViewModel.Info is { Duration.Ticks: 0 } info)
            {
                ViewModel.Info = info with { Duration = _player.PlaybackSession.NaturalDuration };
                RedrawTimeline();
            }
            // A freshly opened video sits on its first frame; once the player has settled there
            // (see OnTick), where it says it is becomes the origin.
            _originMeasureAt = DateTime.UtcNow + TimeSpan.FromMilliseconds(400);
            SetRate(_viewSpeed); // a new source starts at 1×
        });

        (ReencodeMode.IsChecked, FastMode.IsChecked) = (ViewModel.Reencode, !ViewModel.Reencode);

        _tick = DispatcherQueue.CreateTimer();
        _tick.Interval = TimeSpan.FromMilliseconds(30);
        _tick.Tick += (_, _) => OnTick();
        _tick.Start();

        _playhead.Fill = Brush("TextFillColorPrimaryBrush");
        _pendingRange.Fill = Brush("SystemFillColorCautionBrush");
        ViewModel.Clips.CollectionChanged += Clips_CollectionChanged;
        ViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(MainViewModel.PendingStart) or nameof(MainViewModel.Duration)) RedrawTimeline();
        };

        Root.PreviewKeyDown += Root_PreviewKeyDown;
        Root.Loaded += async (_, _) =>
        {
            RedrawTimeline();
            if (args.FirstOrDefault(File.Exists) is string path) await OpenAsync(path);
        };
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    private double Scale => GetDpiForWindow(_hwnd) / 96.0;

    private void SetUpChrome()
    {
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        SystemBackdrop = new MicaBackdrop();

        var icon = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
        if (File.Exists(icon)) AppWindow.SetIcon(icon);

        var size = new SizeInt32((int)(_settings.WindowWidth * Scale), (int)(_settings.WindowHeight * Scale));
        var work = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest).WorkArea;
        size.Width = Math.Min(size.Width, work.Width);
        size.Height = Math.Min(size.Height, work.Height);
        AppWindow.MoveAndResize(new RectInt32(work.X + (work.Width - size.Width) / 2, work.Y + (work.Height - size.Height) / 2, size.Width, size.Height));
        if (AppWindow.Presenter is OverlappedPresenter p)
        {
            p.PreferredMinimumWidth = (int)(1000 * Scale);
            p.PreferredMinimumHeight = (int)(620 * Scale);
            if (_settings.WindowMaximized) p.Maximize();
        }

        AppWindow.Closing += AppWindow_Closing;
    }

    private async void AppWindow_Closing(AppWindow sender, AppWindowClosingEventArgs e)
    {
        if (_exportTask is { IsCompleted: false } task)
        {
            // Stop ffmpeg and let the export remove its half-written file before the window goes.
            e.Cancel = true;
            if (_closeAfterExport) return;
            _closeAfterExport = true;
            _exportCts?.Cancel();
            await task;
            Close();
            return;
        }

        // Don't dispose the player here: pulling it out from under the MediaPlayerElement while
        // the close is in progress made the first click on X do nothing. The process exit frees it.
        _tick.Stop();
        _player.Pause();
        ViewModel.SaveTo(_settings);
        if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Maximized })
        {
            _settings.WindowMaximized = true;
        }
        else
        {
            _settings.WindowMaximized = false;
            _settings.WindowWidth = (int)(AppWindow.Size.Width / Scale);
            _settings.WindowHeight = (int)(AppWindow.Size.Height / Scale);
        }
        _settings.Save();
    }

    /// <summary>For x:Bind: visible when the condition is false.</summary>
    public static Visibility Hide(bool condition) => condition ? Visibility.Collapsed : Visibility.Visible;

    private static Brush Brush(string key) => (Brush)Application.Current.Resources[key];

    // ================================================================== notices

    public void ShowError(string title, string message) => ShowNotice(InfoBarSeverity.Error, title, message);

    private void ShowNotice(InfoBarSeverity severity, string title, string message, ButtonBase? action = null)
    {
        Notice.Severity = severity;
        Notice.Title = title;
        Notice.Message = message;
        Notice.ActionButton = action;
        Notice.IsOpen = true;

        // It covers the top of the video, so anything but an error goes away on its own.
        _noticeTimer.Stop();
        if (severity != InfoBarSeverity.Error)
        {
            _noticeTimer.Interval = TimeSpan.FromSeconds(action != null ? 10 : 5);
            _noticeTimer.Start();
        }
    }

    // ================================================================== focus

    /// <summary>
    /// Clicking empty background doesn't move focus in WinUI, so a time box or the size box would
    /// keep swallowing the E/R/Space shortcuts. When a click lands outside the focused text field
    /// (and outside any open popup), park focus on an invisible element.
    /// </summary>
    private void Root_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var focused = FocusManager.GetFocusedElement(Content.XamlRoot) as DependencyObject;
        if (FindAncestor<TextBox>(focused) is not { } textBox) return;
        DependencyObject field = FindAncestor<NumberBox>(textBox) as DependencyObject ?? textBox;

        var source = e.OriginalSource as DependencyObject;
        if (IsWithin(source, field)) return;
        foreach (var popup in VisualTreeHelper.GetOpenPopupsForXamlRoot(Content.XamlRoot))
            if (IsWithin(source, popup.Child)) return;

        ParkFocus();
    }

    private void ParkFocus()
    {
        FocusSink.IsTabStop = true;
        FocusSink.Focus(FocusState.Programmatic);
    }

    private static T? FindAncestor<T>(DependencyObject? node) where T : DependencyObject
    {
        for (; node != null; node = VisualTreeHelper.GetParent(node))
            if (node is T match) return match;
        return null;
    }

    private static bool IsWithin(DependencyObject? node, DependencyObject? container)
    {
        if (container == null) return false;
        for (; node != null; node = VisualTreeHelper.GetParent(node))
            if (ReferenceEquals(node, container)) return true;
        return false;
    }

    // ================================================================== keyboard

    private static bool IsDown(VirtualKey key) =>
        InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(CoreVirtualKeyStates.Down);

    private async void Root_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        bool ctrl = IsDown(VirtualKey.Control);
        if (ctrl && e.Key == VirtualKey.O)
        {
            e.Handled = true;
            if (ViewModel.CanEditSettings) await PickVideoAsync();
            return;
        }

        // Typing in a time box or picking from a list keeps its keys.
        var focused = FocusManager.GetFocusedElement(Content.XamlRoot) as DependencyObject;
        if (FindAncestor<TextBox>(focused) != null || FindAncestor<ComboBox>(focused) != null || focused is ComboBoxItem) return;
        if (!ViewModel.HasVideo || ctrl) return;

        bool shift = IsDown(VirtualKey.Shift);
        switch (e.Key)
        {
            case VirtualKey.Space: TogglePlay(); break;
            case VirtualKey.Left when shift: SeekBy(TimeSpan.FromSeconds(-1)); break;
            case VirtualKey.Right when shift: SeekBy(TimeSpan.FromSeconds(1)); break;
            case VirtualKey.Left: StepFrame(forward: false); break;
            case VirtualKey.Right: StepFrame(forward: true); break;
            case VirtualKey.Home: Seek(TimeSpan.Zero); break;
            case VirtualKey.End: Seek(ViewModel.Duration); break;
            // Handled here, while the key tunnels down, not on CharacterReceived: a focused clip row
            // (after clicking one) uses letters for type-ahead search and would swallow them.
            case VirtualKey.E: MarkStart(); break;
            case VirtualKey.R: MarkEnd(); break;
            case VirtualKey.Escape when ViewModel.PendingStart != null: ViewModel.CancelPending(); break;
            default: return;
        }
        e.Handled = true;
    }

    // ================================================================== playback

    private bool IsPlaying => _player.PlaybackSession.PlaybackState == MediaPlaybackState.Playing;

    /// <summary>The playhead in ffmpeg time (see <see cref="_origin"/>).</summary>
    private TimeSpan VideoPosition
    {
        get
        {
            var t = _player.PlaybackSession.Position - _origin;
            return t < TimeSpan.Zero ? TimeSpan.Zero : t;
        }
    }

    private void OnTick()
    {
        if (_player.Source == null) return;
        if (_originMeasureAt is DateTime at && DateTime.UtcNow >= at)
        {
            _originMeasureAt = null;
            var raw = _player.PlaybackSession.Position;
            // Seek/step/play cancel the measurement; real offsets are a few frames.
            if (!IsPlaying && raw < TimeSpan.FromSeconds(1)) _origin = raw;
        }
        var pos = VideoPosition;
        if (_stopAt is TimeSpan stop && pos >= stop)
        {
            _player.Pause();
            EndClipPreview();
        }
        if (!_scrubbing) ViewModel.Position = pos;
        PlayIcon.Glyph = IsPlaying ? "" : "";
        PlaceTimelineMarkers();
    }

    private void TogglePlay()
    {
        if (_player.Source == null) return;
        _originMeasureAt = null;
        if (IsPlaying)
        {
            _player.Pause();
            EndClipPreview();
        }
        else
        {
            if (ViewModel.Duration > TimeSpan.Zero && VideoPosition >= ViewModel.Duration - TimeSpan.FromMilliseconds(100))
                Seek(TimeSpan.Zero);
            _player.Play();
            SetRate(_viewSpeed);
        }
    }

    private void Seek(TimeSpan t)
    {
        if (_player.Source == null) return;
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        if (ViewModel.Duration > TimeSpan.Zero && t > ViewModel.Duration) t = ViewModel.Duration;
        EndClipPreview();
        _originMeasureAt = null;
        _player.PlaybackSession.Position = t + _origin;
        ViewModel.Position = t;
        PlaceTimelineMarkers();
    }

    private void SeekBy(TimeSpan delta) => Seek(VideoPosition + delta);

    private void StepFrame(bool forward)
    {
        if (_player.Source == null) return;
        _player.Pause();
        EndClipPreview();
        _originMeasureAt = null;
        if (forward) _player.StepForwardOneFrame();
        else _player.StepBackwardOneFrame();
    }

    private void Play_Click(object sender, RoutedEventArgs e) => TogglePlay();
    private void Player_Tapped(object sender, TappedRoutedEventArgs e) => TogglePlay();
    private void Back1s_Click(object sender, RoutedEventArgs e) => SeekBy(TimeSpan.FromSeconds(-1));
    private void Fwd1s_Click(object sender, RoutedEventArgs e) => SeekBy(TimeSpan.FromSeconds(1));
    private void BackFrame_Click(object sender, RoutedEventArgs e) => StepFrame(forward: false);
    private void NextFrame_Click(object sender, RoutedEventArgs e) => StepFrame(forward: true);

    // ================================================================== opening

    private async void Open_Click(object sender, RoutedEventArgs e) => await PickVideoAsync();

    private async Task PickVideoAsync()
    {
        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.VideosLibrary, ViewMode = PickerViewMode.Thumbnail };
        foreach (var ext in MainViewModel.VideoExtensions) picker.FileTypeFilter.Add(ext);
        InitializeWithWindow.Initialize(picker, _hwnd);
        var file = await picker.PickSingleFileAsync();
        if (file != null) await OpenAsync(file.Path);
    }

    private void Root_DragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.StorageItems) && ViewModel.CanEditSettings)
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            e.DragUIOverride.Caption = "Open video";
        }
    }

    private async void Root_Drop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems) || !ViewModel.CanEditSettings) return;
        var items = await e.DataView.GetStorageItemsAsync();
        if (items.OfType<StorageFile>().FirstOrDefault() is { } file) await OpenAsync(file.Path);
    }

    private async Task OpenAsync(string path)
    {
        if (_ffmpeg == null)
        {
            ShowError("FFmpeg not found", "The app needs ffmpeg.exe and ffprobe.exe to read and cut videos.");
            return;
        }

        try
        {
            Notice.IsOpen = false;
            var info = await _ffmpeg.ProbeAsync(path);
            if (info.VideoCodec == null)
            {
                ShowError("Not a video", $"{System.IO.Path.GetFileName(path)} has no video stream.");
                return;
            }

            _player.Pause();
            EndClipPreview();
            _origin = TimeSpan.Zero;
            ViewModel.LoadVideo(path, info);
            var file = await StorageFile.GetFileFromPathAsync(path);
            _player.Source = MediaSource.CreateFromStorageFile(file);
            RedrawTimeline();
        }
        catch (Exception ex)
        {
            ShowError("Couldn't open video", ex.Message);
        }
    }

    // ================================================================== marking & clip list

    private void MarkStart_Click(object sender, RoutedEventArgs e) => MarkStart();
    private void MarkEnd_Click(object sender, RoutedEventArgs e) => MarkEnd();

    private void MarkStart()
    {
        ViewModel.Position = VideoPosition;
        ViewModel.MarkStart();
    }

    private void MarkEnd()
    {
        ViewModel.Position = VideoPosition;
        // A clip marked while watching at 2× is meant to be exported at 2×.
        if (ViewModel.MarkEnd(_viewSpeed) is string error) ShowNotice(InfoBarSeverity.Warning, "Can't add clip", error);
        else Notice.IsOpen = false;
    }

    private static ClipItem ItemOf(object sender) => (ClipItem)((FrameworkElement)sender).DataContext;

    private void Clips_ItemClick(object sender, ItemClickEventArgs e) => Seek(((ClipItem)e.ClickedItem).Start);

    private void RemoveClip_Click(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.IsExporting) ViewModel.Remove(ItemOf(sender));
    }

    private void PlayClip_Click(object sender, RoutedEventArgs e)
    {
        var item = ItemOf(sender);
        Seek(item.Start);
        // Preview at the clip's export speed; the watching speed comes back when it stops.
        _stopAt = item.End;
        _player.Play();
        SetRate(item.Speed);
    }

    /// <summary>The text a clip-row box shows for its value ("start", "end" or "speed" in Tag).</summary>
    private static string TextOf(ClipItem item, string field) => field switch
    {
        "start" => item.StartText,
        "end" => item.EndText,
        _ => item.SpeedText,
    };

    private void TimeBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        var box = (TextBox)sender;
        if (e.Key == VirtualKey.Enter)
        {
            e.Handled = true;
            ParkFocus(); // commits via LostFocus
        }
        else if (e.Key == VirtualKey.Escape)
        {
            e.Handled = true;
            box.Text = TextOf(ItemOf(box), (string)box.Tag);
            ParkFocus();
        }
    }

    private void TimeBox_LostFocus(object sender, RoutedEventArgs e)
    {
        var box = (TextBox)sender;
        if (box.DataContext is not ClipItem item) return;
        var field = (string)box.Tag;
        var current = TextOf(item, field);
        if (box.Text == current) return;

        if (ViewModel.IsExporting)
        {
            box.Text = current;
            return;
        }

        var error = field == "speed" ? ViewModel.EditSpeed(item, box.Text) : ViewModel.Edit(item, field == "start", box.Text);
        if (error != null) ShowNotice(InfoBarSeverity.Warning, field == "speed" ? "Speed not changed" : "Time not changed", error);
        // The bound text only updates when the value changes; "1:5" → "01:05.000" needs a push.
        box.Text = TextOf(item, field);
    }

    // ================================================================== playback speed

    private void ViewSpeed_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            e.Handled = true;
            ParkFocus(); // commits via LostFocus
        }
        else if (e.Key == VirtualKey.Escape)
        {
            e.Handled = true;
            ViewSpeedBox.Text = PlaybackSpeed.Format(_viewSpeed);
            ParkFocus();
        }
    }

    private void ViewSpeed_LostFocus(object sender, RoutedEventArgs e)
    {
        if (!PlaybackSpeed.TryParse(ViewSpeedBox.Text, out var speed) || PlaybackSpeed.Validate(speed) is not null)
        {
            ShowNotice(InfoBarSeverity.Warning, "Speed not changed",
                $"Type a speed between {PlaybackSpeed.Format(PlaybackSpeed.Min)} and {PlaybackSpeed.Format(PlaybackSpeed.Max)}, like 0.25 or 1.5.");
        }
        else
        {
            _viewSpeed = speed;
            if (_stopAt == null) SetRate(speed);
        }
        ViewSpeedBox.Text = PlaybackSpeed.Format(_viewSpeed);
    }

    private void SetRate(double speed)
    {
        try
        {
            _player.PlaybackSession.PlaybackRate = speed;
        }
        catch (Exception ex)
        {
            ShowNotice(InfoBarSeverity.Warning, "Windows can't play at that speed", ex.Message);
        }
    }

    /// <summary>Stops a "play this clip" preview: no auto-pause, back to the watching speed.</summary>
    private void EndClipPreview()
    {
        if (_stopAt == null) return;
        _stopAt = null;
        SetRate(_viewSpeed);
    }

    // ================================================================== timeline

    private void Clips_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        foreach (ClipItem item in e.NewItems ?? Array.Empty<ClipItem>()) item.PropertyChanged += Clip_PropertyChanged;
        foreach (ClipItem item in e.OldItems ?? Array.Empty<ClipItem>()) item.PropertyChanged -= Clip_PropertyChanged;
        RedrawTimeline();
    }

    private void Clip_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ClipItem.Start) or nameof(ClipItem.End) or nameof(ClipItem.Number)) RedrawTimeline();
    }

    private double TimeToX(TimeSpan t)
    {
        var d = ViewModel.Duration.TotalSeconds;
        return d <= 0 ? 0 : Math.Clamp(t.TotalSeconds / d, 0, 1) * Timeline.ActualWidth;
    }

    private TimeSpan XToTime(double x)
    {
        var w = Timeline.ActualWidth;
        return w <= 0 ? TimeSpan.Zero : TimeSpan.FromSeconds(Math.Clamp(x / w, 0, 1) * ViewModel.Duration.TotalSeconds);
    }

    private void RedrawTimeline()
    {
        TimelineCanvas.Children.Clear();
        double h = Timeline.ActualHeight;
        if (h <= 0) return;

        var accent = Brush("AccentFillColorDefaultBrush");
        foreach (var clip in ViewModel.Clips)
        {
            double x0 = TimeToX(clip.Start), x1 = TimeToX(clip.End);
            var rect = new Rectangle
            {
                Width = Math.Max(2, x1 - x0), Height = h - 12, Fill = accent, Opacity = 0.55,
                RadiusX = 3, RadiusY = 3, IsHitTestVisible = false,
            };
            Canvas.SetLeft(rect, x0);
            Canvas.SetTop(rect, 6);
            TimelineCanvas.Children.Add(rect);

            if (x1 - x0 >= 14)
            {
                var label = new TextBlock { Text = clip.Number.ToString(), FontSize = 11, IsHitTestVisible = false };
                Canvas.SetLeft(label, x0 + 4);
                Canvas.SetTop(label, 8);
                TimelineCanvas.Children.Add(label);
            }
        }

        _pendingRange.Height = h;
        _playhead.Height = h;
        TimelineCanvas.Children.Add(_pendingRange);
        TimelineCanvas.Children.Add(_playhead);
        PlaceTimelineMarkers();
    }

    /// <summary>Moves the playhead and stretches the pending-start range up to it.</summary>
    private void PlaceTimelineMarkers()
    {
        var pos = ViewModel.Position;
        double x = TimeToX(pos);
        Canvas.SetLeft(_playhead, x - 1);
        _playhead.Visibility = ViewModel.HasVideo ? Visibility.Visible : Visibility.Collapsed;

        if (ViewModel.PendingStart is TimeSpan s)
        {
            double xs = TimeToX(s);
            Canvas.SetLeft(_pendingRange, Math.Min(xs, x));
            _pendingRange.Width = Math.Max(2, Math.Abs(x - xs));
            _pendingRange.Visibility = Visibility.Visible;
        }
        else
        {
            _pendingRange.Visibility = Visibility.Collapsed;
        }
    }

    private void Timeline_SizeChanged(object sender, SizeChangedEventArgs e) => RedrawTimeline();

    private void Timeline_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!ViewModel.HasVideo) return;
        _scrubbing = Timeline.CapturePointer(e.Pointer);
        Seek(XToTime(e.GetCurrentPoint(Timeline).Position.X));
        e.Handled = true;
    }

    private void Timeline_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_scrubbing) Seek(XToTime(e.GetCurrentPoint(Timeline).Position.X));
    }

    private void Timeline_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        _scrubbing = false;
        Timeline.ReleasePointerCapture(e.Pointer);
    }

    private void Timeline_PointerCaptureLost(object sender, PointerRoutedEventArgs e) => _scrubbing = false;

    // ================================================================== export

    private void Mode_Checked(object sender, RoutedEventArgs e) => ViewModel.Reencode = ReferenceEquals(sender, ReencodeMode);

    private async void ChangeFolder_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.VideosLibrary };
        picker.FileTypeFilter.Add("*");
        InitializeWithWindow.Initialize(picker, _hwnd);
        var folder = await picker.PickSingleFolderAsync();
        if (folder != null) ViewModel.OutputFolder = folder.Path;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => _exportCts?.Cancel();

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        if (_ffmpeg == null)
        {
            ShowError("FFmpeg not found", "The app needs ffmpeg.exe to export clips.");
            return;
        }

        ParkFocus();
        _player.Pause();
        Notice.IsOpen = false;
        _exportCts = new CancellationTokenSource();
        var run = ViewModel.ExportAllAsync(_ffmpeg, _exportCts.Token);
        _exportTask = run;
        try
        {
            var (saved, failed) = await run;
            if (_closeAfterExport) return;

            var folder = ViewModel.OutputFolder!;
            var open = new Button { Content = "Open folder" };
            open.Click += async (_, _) => await Launcher.LaunchFolderPathAsync(folder);

            if (failed > 0)
            {
                var why = ViewModel.Clips.FirstOrDefault(c => c.State == ClipState.Failed)?.Message;
                ShowNotice(InfoBarSeverity.Error, $"{failed} clip(s) failed", $"{saved} saved. {why}", saved > 0 ? open : null);
            }
            else if (_exportCts.IsCancellationRequested)
            {
                ShowNotice(InfoBarSeverity.Warning, "Export cancelled", $"{saved} clip(s) were saved before you cancelled.", saved > 0 ? open : null);
            }
            else
            {
                ShowNotice(InfoBarSeverity.Success, saved == 1 ? "1 clip saved" : $"{saved} clips saved", folder, open);
            }
        }
        catch (Exception ex)
        {
            ShowError("Export failed", ex.Message);
        }
        finally
        {
            _exportCts.Dispose();
            _exportCts = null;
        }
    }
}
