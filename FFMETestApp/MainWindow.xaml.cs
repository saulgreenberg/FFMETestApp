using Microsoft.Win32;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Unosquare.FFME;
using IOPath = System.IO.Path;
// ReSharper disable AsyncVoidEventHandlerMethod

namespace FFMETestApp;

public partial class MainWindow : Window
{
    #region Constants
    private static string VideoFolder = FindVideoFolder();
    private const string VideoFileFilter = "Video files (*.mp4;*.avi;*.mov;*.wmv;*.mkv)|*.mp4;*.avi;*.mov;*.wmv;*.mkv|All files (*.*)|*.*";
    #endregion

    #region Variables
    private bool _isZoomEnabled = false;
    private double _zoomLevel = 1.0;
    private readonly ScaleTransform _scaleTransform = new ScaleTransform(1, 1);
    private readonly TranslateTransform _translateTransform = new TranslateTransform();
    private readonly ScaleTransform _scaleTransform2 = new ScaleTransform(1, 1);
    private readonly TranslateTransform _translateTransform2 = new TranslateTransform();
    private Point _lastMousePosition;
    private bool _isUserDraggingSlider = false;
    private DateTime _lastSliderUpdate = DateTime.MinValue;
    private bool _wasPlayingBeforeScrub = false;
    private bool _isStepping = false;
    private double _videoFrameRate = 30.0;
    private CancellationTokenSource? _autoSwitchCts;
    private int _autoSwitchIndex = 0;
    private double _positionPct = 0.0;
    private double _positionSeconds = 0.0;
    private bool _isCleaningUp = false;
    private bool _useHardwareAccel = false;
    private bool _hwDecodeChecked = false;

    // Dual-buffer: tracks which MediaElement is currently the visible/active one.
    // true = VideoPlayer is active; false = VideoPlayer2 is active.
    private bool _activeIsPlayer1 = true;
    private Unosquare.FFME.MediaElement ActivePlayer => _activeIsPlayer1 ? VideoPlayer : VideoPlayer2;
    private Unosquare.FFME.MediaElement BackPlayer => _activeIsPlayer1 ? VideoPlayer2 : VideoPlayer;


    // Return all supported video files in the given folder, sorted by name.
    private static string[] GetVideoFiles(string folder) =>
        Directory.GetFiles(folder, "*.mp4")
            .Concat(Directory.GetFiles(folder, "*.avi"))
            .Concat(Directory.GetFiles(folder, "*.mov"))
            .Concat(Directory.GetFiles(folder, "*.wmv"))
            .Concat(Directory.GetFiles(folder, "*.mkv"))
            .OrderBy(f => f)
            .ToArray();
    #endregion

    #region Startup / Event handlers / Closing
    public MainWindow()
    {
        if (DesignerProperties.GetIsInDesignMode(this))
        {
            // In design mode, App.OnStartup doesn't run, so we set a dummy path 
            // to prevent FFME from throwing null-ref errors during InitializeComponent.
            if (string.IsNullOrWhiteSpace(Library.FFmpegDirectory))
            {
                Library.FFmpegDirectory = IOPath.Combine(AppDomain.CurrentDomain.BaseDirectory, "ffmpegbin");
            }
        }

        InitializeComponent();

        if (DesignerProperties.GetIsInDesignMode(this))
            return;

        SetupEventHandlers();
        UpdateStatus("Application initialized. Ready to load video.");
    }

    // Wire up event handlers and configure both media players.
    private void SetupEventHandlers()
    {
        this.Closing += MainWindow_Closing;
        this.Loaded += MainWindow_Loaded;
        this.PreviewKeyDown += MainWindow_PreviewKeyDown;

        // All zoom/pan mouse input is routed through GridVideoPanel so it works in the
        // black-bar areas around the video (Stretch="Uniform" leaves empty space).
        GridVideoPanel.MouseDown += (s, e) => GridVideoPanel.Focus();
        GridVideoPanel.PreviewMouseWheel += VideoPlayer_MouseWheel;
        GridVideoPanel.MouseLeftButtonDown += VideoPlayer_MouseLeftButtonDown;
        GridVideoPanel.MouseMove += VideoPlayer_MouseMove;
        GridVideoPanel.MouseLeftButtonUp += VideoPlayer_MouseLeftButtonUp;

        // See https://github.com/unosquare/ffmediaelement/issues/683#issuecomment-4276582110
        // UseLegacyAudioOut prevents a DirectSound COM RCW crash on rapid open/close cycles.
        VideoPlayer.RendererOptions.UseLegacyAudioOut = true;
        VideoPlayer.MediaFailed += VideoPlayer_MediaFailed;
        VideoPlayer.MediaOpening += VideoPlayer_MediaOpening;

        VideoPlayer2.RendererOptions.UseLegacyAudioOut = true;
        VideoPlayer2.MediaFailed += VideoPlayer_MediaFailed;
        VideoPlayer2.MediaOpening += VideoPlayer_MediaOpening;

        // Set up a combined scale + translate transform so zoom and pan work together.
        // Both players get their own TransformGroup (WPF Freezables can't be shared across
        // two groups) but all four transform objects are kept in sync on every zoom/pan change.
        var group = new TransformGroup();
        group.Children.Add(_scaleTransform);
        group.Children.Add(_translateTransform);
        VideoPlayer.RenderTransformOrigin = new Point(0.5, 0.5);
        VideoPlayer.RenderTransform = group;

        var group2 = new TransformGroup();
        group2.Children.Add(_scaleTransform2);
        group2.Children.Add(_translateTransform2);
        VideoPlayer2.RenderTransformOrigin = new Point(0.5, 0.5);
        VideoPlayer2.RenderTransform = group2;
    }

