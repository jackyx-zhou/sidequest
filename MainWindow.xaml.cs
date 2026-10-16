using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Sidequest;

public partial class MainWindow : Window
{
    public ObservableCollection<BrowserItem> Entries { get; } = new();
    private readonly ObservableCollection<string> _drives = new();
    private readonly XInputController _controller = new();
    private readonly DispatcherTimer _controllerTimer = new() { Interval = TimeSpan.FromMilliseconds(50) };
    private readonly DispatcherTimer _playbackTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private CancellationTokenSource? _thumbnailCancellation;
    private string _currentPath = @"C:\";
    private bool _updatingDrivePicker;
    private int _viewerMediaIndex;
    private List<BrowserItem> _mediaItems = new();
    private bool _videoOpened;
    private bool _videoPlaying;
    private bool _isSeeking;
    private bool _updatingProgress;
    private bool _isFullscreen;
    private bool _isVideoOnly;
    private bool _allowClose;
    private WindowStyle _previousWindowStyle;
    private ResizeMode _previousResizeMode;
    private WindowState _previousWindowState;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;
        DrivePicker.ItemsSource = _drives;
        _controller.Move += MoveSelection;
        _controller.Activate += ActivateSelection;
        _controller.BackPressed += BackAction;
        _controller.Previous += () => MoveMedia(-1);
        _controller.Next += () => MoveMedia(1);
        _controllerTimer.Tick += (_, _) => _controller.Poll();
        _playbackTimer.Tick += (_, _) => UpdatePlaybackTime();
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        ToggleFullscreen();
        RefreshDriveList();
        await LoadDirectoryAsync(_currentPath);
        _controllerTimer.Start();
        _playbackTimer.Start();
        EntriesList.Focus();
    }

    private void RefreshDriveList()
    {
        _updatingDrivePicker = true;
        _drives.Clear();
        foreach (var drive in DriveInfo.GetDrives())
        {
            // Keep locked or not-ready volumes visible so they can be unlocked or mounted first.
            _drives.Add(drive.Name);
        }
        var currentRoot = Path.GetPathRoot(_currentPath) ?? @"C:\";
        if (!_drives.Contains(currentRoot, StringComparer.OrdinalIgnoreCase)) _drives.Insert(0, currentRoot);
        DrivePicker.SelectedItem = _drives.FirstOrDefault(d => string.Equals(d, currentRoot, StringComparison.OrdinalIgnoreCase));
        _updatingDrivePicker = false;
        UpdateBreadcrumbs();
    }

    private async Task LoadDirectoryAsync(string path)
    {
        _thumbnailCancellation?.Cancel();
        _thumbnailCancellation?.Dispose();
        _thumbnailCancellation = null;
        PreviewProgress.Visibility = Visibility.Collapsed;
        PreviewProgressText.Visibility = Visibility.Collapsed;

        string fullPath;
        try { fullPath = Path.GetFullPath(path.Trim().Trim('"')); }
        catch (Exception ex) { StatusText.Text = $"Invalid path: {ex.Message}"; return; }
        if (!Directory.Exists(fullPath))
        {
            StatusText.Text = $"Can't open {fullPath}. If it is encrypted, unlock it in Windows or mount it in VeraCrypt, then refresh drives.";
            return;
        }

        _thumbnailCancellation = new CancellationTokenSource();
        var token = _thumbnailCancellation.Token;
        _currentPath = Path.TrimEndingDirectorySeparator(fullPath);
        if (string.IsNullOrEmpty(_currentPath)) _currentPath = Path.GetPathRoot(fullPath) ?? @"C:\";
        PathLabel.Text = _currentPath;
        UpdateBreadcrumbs();
        Entries.Clear();
        StatusText.Text = "Reading folder…";

        List<BrowserItem> items;
        string? error;
        try { (items, error) = await Task.Run(() => EnumerateDirectory(_currentPath, token), token); }
        catch (OperationCanceledException) { return; }
        if (token.IsCancellationRequested) return;
        foreach (var item in items) Entries.Add(item);
        EntriesList.SelectedIndex = items.Count > 0 ? 0 : -1;
        StatusText.Text = error is null ? $"{items.Count} items · loading Windows previews…" : $"{items.Count} items · {error}";

        var completed = 0;
        PreviewProgress.Minimum = 0;
        PreviewProgress.Maximum = Math.Max(1, items.Count);
        PreviewProgress.Value = 0;
        PreviewProgress.Visibility = items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        PreviewProgressText.Visibility = items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        PreviewProgressText.Text = items.Count > 0 ? $"0 / {items.Count} previews" : "";

        using var gate = new SemaphoreSlim(4);
        var jobs = items.Select(async item =>
        {
            var acquired = false;
            try
            {
                await gate.WaitAsync(token);
                acquired = true;
                var preview = await Task.Run(() => ShellPreviewService.GetPreview(item.FullPath, item.IsDirectory, 640), token);
                if (preview is not null && !token.IsCancellationRequested)
                    await Dispatcher.InvokeAsync(() => item.Thumbnail = preview, DispatcherPriority.Background, token);
            }
            catch (OperationCanceledException) { }
            catch { }
            finally
            {
                if (acquired) gate.Release();
                var done = Interlocked.Increment(ref completed);
                if (!token.IsCancellationRequested && items.Count > 0)
                {
                    try
                    {
                        await Dispatcher.InvokeAsync(() =>
                        {
                            PreviewProgress.Value = done;
                            PreviewProgressText.Text = $"{done} / {items.Count} previews";
                        }, DispatcherPriority.Background, token);
                    }
                    catch (OperationCanceledException) { }
                }
            }
        }).ToArray();
        await Task.WhenAll(jobs);
        if (!token.IsCancellationRequested)
        {
            PreviewProgress.Visibility = Visibility.Collapsed;
            PreviewProgressText.Visibility = Visibility.Collapsed;
            StatusText.Text = error is null ? $"{items.Count} items · {_currentPath}" : $"{items.Count} items · some entries could not be read";
            EntriesList.Focus();
        }
    }

    private static (List<BrowserItem> Items, string? Error) EnumerateDirectory(string path, CancellationToken token)
    {
        var result = new List<BrowserItem>();
        string? error = null;
        try
        {
            foreach (var child in Directory.EnumerateFileSystemEntries(path, "*", SearchOption.TopDirectoryOnly))
            {
                token.ThrowIfCancellationRequested();
                try { result.Add(BrowserItem.FromPath(child)); }
                catch (UnauthorizedAccessException) { error = "some entries are protected"; }
                catch (IOException) { error = "some entries are unavailable"; }
            }
        }
        catch (UnauthorizedAccessException) { error = "access denied"; }
        catch (DirectoryNotFoundException) { error = "folder no longer exists"; }
        catch (IOException) { error = "folder could not be read"; }
        result.Sort((a, b) =>
        {
            if (a.IsDirectory != b.IsDirectory) return a.IsDirectory ? -1 : 1;
            return StringComparer.CurrentCultureIgnoreCase.Compare(a.Name, b.Name);
        });
        return (result, error);
    }

    private void UpdateBreadcrumbs()
    {
        Breadcrumbs.Children.Clear();
        var root = Path.GetPathRoot(_currentPath) ?? @"C:\";
        AddBreadcrumb(root, root);
        var current = root;
        foreach (var segment in _currentPath[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            var separator = new TextBlock { Text = "›", Foreground = (System.Windows.Media.Brush)FindResource("MutedBrush"), FontSize = 20, Margin = new Thickness(8, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
            Breadcrumbs.Children.Add(separator);
            AddBreadcrumb(segment, current);
        }
    }

    private void AddBreadcrumb(string label, string path)
    {
        var button = new Button { Content = label, Tag = path, FontSize = 14, Padding = new Thickness(10, 6, 10, 6), Margin = new Thickness(0, 0, 4, 0) };
        button.Click += async (_, _) => await LoadDirectoryAsync((string)button.Tag);
        Breadcrumbs.Children.Add(button);
    }

    private void UpdateTileColumns()
    {
        var grid = FindVisualChild<System.Windows.Controls.Primitives.UniformGrid>(EntriesList);
        if (grid is not null)
            grid.Columns = Math.Max(1, (int)(EntriesList.ActualWidth / 225));
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
            if (child is T result) return result;
            var nested = FindVisualChild<T>(child);
            if (nested is not null) return nested;
        }
        return null;
    }

    private void EntriesList_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateTileColumns();
    private async void Parent_Click(object sender, RoutedEventArgs e) => await GoParentAsync();
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await LoadDirectoryAsync(_currentPath);
    private async void RefreshDrives_Click(object sender, RoutedEventArgs e) { RefreshDriveList(); await LoadDirectoryAsync(_currentPath); }
    private async void DrivePicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_updatingDrivePicker && DrivePicker.SelectedItem is string drive) await LoadDirectoryAsync(drive);
    }

    private async Task GoParentAsync()
    {
        var parent = Directory.GetParent(_currentPath)?.FullName;
        if (parent is null) { StatusText.Text = "You are at the drive root."; return; }
        await LoadDirectoryAsync(parent);
    }

    private void EntriesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (EntriesList.SelectedItem is not BrowserItem item) return;
        if (item.IsDirectory) { StatusText.Text = item.FullPath; return; }
        StatusText.Text = $"{item.Name} · {item.Extension} · {FormatSize(item.Size)} · {item.Modified:g}";
    }

    private async void EntriesList_MouseDoubleClick(object sender, MouseButtonEventArgs e) => await ActivateSelectedAsync();
    private async void EntriesList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { e.Handled = true; await ActivateSelectedAsync(); }
        else if (e.Key == Key.Back) { e.Handled = true; await GoParentAsync(); }
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Q && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            e.Handled = true;
            ShowQuitConfirmation();
            return;
        }

        if (e.Key == Key.F11)
        {
            e.Handled = true;
            if (_isVideoOnly) ToggleVideoOnly();
            else ToggleFullscreen();
            return;
        }

        if (e.Key is Key.Up or Key.Down or Key.Left or Key.Right)
        {
            e.Handled = true;
            MoveSelection(e.Key switch
            {
                Key.Up => -1,
                Key.Down => 1,
                Key.Left => -2,
                _ => 2
            });
            return;
        }

        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            ActivateSelection();
            return;
        }

        if (ViewerOverlay.Visibility == Visibility.Visible)
        {
            if (e.Key == Key.Escape || e.Key == Key.Back)
            {
                e.Handled = true;
                if (QuitConfirmationOverlay.Visibility == Visibility.Visible) DismissQuitConfirmation();
                else if (_isVideoOnly) ToggleVideoOnly();
                else CloseViewer();
            }
            else if (e.Key == Key.Space && VideoPlayer.Visibility == Visibility.Visible) { e.Handled = true; TogglePlayback(); }
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            if (QuitConfirmationOverlay.Visibility == Visibility.Visible) DismissQuitConfirmation();
            else if (IsAtDriveRoot()) ShowQuitConfirmation();
            else _ = GoParentAsync();
        }
    }

    private async void ActivateSelection()
    {
        if (QuitConfirmationOverlay.Visibility == Visibility.Visible)
        {
            if (Keyboard.FocusedElement is Button quitDialogButton && quitDialogButton.IsEnabled)
                quitDialogButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, quitDialogButton));
            else
                DismissQuitConfirmation();
            return;
        }

        if (_isVideoOnly)
        {
            TogglePlayback();
            return;
        }

        if (ViewerOverlay.Visibility == Visibility.Visible)
        {
            if (Keyboard.FocusedElement is Button viewerButton && viewerButton.IsEnabled)
                viewerButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, viewerButton));
            else
                TogglePlayback();
            return;
        }

        if (DrivePicker.IsKeyboardFocusWithin)
        {
            DrivePicker.IsDropDownOpen = !DrivePicker.IsDropDownOpen;
            return;
        }
        if (Keyboard.FocusedElement is Button toolbarButton && toolbarButton.IsEnabled)
        {
            toolbarButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, toolbarButton));
            return;
        }
        await ActivateSelectedAsync();
    }
    private async Task ActivateSelectedAsync()
    {
        if (ViewerOverlay.Visibility == Visibility.Visible)
        {
            if (VideoPlayer.Visibility == Visibility.Visible) TogglePlayback();
            return;
        }
        if (EntriesList.SelectedItem is not BrowserItem item) return;
        if (item.IsDirectory) await LoadDirectoryAsync(item.FullPath);
        else if (MediaTypes.IsMedia(item.Name)) OpenMedia(item);
        else StatusText.Text = $"Preview is not supported for {item.Extension} files.";
    }

    private void MoveSelection(int direction)
    {
        if (QuitConfirmationOverlay.Visibility == Visibility.Visible)
        {
            var quitControls = GetQuitConfirmationControls();
            var focusedQuitControl = quitControls.FirstOrDefault(control => control.IsKeyboardFocusWithin);
            MoveControlFocus(quitControls, focusedQuitControl, direction is -1 or -2 ? -1 : 1);
            return;
        }

        if (ViewerOverlay.Visibility == Visibility.Visible)
        {
            if (_isVideoOnly)
            {
                if (direction is -2 or 2) SeekBy(direction < 0 ? -5 : 5);
                return;
            }

            var viewerControls = GetViewerControls();
            var focusedViewerControl = viewerControls.FirstOrDefault(control => control.IsKeyboardFocusWithin);
            if (focusedViewerControl == PlaybackProgress && direction is -2 or 2)
                SeekBy(direction < 0 ? -5 : 5);
            else
                MoveControlFocus(viewerControls, focusedViewerControl, direction is -1 or -2 ? -1 : 1);
            return;
        }

        var toolbarControls = GetToolbarControls();
        var focusedToolbarControl = toolbarControls.FirstOrDefault(control => control.IsKeyboardFocusWithin);
        if (focusedToolbarControl is not null)
        {
            if (focusedToolbarControl == DrivePicker && DrivePicker.IsDropDownOpen && direction is -1 or 1)
            {
                if (_drives.Count > 0)
                    DrivePicker.SelectedIndex = (DrivePicker.SelectedIndex + direction + _drives.Count) % _drives.Count;
            }
            else if (direction == 1)
            {
                EntriesList.Focus();
                if (EntriesList.SelectedIndex < 0 && Entries.Count > 0) EntriesList.SelectedIndex = 0;
            }
            else if (direction is -2 or 2)
            {
                MoveControlFocus(toolbarControls, focusedToolbarControl, direction < 0 ? -1 : 1);
            }
            return;
        }

        var columns = Math.Max(1, (int)(EntriesList.ActualWidth / 225));
        var index = Math.Max(0, EntriesList.SelectedIndex);
        if (direction == -1 && index < columns)
        {
            DrivePicker.Focus();
            return;
        }
        if (Entries.Count == 0) return;
        index = direction switch
        {
            -1 => index - columns,
            1 => index + columns,
            -2 => index - 1,
            _ => index + 1
        };
        EntriesList.SelectedIndex = Math.Clamp(index, 0, Entries.Count - 1);
        EntriesList.ScrollIntoView(EntriesList.SelectedItem);
        EntriesList.Focus();
    }

    private Control[] GetToolbarControls() =>
    [DrivePicker, ParentButton, RefreshButton, RefreshDrivesButton];

    private Control[] GetViewerControls() =>
    (new Control[] { ViewerBackButton, PreviousButton, NextButton, VideoOnlyButton, FullScreenButton,
        PlaybackProgress, PlayPauseButton, SeekBackButton, SeekForwardButton })
        .Where(control => control.Visibility == Visibility.Visible && control.IsEnabled)
        .ToArray();

    private Control[] GetQuitConfirmationControls() => [KeepBrowsingButton, QuitButton];

    private static void MoveControlFocus(Control[] controls, Control? focused, int direction)
    {
        if (controls.Length == 0) return;
        var index = Array.IndexOf(controls, focused);
        var next = index < 0 ? (direction > 0 ? 0 : controls.Length - 1) : Math.Clamp(index + direction, 0, controls.Length - 1);
        controls[next].Focus();
    }

    private void BackAction()
    {
        if (QuitConfirmationOverlay.Visibility == Visibility.Visible) DismissQuitConfirmation();
        else if (_isVideoOnly) ToggleVideoOnly();
        else if (ViewerOverlay.Visibility == Visibility.Visible) CloseViewer();
        else if (IsAtDriveRoot()) ShowQuitConfirmation();
        else _ = GoParentAsync();
    }

    private bool IsAtDriveRoot()
    {
        var root = Path.GetPathRoot(_currentPath);
        return root is not null && string.Equals(Path.GetFullPath(_currentPath), Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase);
    }

    private void ShowQuitConfirmation()
    {
        if (QuitConfirmationOverlay.Visibility == Visibility.Visible) return;
        QuitConfirmationOverlay.Visibility = Visibility.Visible;
        KeepBrowsingButton.Focus();
    }

    private void DismissQuitConfirmation()
    {
        QuitConfirmationOverlay.Visibility = Visibility.Collapsed;
        if (ViewerOverlay.Visibility == Visibility.Visible)
        {
            if (_isVideoOnly) ViewerOverlay.Focus();
            else if (VideoPlayer.Visibility == Visibility.Visible) PlayPauseButton.Focus();
            else FullScreenButton.Focus();
        }
        else EntriesList.Focus();
    }

    private void KeepBrowsing_Click(object sender, RoutedEventArgs e) => DismissQuitConfirmation();

    private void Quit_Click(object sender, RoutedEventArgs e)
    {
        _allowClose = true;
        Close();
    }

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_allowClose) return;
        e.Cancel = true;
        ShowQuitConfirmation();
    }

    private void OpenMedia(BrowserItem item)
    {
        _mediaItems = Entries.Where(e => !e.IsDirectory && MediaTypes.IsMedia(e.Name)).ToList();
        _viewerMediaIndex = _mediaItems.FindIndex(e => string.Equals(e.FullPath, item.FullPath, StringComparison.OrdinalIgnoreCase));
        ShowCurrentMedia();
    }

    private void ShowCurrentMedia()
    {
        if (_viewerMediaIndex < 0 || _viewerMediaIndex >= _mediaItems.Count) return;
        var item = _mediaItems[_viewerMediaIndex];
        _videoOpened = false;
        _videoPlaying = false;
        _isSeeking = false;
        ViewerOverlay.Visibility = Visibility.Visible;
        ViewerTitle.Text = item.Name;
        ViewerMessage.Text = "";
        PlaybackTime.Text = "";
        PlaybackProgress.Value = 0;
        PlaybackProgress.Maximum = 1;
        VideoPlayer.Stop();
        VideoPlayer.Source = null;
        StillImage.Source = null;
        if (item.IsImage)
        {
            VideoPlayer.Visibility = Visibility.Collapsed;
            PlayPauseButton.Visibility = Visibility.Collapsed;
            PlaybackProgress.Visibility = Visibility.Collapsed;
            VideoOnlyButton.Visibility = Visibility.Collapsed;
            StillImage.Visibility = Visibility.Visible;
            try
            {
                var image = new BitmapImage();
                image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad; image.UriSource = new Uri(item.FullPath); image.EndInit(); image.Freeze();
                StillImage.Source = image;
            }
            catch (Exception ex) { ViewerMessage.Text = $"Windows couldn't display this image.\n{ex.Message}"; }
        }
        else
        {
            StillImage.Visibility = Visibility.Collapsed;
            VideoPlayer.Visibility = Visibility.Visible;
            PlayPauseButton.Visibility = Visibility.Visible;
            PlaybackProgress.Visibility = Visibility.Visible;
            VideoOnlyButton.Visibility = Visibility.Visible;
            PlaybackProgress.IsEnabled = true;
            PlayPauseButton.Content = "▶ Play";
            try { VideoPlayer.Source = new Uri(item.FullPath); VideoPlayer.Play(); _videoPlaying = true; PlayPauseButton.Content = "Ⅱ Pause"; }
            catch (Exception ex) { ShowCodecMessage(ex.Message); }
        }
        if (VideoPlayer.Visibility == Visibility.Visible) PlayPauseButton.Focus();
        else FullScreenButton.Focus();
    }

    private void CloseViewer()
    {
        if (_isVideoOnly) ToggleVideoOnly();
        VideoPlayer.Stop(); VideoPlayer.Source = null;
        ViewerOverlay.Visibility = Visibility.Collapsed;
        EntriesList.Focus();
    }

    private void MoveMedia(int amount)
    {
        if (_mediaItems.Count < 2) return;
        _viewerMediaIndex = (_viewerMediaIndex + amount + _mediaItems.Count) % _mediaItems.Count;
        ShowCurrentMedia();
    }

    private void PlayPause_Click(object sender, RoutedEventArgs e) => TogglePlayback();
    private void TogglePlayback()
    {
        if (VideoPlayer.Visibility != Visibility.Visible) return;
        if (_videoPlaying) { VideoPlayer.Pause(); _videoPlaying = false; PlayPauseButton.Content = "▶ Play"; }
        else { VideoPlayer.Play(); _videoPlaying = true; PlayPauseButton.Content = "Ⅱ Pause"; }
    }
    private void SeekBack_Click(object sender, RoutedEventArgs e) => SeekBy(-10);
    private void SeekForward_Click(object sender, RoutedEventArgs e) => SeekBy(10);

    private void SeekBy(double seconds)
    {
        if (!_videoOpened || !VideoPlayer.NaturalDuration.HasTimeSpan) return;
        var duration = VideoPlayer.NaturalDuration.TimeSpan.TotalSeconds;
        VideoPlayer.Position = TimeSpan.FromSeconds(Math.Clamp(VideoPlayer.Position.TotalSeconds + seconds, 0, duration));
    }

    private void PlaybackProgress_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updatingProgress || !_videoOpened || !VideoPlayer.NaturalDuration.HasTimeSpan) return;
        VideoPlayer.Position = TimeSpan.FromSeconds(Math.Clamp(e.NewValue, 0, VideoPlayer.NaturalDuration.TimeSpan.TotalSeconds));
    }

    private void PlaybackProgress_MouseDown(object sender, MouseButtonEventArgs e) => _isSeeking = true;
    private void PlaybackProgress_MouseUp(object sender, MouseButtonEventArgs e) => _isSeeking = false;

    private void VideoPlayer_MediaOpened(object sender, RoutedEventArgs e)
    {
        _videoOpened = true;
        ViewerMessage.Text = "";
        var durationSeconds = VideoPlayer.NaturalDuration.HasTimeSpan ? VideoPlayer.NaturalDuration.TimeSpan.TotalSeconds : 1;
        PlaybackProgress.Maximum = Math.Max(1, durationSeconds);
        PlaybackProgress.IsEnabled = true;
        UpdatePlaybackTime();
    }

    private void VideoPlayer_MediaFailed(object sender, ExceptionRoutedEventArgs e)
    {
        PlaybackProgress.IsEnabled = false;
        ShowCodecMessage(e.ErrorException?.Message ?? "Windows could not decode this video.");
    }

    private void ShowCodecMessage(string detail)
    {
        PlaybackProgress.IsEnabled = false;
        ViewerMessage.Text = $"Windows couldn't play this video. It may need a codec that isn't installed.\n\n{detail}";
    }

    private void UpdatePlaybackTime()
    {
        if (VideoPlayer.Visibility != Visibility.Visible || !_videoOpened) return;
        var duration = VideoPlayer.NaturalDuration.HasTimeSpan ? VideoPlayer.NaturalDuration.TimeSpan : TimeSpan.Zero;
        PlaybackTime.Text = $"{FormatClock(VideoPlayer.Position)} / {FormatClock(duration)}";
        if (!_isSeeking)
        {
            _updatingProgress = true;
            PlaybackProgress.Maximum = Math.Max(1, duration.TotalSeconds);
            PlaybackProgress.Value = Math.Clamp(VideoPlayer.Position.TotalSeconds, 0, PlaybackProgress.Maximum);
            _updatingProgress = false;
        }
    }

    private void Previous_Click(object sender, RoutedEventArgs e) => MoveMedia(-1);
    private void Next_Click(object sender, RoutedEventArgs e) => MoveMedia(1);
    private void FullScreen_Click(object sender, RoutedEventArgs e) => ToggleFullscreen();
    private void VideoOnly_Click(object sender, RoutedEventArgs e) => ToggleVideoOnly();
    private void CloseViewer_Click(object sender, RoutedEventArgs e) => CloseViewer();

    private void ToggleVideoOnly()
    {
        _isVideoOnly = !_isVideoOnly;
        foreach (UIElement child in Root.Children)
        {
            if (!ReferenceEquals(child, ViewerOverlay)) child.Visibility = _isVideoOnly ? Visibility.Collapsed : Visibility.Visible;
        }
        ViewerHeader.Visibility = _isVideoOnly ? Visibility.Collapsed : Visibility.Visible;
        ViewerFooter.Visibility = _isVideoOnly ? Visibility.Collapsed : Visibility.Visible;
        ViewerOverlay.Background = _isVideoOnly
            ? System.Windows.Media.Brushes.Black
            : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(242, 11, 13, 18));
        VideoOnlyButton.Content = _isVideoOnly ? "Exit video fullscreen" : "Video fullscreen";
        if (_isVideoOnly) ViewerOverlay.Focus();
    }

    private void ToggleFullscreen()
    {
        if (!_isFullscreen)
        {
            _previousWindowStyle = WindowStyle;
            _previousResizeMode = ResizeMode;
            _previousWindowState = WindowState;
            WindowState = WindowState.Normal;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            WindowState = WindowState.Maximized;
            _isFullscreen = true;
            FullScreenButton.Content = "Exit full screen";
        }
        else
        {
            WindowState = WindowState.Normal;
            WindowStyle = _previousWindowStyle;
            ResizeMode = _previousResizeMode;
            WindowState = _previousWindowState;
            _isFullscreen = false;
            FullScreenButton.Content = "Full screen";
        }
    }

    private static string FormatClock(TimeSpan time) => time.TotalHours >= 1 ? time.ToString(@"h\:mm\:ss") : time.ToString(@"mm\:ss");
    private static string FormatSize(long size) => size < 1024 ? $"{size} B" : size < 1024 * 1024 ? $"{size / 1024d:0.0} KB" : size < 1024L * 1024 * 1024 ? $"{size / (1024d * 1024):0.0} MB" : $"{size / (1024d * 1024 * 1024):0.0} GB";
}
