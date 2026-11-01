using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Controls;
using System.Windows.Threading;
using System.Windows.Media;
using Microsoft.Win32;
using NAudio.CoreAudioApi;
namespace FreeTapes;
public partial class MainWindow : Window
{
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    private readonly AudioEngine audio = new();
    private readonly LibraryStore store = new();
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(20) };
    private readonly DispatcherTimer volumeSaveTimer = new() { Interval = TimeSpan.FromMilliseconds(350) };
    private readonly TalkBinding talkBinding = new();
    private readonly PlaybackBindings playbackBindings = new();
    private PlaybackAction? captureAction;
    private string? currentTapePath;
    private float currentSongGain = 1;
    private string? currentStreamUrl;
    private ProximityInput? proximity;
    private int capturedMouseButton;
    private CancellationTokenSource? download;
    private CancellationTokenSource? streamRequest;
    private bool closing;
    private string SettingsPath => Path.Combine(store.Root, "settings.json");
    public MainWindow()
    {
        InitializeComponent();
        PreviewKeyDown += CaptureKey;
        PreviewMouseDown += CaptureMouse;
        PreviewMouseUp += (_, e) =>
        {
            if (MouseVirtualKey(e.ChangedButton) == capturedMouseButton && capturedMouseButton != 0)
            { capturedMouseButton = 0; e.Handled = true; }
        };
        Deactivated += (_, _) => { if (talkBinding.Capturing) CancelBind(); };
        audio.Error += message => Dispatcher.BeginInvoke(() => { proximity?.Release(); audio.Disconnect(); Status.Text = "Audio disconnected: " + message; });
        Run(() => proximity = new ProximityInput());
        AppDomain.CurrentDomain.ProcessExit += ReleaseOnExit;
        AppDomain.CurrentDomain.UnhandledException += ReleaseOnException;
        Application.Current.DispatcherUnhandledException += ReleaseOnDispatcherException;
        Application.Current.SessionEnding += ReleaseOnSessionEnd;
        Refresh(null, null); LoadSettings(); Reload(); UpdatePlaybackLabels();
        MusicVolume.ValueChanged += OutputVolumeChanged;
        HeadphoneVolume.ValueChanged += OutputVolumeChanged;
        timer.Tick += (_, _) => { PollPlaybackBindings(); UpdateAudioAndProximity(); };
        volumeSaveTimer.Tick += (_, _) => { volumeSaveTimer.Stop(); Run(store.SavePendingChanges); SaveSettings(); };
        AutoProximity.Click += (_, _) => { UpdateAudioAndProximity(); SaveSettings(); };
        timer.Start();
        Closing += (_, e) =>
        {
            proximity?.Release();
            if(download != null || streamRequest != null) { e.Cancel = true; closing = true; download?.Cancel(); streamRequest?.Cancel(); Status.Text = "Cancelling pending work before closing…"; return; }
            timer.Stop(); volumeSaveTimer.Stop(); Run(store.SavePendingChanges); SaveSettings();
            try { audio.Dispose(); } finally { proximity?.Dispose(); }
            AppDomain.CurrentDomain.ProcessExit -= ReleaseOnExit;
            AppDomain.CurrentDomain.UnhandledException -= ReleaseOnException;
            Application.Current.DispatcherUnhandledException -= ReleaseOnDispatcherException;
            Application.Current.SessionEnding -= ReleaseOnSessionEnd;
        };
    }
    private void ReleaseOnExit(object? sender, EventArgs e) => proximity?.Release();
    private void ReleaseOnException(object sender, UnhandledExceptionEventArgs e) => proximity?.Release();
    private void ReleaseOnDispatcherException(object sender, DispatcherUnhandledExceptionEventArgs e) => proximity?.Release();
    private void ReleaseOnSessionEnd(object sender, SessionEndingCancelEventArgs e) => proximity?.Release();
    private void UpdateAudioAndProximity()
    {
        PlayPauseButton.Content = streamRequest != null ? "Connecting…" : audio.HasActivePlayback && !audio.IsPaused ? "⏸ Pause" : "▶ Play";
        PlayPauseButton.IsEnabled = streamRequest == null;
        var down = proximity?.IsTalkInputDown(talkBinding.VirtualKey) ?? ((GetAsyncKeyState(talkBinding.VirtualKey) & 0x8000) != 0);
        var talking = !closing && talkBinding.IsTalking(down);
        audio.SetLevels((float)MusicVolume.Value / 100, (float)HeadphoneVolume.Value / 100, (float)MicVolume.Value / 100, talking, (float)DuckVolume.Value, currentSongGain);
        TalkState.Text = talkBinding.Capturing ? "WAITING FOR BIND • MIC MUTED" : talking ? "● SPEAKING • MUSIC DUCKED" : "MIC MUTED";
        var ok = proximity?.Update(!closing && AutoProximity.IsChecked == true, audio.IsRouted, talkBinding.Capturing, audio.IsMusicPlaying, talking) ?? false;
        ProximityState.Text = !ok ? "Automatic K unavailable • use game PTT manually"
            : proximity?.Holding == true ? "K held • game PTT requested"
            : AutoProximity.IsChecked != true ? "Automatic K off" : "K released • waiting for WARDOGS or audio";
    }
    private void ChangeBind(object sender, RoutedEventArgs e)
        => BeginBind(null);
    private void BeginBind(PlaybackAction? action)
    {
        captureAction = action;
        talkBinding.BeginCapture();
        proximity?.Release();
        audio.SetLevels((float)MusicVolume.Value / 100, (float)HeadphoneVolume.Value / 100, (float)MicVolume.Value / 100, false, (float)DuckVolume.Value, currentSongGain);
        ChangeBindButton.Content = action == null ? "Listening…" : "Change bind";
        ShowBindPrompt("Press a key or mouse button here. Esc cancels.");
        UpdatePlaybackLabels();
        // Keep Space/Enter from reactivating the button after capture.
        Keyboard.ClearFocus(); Focus();
    }
    private void CaptureKey(object sender, KeyEventArgs e)
    {
        if (!talkBinding.Capturing) return;
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Escape) { CancelBind(); return; }
        if (key != Key.None) CompleteBind(KeyInterop.VirtualKeyFromKey(key));
    }
    private static int MouseVirtualKey(MouseButton button) => button switch
    {
        MouseButton.Left => 0x01, MouseButton.Right => 0x02, MouseButton.Middle => 0x04,
        MouseButton.XButton1 => 0x05, MouseButton.XButton2 => 0x06, _ => 0
    };
    private void CaptureMouse(object sender, MouseButtonEventArgs e)
    {
        if (!talkBinding.Capturing) return;
        e.Handled = true;
        capturedMouseButton = MouseVirtualKey(e.ChangedButton);
        CompleteBind(capturedMouseButton);
    }
    private void CompleteBind(int virtualKey)
    {
        if (virtualKey is < 1 or > 254) return;
        if (captureAction is PlaybackAction action)
        {
            if (virtualKey == 0x4B || virtualKey == talkBinding.VirtualKey || !playbackBindings.Set(action, virtualKey))
            { ShowBindPrompt("That input is already assigned or reserved for K. Choose another."); return; }
            talkBinding.CancelCapture();
        }
        else
        {
            if (playbackBindings.Conflicts(virtualKey)) { ShowBindPrompt("That input is assigned to playback. Choose another."); return; }
            if (!talkBinding.Set(virtualKey)) return;
        }
        captureAction = null;
        UpdateBindLabel();
        UpdatePlaybackLabels();
        ChangeBindButton.Content = "Change bind";
        ShowBindPrompt("Saved. Release the input before using it.");
        SaveSettings();
    }
    private void CancelBind()
    {
        talkBinding.CancelCapture();
        captureAction = null;
        UpdatePlaybackLabels();
        ChangeBindButton.Content = "Change bind";
        ShowBindPrompt("Binding unchanged. Keyboard or mouse button.");
    }
    private void ShowBindPrompt(string message) { BindPrompt.Text = message; PlaybackPrompt.Text = message; }
    private void UpdateBindLabel() => TalkBindingLabel.Text = BindingLabel(talkBinding.VirtualKey);
    private static string BindingLabel(int virtualKey) => virtualKey switch
    {
        0 => "Unbound",
        0x01 => "Mouse left", 0x02 => "Mouse right", 0x04 => "Mouse middle",
        0x05 => "Mouse side 1", 0x06 => "Mouse side 2",
        var key => KeyInterop.KeyFromVirtualKey(key).ToString()
    };
    private sealed record PlaybackBindingRow(PlaybackAction Action, string Caption, string Label);
    private void UpdatePlaybackLabels() => TransportBindings.ItemsSource = Enum.GetValues<PlaybackAction>()
        .Select(action => new PlaybackBindingRow(action, action == PlaybackAction.Play ? "Play/pause" : action == PlaybackAction.Pause ? "Alt. toggle" : action.ToString(), captureAction == action ? "Listening…" : BindingLabel(playbackBindings[action]))).ToList();
    private void BindPlayback(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: PlaybackAction action }) BeginBind(action);
    }
    private void ClearPlayback(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: PlaybackAction action }) return;
        CancelBind(); playbackBindings.Set(action, 0); UpdatePlaybackLabels(); SaveSettings();
    }
    private void PollPlaybackBindings()
    {
        bool Down(int key) => proximity?.IsTalkInputDown(key) ?? ((GetAsyncKeyState(key) & 0x8000) != 0);
        foreach (var action in playbackBindings.Poll(Down, !closing && !talkBinding.Capturing && proximity?.GameHasFocus == true))
            Run(() => ExecutePlayback(action));
    }
    private Tape? SelectedSavedSong => Library.SelectedItem as Tape ?? OutsideLibrary.SelectedItem as Tape;
    private void Reload() => Run(() =>
    {
        var selected = (Library.SelectedItem as Tape)?.Path; var outside = (OutsideLibrary.SelectedItem as Tape)?.Path;
        var queued = store.Playlist(); var available = store.OutsidePlaylist();
        Library.ItemsSource = queued; OutsideLibrary.ItemsSource = available;
        if(selected != null) Library.SelectedItem = queued.FirstOrDefault(t => t.Path == selected);
        if(outside != null) OutsideLibrary.SelectedItem = available.FirstOrDefault(t => t.Path == outside);
        PlaylistHeading.Text = "PLAYLIST (" + queued.Count + ")";
        OutsideHeading.Text = "NOT IN PLAYLIST (" + available.Count + ")";
    });
    private void PlaylistSelectionChanged(object sender, SelectionChangedEventArgs e) { if(Library.SelectedItem != null) OutsideLibrary.SelectedItem = null; }
    private void OutsideSelectionChanged(object sender, SelectionChangedEventArgs e) { if(OutsideLibrary.SelectedItem != null) Library.SelectedItem = null; }
    private void PlaylistDoubleClick(object sender, MouseButtonEventArgs e)
    {
        for(DependencyObject? node = e.OriginalSource as DependencyObject; node != null;)
        {
            if(node is Button or Slider) return;
            node = node is Visual ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);
        }
        Play(sender, e);
    }
    private void SongVolumeLoaded(object sender, RoutedEventArgs e)
    {
        if(sender is not Slider slider) return;
        slider.ValueChanged -= SongVolumeChanged;
        slider.ValueChanged += SongVolumeChanged;
    }
    private void OutputVolumeChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        UpdateAudioAndProximity();
        volumeSaveTimer.Stop(); volumeSaveTimer.Start();
    }
    private void SongVolumeUnloaded(object sender, RoutedEventArgs e)
    {
        if(sender is Slider slider) slider.ValueChanged -= SongVolumeChanged;
    }
    private void SongVolumeChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if(sender is not Slider { IsLoaded: true, DataContext: Tape tape }) return;
        var percent = Math.Round(e.NewValue);
        if(Math.Abs(tape.VolumePercent - percent) < 0.001) return;
        Run(() =>
        {
            store.SetSongVolume(tape.Path, percent, persist: false);
            tape.VolumePercent = percent;
            if(string.Equals(tape.Path, currentTapePath, StringComparison.OrdinalIgnoreCase))
            {
                currentSongGain = (float)percent / 100;
                UpdateAudioAndProximity();
            }
            volumeSaveTimer.Stop(); volumeSaveTimer.Start();
        });
    }
    private void StopIfCurrent(IEnumerable<Tape> tapes)
    {
        if(!tapes.Any(t => string.Equals(t.Path, currentTapePath, StringComparison.OrdinalIgnoreCase))) return;
        audio.Stop(); currentTapePath = null; NowPlaying.Text = "Playback stopped"; UpdateAudioAndProximity();
    }
    private void RemoveQueuedSong(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if(sender is not Button { Tag: Tape tape }) return;
        Run(() => { store.RemoveFromPlaylist(tape.Path); StopIfCurrent([tape]); Reload(); Status.Text = "Removed from playlist. The song is available on the right."; });
    }
    private void AddQueuedSong(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if(sender is not Button { Tag: Tape tape }) return;
        Run(() => { store.AddToPlaylist(tape.Path); Reload(); Library.SelectedItem = Library.Items.Cast<Tape>().FirstOrDefault(t => t.Path == tape.Path); Status.Text = "Added to the end of the playlist."; });
    }
    private void DeleteSong(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if(sender is not Button { Tag: Tape tape }) return;
        Run(() =>
        {
            if(download != null) throw new InvalidOperationException("Finish or cancel the current save before deleting songs.");
            var files = store.Family(tape.Path);
            var names = string.Join("\n", files.Take(12).Select(t => "• " + Path.GetFileName(t.Path)));
            if(files.Count > 12) names += "\n…and " + (files.Count - 12) + " more files.";
            var prompt = "Permanently delete these " + files.Count + " saved audio file(s), including the original and linked trimmed copies?\n\n" + names
                + "\n\nThey will also be removed from the playlist. Files imported from outside Free Tapes are left in their original location.";
            if(MessageBox.Show(this, prompt, "Delete song and its versions", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
            StopIfCurrent(files);
            try { store.DeleteFamily(tape.Path, files.Select(t => t.Path).ToArray()); Status.Text = "Deleted " + files.Count + " library audio file(s)."; }
            finally { Reload(); }
        });
    }
    private void Run(Action action) { try { action(); } catch(Exception e) { Status.Text = e.Message; } }
    private void Refresh(object? sender, RoutedEventArgs? e) => Run(() =>
    {
        Mic.ItemsSource = audio.Devices(DataFlow.Capture); Output.ItemsSource = audio.Devices(DataFlow.Render);
        Mic.SelectedIndex = 0; Monitor.ItemsSource = audio.Devices(DataFlow.Render); Monitor.SelectedItem = ((List<Device>)Monitor.ItemsSource).FirstOrDefault(d => !d.Name.Contains("CABLE", StringComparison.OrdinalIgnoreCase));
        Output.SelectedItem = ((List<Device>)Output.ItemsSource).FirstOrDefault(d => d.Name.Contains("CABLE Input", StringComparison.OrdinalIgnoreCase));
    });
    private void StartRouting(object sender, RoutedEventArgs e) => Run(() =>
    {
        if(Mic.SelectedItem is not Device mic || Output.SelectedItem is not Device output || Monitor.SelectedItem is not Device headphones) throw new InvalidOperationException("Select your microphone and a virtual cable playback device.");
        if(output.Id == headphones.Id) throw new InvalidOperationException("Choose separate headphones and game-feed outputs to prevent doubled music."); proximity?.Release(); audio.Start(mic.Id, output.Id, headphones.Id); Status.Text = "Routing active → " + output.Name; SaveSettings();
    });
    private void Disconnect(object sender, RoutedEventArgs e) { streamRequest?.Cancel(); proximity?.Release(); audio.Disconnect(); UpdateAudioAndProximity(); Status.Text = "Disconnected"; }
    private void Play(object sender, RoutedEventArgs e) => Run(() =>
    {
        if(Library.SelectedItem is Tape tape) { StartTape(tape); UpdateAudioAndProximity(); }
    });
    private void TogglePlayback(object sender, RoutedEventArgs e) => Run(() => ExecutePlayback(PlaybackAction.Play));
    private void Stop(object sender, RoutedEventArgs e) => Run(() => ExecutePlayback(PlaybackAction.Stop));
    private void Next(object sender, RoutedEventArgs e) => Run(() => ExecutePlayback(PlaybackAction.Next));
    private void ExecutePlayback(PlaybackAction action)
    {
        var tapes = Library.Items.Cast<Tape>().ToList();
        switch (action)
        {
            case PlaybackAction.Play:
            case PlaybackAction.Pause: // Preserve both existing shortcuts as aliases of the combined control.
                if(streamRequest != null) break; // Stop remains available to cancel a connection.
                if(audio.HasActivePlayback) { audio.Pause(); break; }
                if(OutsideLibrary.SelectedItem != null) { Status.Text = "Add this song to the playlist before playing it."; break; }
                if (currentStreamUrl != null && Library.SelectedItem == null)
                {
                    if(streamRequest == null) _ = StartYouTubeStream(currentStreamUrl);
                    break;
                }
                var tape = Library.SelectedItem as Tape ?? tapes.FirstOrDefault(t => t.Path == currentTapePath) ?? tapes.FirstOrDefault();
                if (tape == null) { Status.Text = "The playlist is empty. Add a song from the right."; break; }
                StartTape(tape);
                break;
            case PlaybackAction.Stop:
                streamRequest?.Cancel();
                audio.Stop(); NowPlaying.Text = "Playback stopped"; break;
            case PlaybackAction.Next:
                var index = PlaybackBindings.NextIndex(tapes.Count, tapes.FindIndex(t => t.Path == currentTapePath), Library.SelectedIndex);
                if (index < 0) { Status.Text = "The playlist is empty. Add a song from the right."; break; }
                StartTape(tapes[index]); break;
        }
        UpdateAudioAndProximity();
    }
    private void StartTape(Tape tape)
    {
        streamRequest?.Cancel();
        audio.Play(tape.Path);
        currentSongGain = (float)store.SongVolume(tape.Path) / 100;
        currentStreamUrl = null;
        currentTapePath = tape.Path; NowPlaying.Text = tape.Title;
        Library.SelectedItem = tape; Library.ScrollIntoView(tape);
    }
    private void Import(object sender, RoutedEventArgs e) => Run(() =>
    {
        var dialog = new OpenFileDialog { Filter = "Audio|*.mp3;*.wav;*.m4a", Multiselect = true };
        if(dialog.ShowDialog() == true) { foreach(var path in dialog.FileNames) store.Import(path); Reload(); Status.Text = "Audio added to your library."; }
    });
    private async void Download(object sender, RoutedEventArgs e)
    {
        if(download != null) return;
        download = new CancellationTokenSource(); DownloadButton.IsEnabled = false; TrimButton.IsEnabled = false; CancelButton.IsEnabled = true;
        Status.Text = "Downloading and converting to MP3…";
        try { var range = ClipRange.Parse(TrimStart.Text, TrimEnd.Text); await store.Download(Url.Text.Trim(), download.Token, range); Reload(); Status.Text = "Saved to your library. Available offline."; }
        catch(OperationCanceledException) { Status.Text = "Import cancelled."; }
        catch(Exception error) { Status.Text = error.Message; }
        finally { download.Dispose(); download = null; DownloadButton.IsEnabled = true; TrimButton.IsEnabled = true; CancelButton.IsEnabled = false; if(closing && streamRequest == null) Close(); }
    }
    private async void TrimSelected(object sender, RoutedEventArgs e)
    {
        if(download != null) return;
        if(SelectedSavedSong is not Tape tape) { Status.Text = "Select a saved song to trim."; return; }
        download = new CancellationTokenSource(); DownloadButton.IsEnabled = false; TrimButton.IsEnabled = false; CancelButton.IsEnabled = true;
        try
        {
            var range = ClipRange.Parse(TrimStart.Text, TrimEnd.Text);
            Status.Text = "Saving a trimmed MP3 copy…";
            var path = await MediaTools.ExportTrim(tape.Path, store.Music, range, download.Token);
            store.RegisterSaved(path, Path.GetFileName(tape.Path));
            Reload(); Library.SelectedItem = Library.Items.Cast<Tape>().FirstOrDefault(t => t.Path == path);
            Status.Text = "Trimmed MP3 saved. Your original song is unchanged.";
        }
        catch(OperationCanceledException) { Status.Text = "Trim cancelled."; }
        catch(Exception error) { Status.Text = error.Message; }
        finally { download.Dispose(); download = null; DownloadButton.IsEnabled = true; TrimButton.IsEnabled = true; CancelButton.IsEnabled = false; if(closing && streamRequest == null) Close(); }
    }
    private async void StreamYouTube(object sender, RoutedEventArgs e) => await StartYouTubeStream(Url.Text.Trim());
    private async Task StartYouTubeStream(string url)
    {
        if(!audio.IsRouted) { Status.Text = "Start audio routing before streaming."; return; }
        streamRequest?.Cancel();
        var request = new CancellationTokenSource(); streamRequest = request; StreamButton.IsEnabled = false;
        audio.Stop(); UpdateAudioAndProximity();
        Status.Text = "Connecting to YouTube… Stop cancels. No MP3 will be saved.";
        try
        {
            var media = await MediaTools.Resolve(url, request.Token);
            request.Token.ThrowIfCancellationRequested();
            audio.PlayStream(media);
            currentTapePath = null; currentSongGain = 1; currentStreamUrl = url; Library.SelectedItem = null; OutsideLibrary.SelectedItem = null;
            NowPlaying.Text = "Streaming • " + media.Title;
            Status.Text = "Streaming full track. Trim fields apply only to MP3 saves.";
            UpdateAudioAndProximity();
        }
        catch(OperationCanceledException) { if(ReferenceEquals(streamRequest, request)) Status.Text = "Stream connection cancelled."; }
        catch(Exception error) { if(ReferenceEquals(streamRequest, request)) Status.Text = error.Message; }
        finally
        {
            if(ReferenceEquals(streamRequest, request)) { streamRequest = null; StreamButton.IsEnabled = true; }
            request.Dispose();
            if(closing && download == null && streamRequest == null) Close();
        }
    }
    private void CancelDownload(object sender, RoutedEventArgs e) => download?.Cancel();
    private sealed record Settings(string? Mic, string? Output, Key Key, double Music, double Gain, double Duck, string? Monitor, int? TalkVirtualKey = null, bool AutoProximity = true, Dictionary<PlaybackAction, int>? PlaybackKeys = null, double? HeadphoneMusic = null);
    private void SaveSettings() => Run(() => File.WriteAllText(SettingsPath, JsonSerializer.Serialize(new Settings((Mic.SelectedItem as Device)?.Id, (Output.SelectedItem as Device)?.Id, Key.V, MusicVolume.Value, MicVolume.Value, DuckVolume.Value, (Monitor.SelectedItem as Device)?.Id, talkBinding.VirtualKey, AutoProximity.IsChecked == true, Enum.GetValues<PlaybackAction>().ToDictionary(action => action, action => playbackBindings[action]), HeadphoneVolume.Value))));
    private void LoadSettings() => Run(() =>
    {
        if(!File.Exists(SettingsPath)) return;
        var s = JsonSerializer.Deserialize<Settings>(File.ReadAllText(SettingsPath)); if(s == null) return;
        Mic.SelectedItem = ((List<Device>)Mic.ItemsSource).FirstOrDefault(d => d.Id == s.Mic);
        Output.SelectedItem = ((List<Device>)Output.ItemsSource).FirstOrDefault(d => d.Id == s.Output);
        if(s.Monitor != null) Monitor.SelectedItem = ((List<Device>)Monitor.ItemsSource).FirstOrDefault(d => d.Id == s.Monitor);
        talkBinding.Set(s.TalkVirtualKey ?? KeyInterop.VirtualKeyFromKey(s.Key)); UpdateBindLabel();
        AutoProximity.IsChecked = s.AutoProximity;
        if (s.PlaybackKeys != null)
            foreach (var (action, key) in s.PlaybackKeys)
                if (Enum.IsDefined(action) && key != 0x4B && key != talkBinding.VirtualKey) playbackBindings.Set(action, key);
        MusicVolume.Value = s.Music; HeadphoneVolume.Value = s.HeadphoneMusic ?? s.Music; MicVolume.Value = s.Gain; DuckVolume.Value = s.Duck;
    });
}