    // Clean up both players on window close.
    // e.Cancel defers the close so the async cleanup can finish; Shutdown() then re-triggers
    // Closing, which the _isCleaningUp guard lets through without cancelling a second time.
    private async void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (_isCleaningUp) return;
        e.Cancel = true;
        _isCleaningUp = true;
        try
        {
            _autoSwitchCts?.Cancel();
            UpdateStatus("Shutting down...");
            await CleanupPlayer(VideoPlayer);
            await CleanupPlayer(VideoPlayer2);
        }
        catch
        {
            // Suppress — window is closing regardless.
        }
        Application.Current.Shutdown();
    }

    // Pick a random video from the VideoFolder and start playing it.
    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        // The program requires a folder containing a few videos within it.
        // Such a folder should be included in the distribution
        if (VideoFolder == string.Empty)
        {
            // It couldn't find the included Video folder so ask the user to manually select it.
            MessageBox.Show("This program will ask you to select a folder containing at least a few videos in it. When you do, it will open a video from that folder at random.",
                "Select Video Folder", MessageBoxButton.OK, MessageBoxImage.Information);

            var dialog = new OpenFolderDialog
            {
                Title = "Select a folder containing videos"
            };
            if (dialog.ShowDialog() != true)
            {
                Application.Current.Shutdown();
                return;
            }
            VideoFolder = dialog.FolderName;
        }
        try
        {
            var videos = GetVideoFiles(VideoFolder);
            if (videos.Length == 0)
            {
                MessageBox.Show("No videos found in " + VideoFolder + ", so the program will shut down.",
                    "No videos found", MessageBoxButton.OK, MessageBoxImage.Error);
                Application.Current.Shutdown();
                return;
            }

            string pick = videos[Random.Shared.Next(videos.Length)];
            bool loaded = await LoadVideo(pick);
            if (loaded)
            {
                await ActivePlayer.Play();
                UpdatePlayPauseIcon(true);
            }
        }
        catch
        {
            // Ignore
        }
    }
    #endregion

    #region File Operations
    // A folder including sample videos is located in the project. Find it.
    private static string FindVideoFolder()
    {
        // 1. Check current directory (deployment)
        string path = IOPath.Combine(AppDomain.CurrentDomain.BaseDirectory, "Videos");
        if (Directory.Exists(path))
        {
            return path;
        }

        // 2. Traverse up to find it, but stop at the project/solution root
        var dir = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
        while (dir != null)
        {
            path = IOPath.Combine(dir.FullName, "Videos");
            if (Directory.Exists(path)) return path;

            // Also check for the case where it's inside the FFMETestApp folder
            path = IOPath.Combine(dir.FullName, "FFMETestApp", "Videos");
            if (Directory.Exists(path)) return path;

            bool isProjectRoot = dir.GetFiles("*.slnx").Length > 0 ;
            if (isProjectRoot) break;

            dir = dir.Parent;
        }

        return string.Empty;
    }


    #endregion

    #region Dialogs to open a video 
    // Show file-open dialog and load the selected video.
    private async void ButtonOpenFile_Click(object sender, RoutedEventArgs e)
    {
        string fileName = string.Empty;
        try
        {
            await StopAutoSwitchAndPlayers();
            OpenFileDialog openFileDialog = new OpenFileDialog
            {
                Title = "Select Video File",
                InitialDirectory = VideoFolder,
                Filter = VideoFileFilter
            };

            if (openFileDialog.ShowDialog() == true)
            {
                fileName = openFileDialog.FileName;
                if (await LoadVideo(fileName))
                {
                    await ActivePlayer.Play();
                }
                UpdatePlayPauseIcon(true);
            }
        }
        catch (Exception ex)
        {
            const string errorMessage = "Error opening video";
            UpdateStatus($"{errorMessage}: {ex.Message}");
            string details = $"{errorMessage}: {IOPath.GetFileName(fileName)}\n\nError: {ex.Message}\nType: {ex.GetType().Name}";
            if (ex.InnerException != null)
                details += $"\nInner: {ex.InnerException.Message}";
            MessageBox.Show(details, errorMessage, MessageBoxButton.OK, MessageBoxImage.Error);
            UpdatePlayPauseIcon(false);
        }
    }
    // Open the thumbnail grid dialog; clicking a thumbnail loads that video.
    private async void ButtonOpenByThumbnail_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(VideoFolder)) return;
        var videos = GetVideoFiles(VideoFolder);
        if (videos.Length == 0)
        {
            MessageBox.Show($"No video files found in:\n{VideoFolder}", "No Videos",
                MessageBoxButton.OK, MessageBoxImage.Information);
            UpdatePlayPauseIcon(false);
            return;
        }
        await StopAutoSwitchAndPlayers();
        var win = new ThumbnailsWindow(videos) { Owner = this };
        win.VideoSelected += async path =>
        {
            win.Close();
            Activate();
            _isUserDraggingSlider = false;
            _wasPlayingBeforeScrub = false;

            if (UseDualBufferCheckBox.IsChecked == true)
            {
                await LoadAndSwapDualBuffer(path, CancellationToken.None);
                PositionSlider.Value = 0;
                PositionText.Text = "00:00:00 / 00:00:00";
            }
            else
            {
                if (await LoadVideo(path))
                {
                    await ActivePlayer.Play();
                }
            }
        };
        win.Show();
        UpdatePlayPauseIcon(true);
    }

    #endregion

    #region Core Video Operations — LoadVideo, CleanupPlayer

    // Load a video into VideoPlayer (single-buffer path). Returns true on success.
    private async Task<bool> LoadVideo(string filePath, bool silent = false)
    {
        try
        {
            if (!Dispatcher.CheckAccess())
            {
                bool result = false;
                await Dispatcher.InvokeAsync(async () => result = await LoadVideo(filePath, silent));
                return result;
            }

            _isUserDraggingSlider = false;
            _wasPlayingBeforeScrub = false;

            bool loaded = await LoadVideoIntoPlayer(VideoPlayer, filePath, silent);
            if (loaded)
            {
                // If VideoPlayer2 is currently visible (left over from a prior dual-buffer swap),
                // swap visibility back so the newly loaded VideoPlayer is on screen.
                if (!_activeIsPlayer1)
                {
                    var previous = ActivePlayer; // VideoPlayer2
                    VideoPlayer.Visibility = Visibility.Visible;
                    _activeIsPlayer1 = true;
                    previous.Visibility = Visibility.Hidden;
                    _ = CleanupPlayer(previous);
                }
                PositionSlider.Value = 0;
                PositionText.Text = "00:00:00 / 00:00:00";
                FileNameText.Text = IOPath.GetFileName(filePath);
                UpdateStatus($"Loaded: {IOPath.GetFileName(filePath)}");
                UpdatePlayPauseIcon(false);
            }
            return loaded;
        }
        catch (Exception ex)
        {
            UpdateStatus($"Error loading video: {ex.Message}");
            if (!silent)
            {
                string details = $"Error loading: {IOPath.GetFileName(filePath)}\n\nError: {ex.Message}";
                if (ex.InnerException != null) details += $"\nInner: {ex.InnerException.Message}";
                MessageBox.Show(details, "Video Load Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            return false;
        }
    }

    // Open a video file into the given player instance, with one retry for transient container errors.
    private async Task<bool> LoadVideoIntoPlayer(Unosquare.FFME.MediaElement player, string filePath, bool silent = false)
    {
        try
        {
            await CleanupPlayer(player);

            var uri = new Uri(filePath, UriKind.Absolute);
            try
            {
                await player.Open(uri);
            }
            catch (Unosquare.FFME.Common.MediaContainerException)
            {
                // Transient race condition during rapid switching — brief wait then retry once.
                await Task.Delay(150);
                await CleanupPlayer(player);
                await player.Open(uri);
            }

            // Always seek to the beginning; FFME can carry over position from a previous video.
            try
            {
                await player.Seek(TimeSpan.Zero);
            }
            catch
            {
                // Do nothing
            }

            return true;
        }
        catch (Exception ex)
        {
            if (!silent) UpdateStatus($"Error loading video: {ex.Message}");
            if (ex.InnerException != null)
                Debug.Print($"[LoadVideoIntoPlayer] Inner: {ex.InnerException.Message}");
            return false;
        }
    }

    // Stop and close the given player, polling briefly to confirm state changes.
    private async Task CleanupPlayer(Unosquare.FFME.MediaElement player)
    {
        try
        {
            if (player == null)
            {
                return;
            }

            if (player.IsLoaded || player.Source != null)
            {
                if (player.IsPlaying)
                {
                    await player.Stop();
                    // Poll until stopped (max 200 ms).
                    for (int i = 0; player.IsPlaying && i < 20; i++)
                        await Task.Delay(10);
                }

                await player.Close();

                // Poll until closed (max 200 ms).
                for (int i = 0; (player.IsLoaded || player.Source != null) && i < 20; i++)
                {
                    await Task.Delay(10);
                }
            }
        }
        catch (Exception ex)
        {
            UpdateStatus($"Warning during cleanup: {ex.Message}");
        }
    }

    #endregion

    #region FFME Event Handlers — MediaOpening, MediaOpened, MediaFailed, PositionChanged, MediaEnded

    // Apply hardware acceleration setting just before FFME opens the file.
    // MediaOpening fires on FFME's background thread, so we read _useHardwareAccel (a plain bool)
    // rather than touching HardwareAccelCheckBox.IsChecked (a UI property).
    // When hardware is wanted, pass the stream's own HardwareDevices list so FFME picks the
    // best available accelerator (typically D3D11VA on Windows).
    // When hardware is off, leave VideoHardwareDevices at its default (null = software decode).
    private void VideoPlayer_MediaOpening(object? sender, Unosquare.FFME.Common.MediaOpeningEventArgs e)
    {
        if (!_useHardwareAccel) return;

        var videoStream = e.Info?.Streams?.Values.FirstOrDefault(s => s.PixelWidth > 0);
        var hwDevices = videoStream?.HardwareDevices?.ToArray();
        if (hwDevices != null && hwDevices.Length > 0)
            e.Options.VideoHardwareDevices = hwDevices;
    }


    // Update UI when a video finishes opening. Ignored for the back-buffer player.
    private void VideoPlayer_MediaOpened(object sender, Unosquare.FFME.Common.MediaOpenedEventArgs e)
    {
        if (false == sender.Equals(ActivePlayer))
        {
            return;
        }

        Dispatcher.Invoke(() =>
        {
            UpdateStatus("Media opened successfully");
            ResolutionText.Text = $"Resolution: {ActivePlayer.NaturalVideoWidth}x{ActivePlayer.NaturalVideoHeight}";
            var videoStream = e.Info?.Streams?.Values.FirstOrDefault(s => s.PixelWidth > 0);
            if (videoStream?.FPS > 0)
            {
                _videoFrameRate = videoStream.FPS;
            }
            FrameRateText.Text = $"FPS: {_videoFrameRate:F2}";
            // VideoHardwareDecoder is only populated after the first frame decodes, not at open time.
            // Reset the flag so PositionChanged will pick it up on the first frame.
            _hwDecodeChecked = false;
            HwAccelText.Text = "Decode: Detecting...";
            var duration = ActivePlayer.NaturalDuration ?? TimeSpan.Zero;
            PositionText.Text = $"00:00:00 / {duration:hh\\:mm\\:ss}";
            UpdatePlayPauseIcon(ActivePlayer.IsPlaying);
            UpdateCornerAndProgressBarOverlays();
        });
    }

    // Log failure for either player.
    private void VideoPlayer_MediaFailed(object? sender, Unosquare.FFME.Common.MediaFailedEventArgs e)
    {
        Dispatcher.Invoke(() => UpdateStatus($"Media failed: {e.ErrorException?.Message ?? "unknown error"}"));
    }

    // Drive the position slider and overlay animation. Ignored for the back-buffer player
    // and ignored entirely while the user is scrubbing (stale FFME events must not override
    // the slider position the user has dragged to).
    private void VideoPlayer_PositionChanged(object sender, Unosquare.FFME.Common.PositionChangedEventArgs e)
    {
        if (false == sender.Equals(ActivePlayer))
        {
            return;
        }
        Dispatcher.Invoke(() =>
        {
            if (_isUserDraggingSlider)
            {
                return;
            }

            // VideoHardwareDecoder is only set after the first frame decodes. Do one read here
            // and stop checking once we have a definitive answer.
            if (!_hwDecodeChecked)
            {
                string hwName = ActivePlayer.VideoHardwareDecoder;
                if (hwName != null)
                {
                    HwAccelText.Text = string.IsNullOrEmpty(hwName) ? "Decode: Software" : $"Decode: {hwName}";
                    _hwDecodeChecked = true;
                }
            }

            var position = e.Position;
            var duration = ActivePlayer.NaturalDuration ?? TimeSpan.Zero;
            PositionText.Text = $"{position:hh\\:mm\\:ss} / {duration:hh\\:mm\\:ss}";

            if (duration.TotalSeconds > 0)
            {
                double pct = Math.Max(0, Math.Min(1, position.TotalSeconds / duration.TotalSeconds));
                _positionPct     = pct;
                _positionSeconds = position.TotalSeconds;
                UpdateProgressBar(pct);

                var now = DateTime.Now;
                if ((now - _lastSliderUpdate).TotalMilliseconds > 250)
                {
                    PositionSlider.Value = pct * 100.0;
                    _lastSliderUpdate = now;
                }
            }

            AnimatePositionOverlay(position);
        });
    }

    // Loop the video when it ends, unless auto-switch is managing playback or the user is scrubbing.
    private void VideoPlayer_MediaEnded(object sender, EventArgs e)
    {
        if (false == sender.Equals(ActivePlayer))
        {
            return;
        }
        
        if (AutoRepeatCheckBox?.IsChecked != true || _autoSwitchCts != null || _isUserDraggingSlider)
        {
            UpdatePlayPauseIcon(false);
            return;
        }

        _ = Dispatcher.InvokeAsync(async () =>
        {
            try
            {
                await ActivePlayer.Seek(TimeSpan.Zero);
                await ActivePlayer.Play();
                UpdatePlayPauseIcon(true);
            }
            catch
            {
                // Do nothing
            }
        });
    }
    #endregion

    #region Video Controls — Play / Pause / Stop

    // Toggle between play and pause; icon follows the resulting state.
    private async void PlayPauseButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            // If auto-switch is active, always cancel it and pause — don't rely on IsPlaying,
            // which is unreliable during dual-buffer swaps.
            if (_autoSwitchCts != null)
            {
                _autoSwitchCts.Cancel();
                AutoSwitchCheckBox.IsChecked = false;
                try
                {
                    await VideoPlayer.Pause();
                }
                catch (Exception ex)
                {
                    UpdateStatus($"Error pausing player 1: {ex.Message}");
                }

                try
                {
                    await VideoPlayer2.Pause();
                }
                catch (Exception ex)
                {
                    UpdateStatus($"Error pausing player 2: {ex.Message}");
                }

                UpdateStatus("Paused");
                UpdatePlayPauseIcon(false);
                return;
            }

            if (ActivePlayer.IsPlaying)
            {
                await ActivePlayer.Pause();
                UpdateStatus("Paused");
                UpdatePlayPauseIcon(false);
            }
            else
            {
                await ActivePlayer.Play();
                UpdateStatus("Playing");
                UpdatePlayPauseIcon(true);
            }
        }
        catch (Exception ex)
        {
            UpdateStatus($"Error: {ex.Message}");
        }
    }

    // Stop playback, cancel auto-switching, and restore the play icon.
    private async void StopButton_Click(object sender, RoutedEventArgs e)
    {
        await StopAutoSwitchAndPlayers();
        PositionSlider.Value = 0;
        UpdateStatus("Stopped");
    }

    // Keep the play/pause icon in sync with the active player's state.
    private void UpdatePlayPauseIcon(bool playing)
    {
        PlayPauseIcon.Text = playing ? "⏸" : "▶";
    }
    #endregion

    #region Video Controls — Scrubbing

    // Set Position directly — FFME's intended scrubbing API. This queues the seek internally
    // without the async overhead of Seek(), so rapid slider ticks don't pile up.
    private void PositionSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_isUserDraggingSlider || ActivePlayer == null)
        {
            return;
        }

        var duration = ActivePlayer.NaturalDuration ?? TimeSpan.Zero;
        if (duration.TotalSeconds <= 0) return;

        double pct = e.NewValue / 100.0;
        var target = TimeSpan.FromSeconds(duration.TotalSeconds * pct);
        PositionText.Text = $"{target:hh\\:mm\\:ss} / {duration:hh\\:mm\\:ss}";
        _positionPct     = pct;
        _positionSeconds = target.TotalSeconds;
        UpdateProgressBar(pct);

        if (ActivePlayer.IsSeekable)
            ActivePlayer.Position = target;

        AnimatePositionOverlay(target);
    }

    // Pause before scrubbing so live seeks are responsive.
    private async void PositionSlider_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        _isUserDraggingSlider = true;
        _wasPlayingBeforeScrub = ActivePlayer?.IsPlaying == true;
        if (_wasPlayingBeforeScrub)
        {
            try
            {
                await ActivePlayer!.Pause();
                UpdateStatus("Paused for scrubbing");
                UpdatePlayPauseIcon(false);
            }
            catch (Exception ex)
            {
                UpdateStatus($"Pause error: {ex.Message}");
            }
        }
    }

    // Arrow-key frame stepping. PreviewKeyDown fires before any focused control (e.g. the
    // slider) consumes the key, and also fires on every OS key-repeat event when held down.
    // _isStepping gates out repeat events that arrive before the current step finishes,
    // naturally rate-limiting to FFME's decode speed rather than the OS repeat rate.
    private async void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Left && e.Key != Key.Right) return;
        if (ActivePlayer == null || !ActivePlayer.IsSeekable) return;

        e.Handled = true; // prevent arrow keys from moving slider thumb focus
        if (_isStepping)
        {
            return;
        }
        _isStepping = true;

        try
        {
            await StopAutoSwitchAndPlayers();

            if (ActivePlayer.IsSeekable)
            {
                if (e.Key == Key.Right)
                {
                    await ActivePlayer.StepForward();
                }
                else
                {
                    await ActivePlayer.StepBackward();
                }
            }
        }
        catch
        {
            // Do nothing
        }
        finally
        {
            _isStepping = false;
        }
    }

    // On release: set Position to the exact release point, then resume if needed.
    private async void PositionSlider_PreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        var duration = ActivePlayer?.NaturalDuration ?? TimeSpan.Zero;
        if (duration.TotalSeconds > 0 && ActivePlayer?.IsSeekable == true)
        {
            var target = TimeSpan.FromSeconds(duration.TotalSeconds * (PositionSlider.Value / 100.0));
            ActivePlayer.Position = target;
            AnimatePositionOverlay(target);
        }

        _isUserDraggingSlider = false;

        if (_wasPlayingBeforeScrub && ActivePlayer != null)
        {
            try
            {
                await ActivePlayer.Play();
                UpdateStatus("Resumed playback after scrubbing");
                UpdatePlayPauseIcon(true);
            }
            catch (Exception ex)
            {
                UpdateStatus($"Resume error: {ex.Message}");
            }
        }
        _wasPlayingBeforeScrub = false;
    }
    #endregion

    #region Video Controls — Volume

    // Apply volume to the active player only; back-buffer player stays muted.
    private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (ActivePlayer != null)
            ActivePlayer.Volume = e.NewValue;
    }
    #endregion

    #region Video Controls — Speed

    // Apply the selected speed ratio to both players so it persists across dual-buffer swaps.
    private void SpeedRadioButton_Checked(object sender, RoutedEventArgs e)
    {
        double speed = sender == Speed05 ? 0.5 : sender == Speed20 ? 2.0 : 1.0;
        if (VideoPlayer != null) VideoPlayer.SpeedRatio = speed;
        if (VideoPlayer2 != null) VideoPlayer2.SpeedRatio = speed;
    }
    #endregion

    #region Video Controls — Auto Switch

    // Start or stop the auto-switch loop when the checkbox is toggled.
    private async void AutoSwitchCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (AutoSwitchCheckBox.IsChecked == true)
        {
            if (_autoSwitchCts != null)
            {
                return; // already running
            }

            try
            {
                await StartAutoSwitch();
            }
            catch (Exception ex)
            {
                UpdateStatus($"Auto switch error: {ex.Message}");
            }
        }
        else
        {
            _autoSwitchCts?.Cancel();
        }
    }

    // Load and play the next video from the list, advancing _autoSwitchIndex.
    // Used by both the auto-switch loop and the Next button.
    private async Task SwitchToNextVideoAsync(string[] videos, CancellationToken token)
    {
        var video = videos[_autoSwitchIndex % videos.Length];
        _autoSwitchIndex++;

        if (UseDualBufferCheckBox.IsChecked == true)
        {
            await LoadAndSwapDualBuffer(video, token);
        }
        else
        {
            // Single-buffer: stop current, load next, play (produces a brief blank flash).
            bool loaded = await LoadVideo(video, silent: true);
            if (loaded)
            {
                try
                {
                    await VideoPlayer.Play();
                    UpdatePlayPauseIcon(true);
                }
                catch (Exception ex)
                {
                    UpdateStatus($"Play error: {ex.Message}");
                }
            }
        }
    }

    // Immediately switch to the next video, using the same path as the auto-switch loop.
    // Button is disabled for the duration of the switch to prevent overlapping calls
    // that would race on the back-buffer player and produce a blank screen.
    private async void ButtonNext_Click(object sender, RoutedEventArgs e)
    {
        var videos = GetVideoFiles(VideoFolder);
        if (videos.Length == 0) return;
        ButtonNext.IsEnabled = false;
        try
        {
            await SwitchToNextVideoAsync(videos, CancellationToken.None);
        }
        finally
        {
            ButtonNext.IsEnabled = true;
        }
    }

    // Repeatedly load and play videos from the video folder.
    //
    // Loop structure:
    //   1. Pick the next video via SwitchToNextVideoAsync (shared with the Next button).
    //   2. Wait for the configured interval before switching again.
    //   3. On cancellation the loop breaks; finally stops the active player and resets the checkbox.
    private async Task StartAutoSwitch()
    {
        var testVideos = GetVideoFiles(VideoFolder);

        if (testVideos.Length <= 1)
        {
            UpdateStatus($"Need at least 2 videos in {VideoFolder} for testing");
            AutoSwitchCheckBox.IsChecked = false;
            return;
        }

        _autoSwitchCts = new CancellationTokenSource();
        var token = _autoSwitchCts.Token;

        try
        {
            while (!token.IsCancellationRequested)
            {
                await SwitchToNextVideoAsync(testVideos, token);

                // Wait for the configured interval; break immediately on cancellation.
                int intervalMs = (int)(AutoSwitchTime.Value * 1000);
                try { await Task.Delay(intervalMs, token); }
                catch (OperationCanceledException) { break; }
            }
        }
        finally
        {
            _autoSwitchCts?.Dispose();
            _autoSwitchCts = null;

            try
            {
                await ActivePlayer.Stop();
                UpdatePlayPauseIcon(false);
            }
            catch
            {
                // Do nothing
            }

            UpdateStatus("Auto switch stopped.");

            if (AutoSwitchCheckBox.IsChecked == true)
                AutoSwitchCheckBox.IsChecked = false;
        }
    }

    // Cancel auto-switching and stop both players. Safe to call when switching is not active.
    private async Task StopAutoSwitchAndPlayers()
    {
        if (_autoSwitchCts != null)
        {
            _autoSwitchCts.Cancel();
            AutoSwitchCheckBox.IsChecked = false;
        }

        try
        {
            await VideoPlayer.Stop();
        }
        catch
        {
            // Do nothing
        }

        try
        {
            await VideoPlayer2.Stop();
        }
        catch
        {
            // Do nothing
        }
        UpdatePlayPauseIcon(false);
    }

    // Update the interval label next to the slider.
    private void SliderAutoSwitchTime_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (SliderAutoSwitchTimeLabel != null)
        {
            SliderAutoSwitchTimeLabel.Text = $"{e.NewValue:F2}s";
        }
    }
    #endregion

    #region Video Controls - Hardware acceleration
    // Reload the current video so the new hardware acceleration setting takes effect immediately.
    private async void HardwareAccelCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        _useHardwareAccel = HardwareAccelCheckBox.IsChecked == true;
        if (ActivePlayer.Source != null)
        {
            string current = ActivePlayer.Source.LocalPath;
            await LoadVideo(current, silent: true);
            await ActivePlayer.Play();
        }
    }
    #endregion

    #region Dual buffering
    // Dual-buffer swap: preload the next video in the hidden back player, play it, then
    // instantly make it visible and hide the old active player. Eliminates the blank flash.
    private async Task LoadAndSwapDualBuffer(string filePath, CancellationToken token)
    {
        var back = BackPlayer;

        bool loaded = await LoadVideoIntoPlayer(back, filePath, silent: true);
        if (!loaded || token.IsCancellationRequested)
        {
            return;
        }

        try
        {
            // Carry the current speed setting into the back player before it starts playing;
            // FFME may reset SpeedRatio to 1.0 on Close()/Open(), so we re-apply it here.
            back.SpeedRatio = ActivePlayer.SpeedRatio;
            await back.Play();
            // Allow ~80 ms for the first frame to decode before making the player visible.
            await Task.Delay(80, token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            UpdateStatus($"Back player play error: {ex.Message}");
            return;
        }

        var previousActive = ActivePlayer;

        back.Volume = SliderVolume.Value;
        back.Visibility = Visibility.Visible;
        _activeIsPlayer1 = !_activeIsPlayer1;

        previousActive.Visibility = Visibility.Hidden;
        _ = CleanupPlayer(previousActive);

        FileNameText.Text = IOPath.GetFileName(filePath);
        UpdateStatus($"Loaded: {IOPath.GetFileName(filePath)}");
        UpdatePlayPauseIcon(true);
        PositionSlider.Value = 0;
        PositionText.Text = "00:00:00 / 00:00:00";
        _positionPct = 0;
        _positionSeconds = 0;
        UpdateCornerAndProgressBarOverlays();
    }
    #endregion

    #region Overlay Controls

    // Show or hide the overlay canvas.
    private void ShowOverlaysCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (OverlayCanvas != null)
        {
            OverlayCanvas.Visibility = ShowOverlaysCheckBox.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            UpdateStatus($"Overlays {(ShowOverlaysCheckBox.IsChecked == true ? "shown" : "hidden")}");
        }
    }
    #endregion

    #region Zoom Controls

    // Toggle zoom mode; reset to 1:1 when disabled.
    private void EnableZoomCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        _isZoomEnabled = EnableZoomCheckBox.IsChecked == true;
        UpdateStatus($"Zoom {(_isZoomEnabled ? "enabled" : "disabled")}");
        if (!_isZoomEnabled)
        {
            ResetZoom();
        }
    }

    // Apply the current zoom level via the shared ScaleTransform.
    private void ApplyZoom(double zoomLevel)
    {
        _scaleTransform.ScaleX  = _scaleTransform2.ScaleX  = zoomLevel;
        _scaleTransform.ScaleY  = _scaleTransform2.ScaleY  = zoomLevel;
        // reposition the overlay as needed based on the zoom
        UpdateCornerAndProgressBarOverlays();
    }

    // Reset zoom and pan back to defaults.
    private void ResetZoom()
    {
        _zoomLevel = 1.0;
        _scaleTransform.ScaleX  = _scaleTransform2.ScaleX  = 1;
        _scaleTransform.ScaleY  = _scaleTransform2.ScaleY  = 1;
        _translateTransform.X   = _translateTransform2.X   = 0;
        _translateTransform.Y   = _translateTransform2.Y   = 0;
        // reposition the overlay as needed based on the zoom
        UpdateCornerAndProgressBarOverlays();
    }
    #endregion

    #region GetVideoPlayerCoordinates
    // Used by Frame number/Timestamp overlay to position itself
    // Return the screen position (in OverlayCanvas/GridVideoPanel coordinates) of the
    // top-left corner of the actual video image, accounting for Stretch="Uniform" black bars,
    // the current zoom level, and the current pan translation.
    private Point GetVideoContentTopLeft()
    {
        double elemW = ActivePlayer.ActualWidth;
        double elemH = ActivePlayer.ActualHeight;
        if (elemW <= 0 || elemH <= 0) return new Point(0, 0);

        // Find where Stretch="Uniform" places the content rectangle inside the element.
        double natW = ActivePlayer.NaturalVideoWidth;
        double natH = ActivePlayer.NaturalVideoHeight;
        double contentLeft = 0, contentTop = 0;
        if (natW > 0 && natH > 0)
        {
            double scale = Math.Min(elemW / natW, elemH / natH);
            contentLeft = (elemW - natW * scale) / 2.0;
            contentTop = (elemH - natH * scale) / 2.0;
        }

        // Apply the active player's RenderTransform (TransformGroup(Scale, Translate) with
        // RenderTransformOrigin = (0.5, 0.5)).  The formula for any local point (x, y) is:
        //   screen = s * (local - center) + translate + center
        double cx = elemW / 2.0;
        double cy = elemH / 2.0;
        double screenX = _zoomLevel * (contentLeft - cx) + _translateTransform.X + cx;
        double screenY = _zoomLevel * (contentTop - cy) + _translateTransform.Y + cy;

        return new Point(screenX, screenY);
    }

    // Used by progress bar to ensure it fits within the video player.
    // Returns the clamped (left, maxWidth) of the visible video in canvas coordinates,
    // accounting for Stretch=Uniform black bars, zoom, and pan. Clamps to the grid edges
    private (double left, double maxWidth, double barTop) GetVideoBarBounds()
    {
        double elemW = ActivePlayer?.ActualWidth ?? 0;
        double elemH = ActivePlayer?.ActualHeight ?? 0;
        double gridW = OverlayCanvas.ActualWidth;
        double gridH = OverlayCanvas.ActualHeight;
        const double barHeight = 50;
        if (elemW <= 0 || elemH <= 0 || gridW <= 0)
            return (0, gridW, Math.Max(0, gridH - barHeight));

        double natW = ActivePlayer!.NaturalVideoWidth;
        double natH = ActivePlayer.NaturalVideoHeight;
        double contentW = elemW;
        double contentH = elemH;
        double contentLeft = 0;
        double contentTop = 0;
        if (natW > 0 && natH > 0)
        {
            double scale = Math.Min(elemW / natW, elemH / natH);
            contentW = natW * scale;
            contentH = natH * scale;
            contentLeft = (elemW - contentW) / 2.0;
            contentTop = (elemH - contentH) / 2.0;
        }

        double cx = elemW / 2.0;
        double cy = elemH / 2.0;
        double screenLeft = _zoomLevel * (contentLeft - cx) + _translateTransform.X + cx;
        double screenRight = _zoomLevel * (contentLeft + contentW - cx) + _translateTransform.X + cx;
        double screenBottom = _zoomLevel * (contentTop + contentH - cy) + _translateTransform.Y + cy;

        double clampedLeft = Math.Max(0, screenLeft);
        double clampedRight = Math.Min(gridW, screenRight);
        double clampedBottom = Math.Min(gridH, screenBottom);
        return (clampedLeft, Math.Max(0, clampedRight - clampedLeft), clampedBottom - barHeight);
    }
    #endregion

    #region Mouse Callbacks — Zoom and Pan
    // Handlers are attached to GridVideoPanel (not VideoPlayer) so they also fire
    // when the cursor is in the black bars around a Stretch="Uniform" video.

    // Mouse wheel: zoom in/out while zoom mode is enabled.
    private void VideoPlayer_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (!_isZoomEnabled) return;

        _zoomLevel *= e.Delta > 0 ? 1.1 : 0.9;
        _zoomLevel = Math.Max(0.5, Math.Min(_zoomLevel, 5.0));
        ApplyZoom(_zoomLevel);
        UpdateStatus($"Zoom level: {_zoomLevel:F2}x");

        // Mark handled so the event doesn't also fire on the parent (GridVideoPanel).
        e.Handled = true;
    }

    // Mouse down: double-click resets zoom and pan; single click begins a pan drag.
    // _lastMousePosition is set BEFORE CaptureMouse() so any MouseMove fired synchronously
    // during capture has a valid reference and does not cause an immediate jump.
    private void VideoPlayer_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            ResetZoom();
            UpdateStatus("Zoom reset");
            return;
        }

        if (!_isZoomEnabled) return;
        // Positions are relative to GridVideoPanel so deltas are in screen (parent) coordinates.
        _lastMousePosition = e.GetPosition(GridVideoPanel);
        GridVideoPanel.CaptureMouse();
    }

    // Mouse move: translate the video by the drag delta while the mouse is captured,
    // then clamp so at least half the video remains on screen.
    private void VideoPlayer_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_isZoomEnabled || !GridVideoPanel.IsMouseCaptured) return;

        var newPos = e.GetPosition(GridVideoPanel);
        var delta = newPos - _lastMousePosition;
        _translateTransform.X += delta.X;
        _translateTransform.Y += delta.Y;
        _lastMousePosition = newPos;

        ClampTranslation();
        UpdateCornerAndProgressBarOverlays();
    }

    // Keep the video center within the panel bounds.
    //
    // With RenderTransformOrigin=(0.5,0.5) and TransformGroup(Scale,Translate), the video
    // center maps to (panelW/2 + tx, panelH/2 + ty) in screen space. Clamping tx to
    // [-panelW/2, panelW/2] keeps the center inside the panel, which guarantees that at least
    // half the video is visible regardless of zoom level.
    private void ClampTranslation()
    {
        double w = GridVideoPanel.ActualWidth;
        double h = GridVideoPanel.ActualHeight;
        if (w <= 0 || h <= 0) return;

        _translateTransform.X  = Math.Max(-w / 2, Math.Min(w / 2, _translateTransform.X));
        _translateTransform.Y  = Math.Max(-h / 2, Math.Min(h / 2, _translateTransform.Y));
        _translateTransform2.X = _translateTransform.X;
        _translateTransform2.Y = _translateTransform.Y;
    }

    // Mouse up: release the capture to end the pan drag.
    private void VideoPlayer_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        GridVideoPanel.ReleaseMouseCapture();
    }
    #endregion

    #region Overlay - FrameNumber, Timestamp and Progress bar
    // Position the fixed corner overlays at the top-left of the visible video image.
    private void UpdateCornerAndProgressBarOverlays()
    {
        if (FrameNumberText == null || TimestampText == null) return;

        var pos = GetVideoContentTopLeft();
        Canvas.SetLeft(FrameNumberText, pos.X);
        Canvas.SetTop(FrameNumberText, pos.Y);

        Canvas.SetLeft(TimestampText, pos.X);
        double episodeH = FrameNumberText.ActualHeight > 0 ? FrameNumberText.ActualHeight : 32;
        Canvas.SetTop(TimestampText, pos.Y + episodeH + 2);

        UpdateProgressBar(_positionPct);
    }

    // Reposition and resize the progress bar, end marker, and position label.
    private void UpdateProgressBar(double pct)
    {
        var (barLeft, barMaxWidth, barTop) = GetVideoBarBounds();
        double barCurrentWidth = barMaxWidth * pct;

        Canvas.SetLeft(PositionBar, barLeft);
        Canvas.SetTop(PositionBar, barTop);
        PositionBar.Width = barCurrentWidth;

        double markerLeft = barLeft + barCurrentWidth - PositionIndicator.Width / 2.0;
        Canvas.SetLeft(PositionIndicator, markerLeft);
        Canvas.SetTop(PositionIndicator, barTop);

        PositionLabel.Text = $"{_positionSeconds:F1}s";
        double labelH = PositionLabel.ActualHeight > 0 ? PositionLabel.ActualHeight : 16;
        Canvas.SetLeft(PositionLabel, markerLeft);
        Canvas.SetTop(PositionLabel, barTop - labelH - 2);
    }
    #endregion

    #region Overlay — Position Animation

    private void AnimatePositionOverlay(TimeSpan position)
    {
        double seconds = position.TotalSeconds;
        FrameNumberText.Text = $"Frame {(int)Math.Round(seconds * _videoFrameRate):000}";
        TimestampText.Text = position.ToString(@"hh\:mm\:ss\.fff");
        UpdateCornerAndProgressBarOverlays();
    }
    #endregion

    #region UpdateStatus
    private void UpdateStatus(string message)
    {
        if (StatusText != null)
            StatusText.Text = message;
    }
    #endregion
}
