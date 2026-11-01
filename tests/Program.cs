using FreeTapes;
if(args.Length > 0 && args[0] == "--audio-diagnostics")
{
    using var enumerator = new NAudio.CoreAudioApi.MMDeviceEnumerator();
    var cableDevices = new List<NAudio.CoreAudioApi.MMDevice>();
    foreach(var flow in new[] { NAudio.CoreAudioApi.DataFlow.Render, NAudio.CoreAudioApi.DataFlow.Capture })
    {
        using(var normal = enumerator.GetDefaultAudioEndpoint(flow, NAudio.CoreAudioApi.Role.Communications))
            Console.WriteLine("Default communications " + flow + ": " + normal.FriendlyName);
        foreach(var device in enumerator.EnumerateAudioEndPoints(flow, NAudio.CoreAudioApi.DeviceState.Active))
        {
            Console.WriteLine(flow + " | " + device.FriendlyName + " | muted=" + device.AudioEndpointVolume.Mute + " | Windows level=" + Math.Round(device.AudioEndpointVolume.MasterVolumeLevelScalar * 100) + "%");
            if(device.FriendlyName.Contains("CABLE", StringComparison.OrdinalIgnoreCase)) cableDevices.Add(device); else device.Dispose();
        }
    }
    var peaks = new Dictionary<string, float>();
    Console.WriteLine("Reading cable peak meters for 10 seconds (no audio recording)…");
    for(var tick = 0; tick < 40; tick++)
    {
        foreach(var device in cableDevices) peaks[device.FriendlyName] = Math.Max(peaks.GetValueOrDefault(device.FriendlyName), device.AudioMeterInformation.MasterPeakValue);
        await Task.Delay(250);
    }
    foreach(var (name, peak) in peaks) Console.WriteLine(name + " | peak=" + (peak * 100).ToString("F2") + "%");
    foreach(var device in cableDevices) device.Dispose();
    return;
}
var catalogTestRoot = Path.Combine(Path.GetTempPath(), "FreeTapes-catalog-" + Guid.NewGuid().ToString("N"));
try
{
    var library = new LibraryStore(catalogTestRoot);
    var original = Path.Combine(library.Music, "Song.mp3");
    var unrelated = Path.Combine(library.Music, "Song remix.mp3");
    File.WriteAllText(original, "original"); File.WriteAllText(unrelated, "unrelated");
    if(library.Playlist().Count != 2) throw new Exception("Existing library migration failed.");
    if(library.SongVolume(original) != 100) throw new Exception("Existing songs must default to 100% volume.");
    library.SetSongVolume(original, 37); library.SetSongVolume(unrelated, 200);
    var volumeReload = new LibraryStore(catalogTestRoot);
    if(volumeReload.SongVolume(original) != 37 || volumeReload.SongVolume(unrelated) != 200
        || volumeReload.List().Single(t => t.Path == original).VolumePercent != 37)
        throw new Exception("Independent song volumes were not persisted/reloaded into song rows.");
    foreach(var invalid in new[] { 0d, 201, double.NaN, double.PositiveInfinity })
    {
        try { library.SetSongVolume(original, invalid); throw new Exception("Invalid song volume accepted."); } catch(ArgumentOutOfRangeException) { }
    }
    library.RemoveFromPlaylist(original);
    var reopened = new LibraryStore(catalogTestRoot);
    if(reopened.Playlist().Any(t => t.Path == original) || reopened.OutsidePlaylist().Single().Path != original || !File.Exists(original))
        throw new Exception("Playlist exclusion was not persisted independently of the file.");
    if(reopened.OutsidePlaylist().Single().VolumePercent != 37) throw new Exception("Removing from playlist lost the song volume.");
    reopened.AddToPlaylist(original); reopened.AddToPlaylist(original);
    if(reopened.Playlist().Count != 2 || reopened.Playlist().Last().Path != original) throw new Exception("Add should append once.");
    reopened.SetSongVolume(original, 1, persist: false); reopened.SavePendingChanges();
    if(new LibraryStore(catalogTestRoot).SongVolume(original) != 1 || reopened.SongVolume(unrelated) != 200) throw new Exception("Batched volume save or 1% boundary failed.");
    var trim = Path.Combine(library.Music, "Different trimmed filename.mp3");
    File.WriteAllText(trim, "trim"); reopened.RegisterSaved(trim, Path.GetFileName(original));
    var nested = Path.Combine(library.Music, "Nested trim.mp3");
    File.WriteAllText(nested, "trim of trim"); reopened.RegisterSaved(nested, Path.GetFileName(trim));
    var preview = reopened.Family(trim).Select(t => t.Path).ToArray();
    if(preview.Length != 3 || preview.Contains(unrelated)) throw new Exception("Original/trim lineage incorrect.");
    if(reopened.SongVolume(trim) != 100 || reopened.SongVolume(original) != 1) throw new Exception("Original and trimmed volumes must be independent.");
    try { reopened.DeleteFamily(trim, [trim]); throw new Exception("Changed deletion preview was accepted."); } catch(InvalidOperationException) { }
    var outside = Path.Combine(catalogTestRoot, "external-original.mp3"); File.WriteAllText(outside, "outside");
    try { reopened.DeleteFamily(outside); throw new Exception("Outside-library deletion accepted."); } catch(InvalidOperationException) { }
    reopened.DeleteFamily(trim, preview);
    if(preview.Any(File.Exists) || !File.Exists(unrelated) || !File.Exists(outside) || reopened.Playlist().Count != 1)
        throw new Exception("Family deletion removed the wrong files or left stale playlist entries.");
    File.WriteAllText(original, "replacement"); reopened.RegisterSaved(original);
    if(reopened.SongVolume(original) != 100 || reopened.SongVolume(unrelated) != 200) throw new Exception("Deleted song's volume was retained or affected another song.");
    var legacyOriginal = Path.Combine(library.Music, "Legacy.wav"); var legacyTrim = Path.Combine(library.Music, "Legacy [trim abcdef12].mp3");
    File.WriteAllText(legacyOriginal, "legacy"); File.WriteAllText(legacyTrim, "legacy trim");
    var migrated = new LibraryStore(catalogTestRoot);
    if(migrated.Family(legacyTrim).Count != 2) throw new Exception("Older trim filename migration failed.");
    var oldRoot = Path.Combine(catalogTestRoot, "old-version");
    var oldLibrary = new LibraryStore(oldRoot); var oldSong = Path.Combine(oldLibrary.Music, "Old.mp3");
    File.WriteAllText(oldSong, "old song");
    File.WriteAllText(Path.Combine(oldRoot, "library-state.json"), "{\"Playlist\":[],\"Families\":{\"Old.mp3\":\"Old.mp3\"}}");
    var oldReload = new LibraryStore(oldRoot);
    if(oldReload.Playlist().Count != 0 || oldReload.OutsidePlaylist().Single().VolumePercent != 100)
        throw new Exception("Older metadata migration changed playlist membership or default song volume.");
    oldReload.SetSongVolume(oldSong, 85);
    if(new LibraryStore(oldRoot).SongVolume(oldSong) != 85) throw new Exception("Older metadata did not accept persisted song levels.");
    Console.WriteLine("PASS: playlist migration, persistent exclusion/add order, exact trim lineage, legacy migration, confirmed family deletion, unrelated/external file protection.");
    Console.WriteLine("PASS: per-song volume defaults, independent persistence, both lists, 1–200% bounds, batched saves and deletion cleanup.");
}
finally { if(Directory.Exists(catalogTestRoot)) Directory.Delete(catalogTestRoot, true); }
var trimRange = ClipRange.Parse("1:05.5", "2:00");
if(trimRange.StartSeconds != 65.5 || trimRange.EndSeconds != 120 || ClipRange.Parse("", "").StartSeconds != 0
    || ClipRange.Parse("1:02:03", "").StartSeconds != 3723 || ClipRange.Parse("12.25", "20").LengthSeconds != 7.75)
    throw new Exception("Trim timestamp parsing failed.");
foreach(var (start, end) in new[] { ("-1", "5"), ("1:60", ""), ("1.5:20", ""), ("10", "5"), ("10", "10"), ("NaN", ""), ("0", "Infinity") })
{
    try { ClipRange.Parse(start, end); throw new Exception("Invalid trim range accepted."); } catch(ArgumentException) { }
}
try { ClipRange.Parse("30", "90").Validate(60); throw new Exception("Out-of-range trim accepted."); } catch(ArgumentException) { }
Console.WriteLine("PASS: trim time formats, full-length defaults, invalid times and source-duration bounds.");
if(args.Length > 0)
{
    MediaTools.ToolsDirectory = Path.GetFullPath(args[0]);
    var mediaTest = Path.Combine(Path.GetTempPath(), "FreeTapes-media-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(mediaTest);
    try
    {
        var original = Path.Combine(mediaTest, "source.wav");
        using(var writer = new NAudio.Wave.WaveFileWriter(original, new NAudio.Wave.WaveFormat(48000, 16, 1))) writer.Write(new byte[288000], 0, 288000);
        var originalBytes = File.ReadAllBytes(original);
        var output = await MediaTools.ExportTrim(original, mediaTest, new ClipRange(0.5, 1.5), CancellationToken.None);
        using(var reader = new NAudio.Wave.AudioFileReader(output))
            if(Math.Abs(reader.TotalTime.TotalSeconds - 1) > 0.15) throw new Exception("Trimmed MP3 has incorrect duration.");
        if(!File.ReadAllBytes(original).SequenceEqual(originalBytes)) throw new Exception("Trim modified the original.");
        var failure = "";
        using(var stream = new PcmStream(MediaTools.Command(MediaTools.Executable("ffmpeg.exe"), ["-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "sine=frequency=440:duration=3", "-ac", "2", "-ar", "48000", "-f", "f32le", "pipe:1"]), error => failure = error))
        {
            var timeout = DateTime.UtcNow.AddSeconds(10);
            while(stream.Game.BufferedDuration.TotalSeconds < 1 && DateTime.UtcNow < timeout) await Task.Delay(20);
            await Task.Delay(150); // Pause consumers; the producer must apply backpressure.
            if(stream.Game.BufferedDuration.TotalSeconds > 1.2 || stream.Monitor.BufferedDuration.TotalSeconds > 1.2) throw new Exception("Stream buffering is unbounded.");
            var bytes = new byte[32768]; long gameBytes = 0, monitorBytes = 0;
            while((!stream.Completed || stream.Game.BufferedBytes > 0 || stream.Monitor.BufferedBytes > 0) && DateTime.UtcNow < timeout)
            {
                gameBytes += stream.Game.Read(bytes, 0, bytes.Length);
                monitorBytes += stream.Monitor.Read(bytes, 0, bytes.Length);
                await Task.Delay(2);
            }
            if(!stream.Completed || failure != "" || gameBytes != 3 * 48000 * 8 || gameBytes != monitorBytes) throw new Exception("PCM stream did not deliver complete audio to both outputs: " + failure);
        }
        using(var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            try { await MediaTools.ExportTrim(original, mediaTest, new ClipRange(0, 1), cancelled.Token); throw new Exception("Cancelled trim succeeded."); } catch(OperationCanceledException) { }
        }
        if(Directory.GetFiles(mediaTest, "*.partial").Length != 0) throw new Exception("Cancelled trim left a partial file.");
        Console.WriteLine("PASS: actual FFmpeg MP3 trim duration/original preservation/cancellation and RAM-only PCM streaming, dual outputs, backpressure, completion.");
        if(args.Length > 1)
        {
            using var networkTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var media = await MediaTools.Resolve(args[1], networkTimeout.Token);
            string? streamFailure = null;
            using(var live = new PcmStream(media, error => streamFailure = error))
            {
                var bytes = new byte[32768]; long received = 0;
                while(received < 48000 * 8 && !networkTimeout.IsCancellationRequested && streamFailure == null)
                {
                    received += live.Game.Read(bytes, 0, bytes.Length);
                    live.Monitor.Read(bytes, 0, bytes.Length);
                    if(live.Completed && live.Game.BufferedBytes == 0) break;
                    await Task.Delay(10);
                }
                if(received < 48000 * 8) throw new Exception("Live YouTube stream test failed: " + streamFailure);
            }
            Console.WriteLine("PASS: live YouTube audio resolution and decoding into RAM without a saved track.");
        }
    }
    finally { Directory.Delete(mediaTest, true); }
}
var controls = new PlaybackBindings();
controls.Set(PlaybackAction.Play, 0x75); // F6
controls.Set(PlaybackAction.Pause, 0x76);
controls.Set(PlaybackAction.Stop, 0x77);
controls.Set(PlaybackAction.Next, 0x05); // Mouse side 1
var held = new HashSet<int>();
void ExpectAction(bool enabled, params PlaybackAction[] expected)
{
    var actions = controls.Poll(held.Contains, enabled);
    if (!actions.SequenceEqual(expected)) throw new Exception("Unexpected playback action(s): " + string.Join(",", actions));
}
ExpectAction(true);
held.Add(0x75); ExpectAction(true, PlaybackAction.Play); ExpectAction(true); // Hold must not restart repeatedly.
held.Clear(); ExpectAction(true);
held.Add(0x76); ExpectAction(true, PlaybackAction.Pause); ExpectAction(true);
held.Clear(); ExpectAction(true);
held.Add(0x77); ExpectAction(true, PlaybackAction.Stop);
held.Clear(); ExpectAction(true);
held.Add(0x05); ExpectAction(true, PlaybackAction.Next); ExpectAction(true);
held.Clear(); ExpectAction(true);
held.Add(0x75); ExpectAction(false); ExpectAction(true); // Focus/capture change while held must not activate.
held.Clear(); ExpectAction(true);
controls.Set(PlaybackAction.Play, 0x78); held.Add(0x78); ExpectAction(true); // Captured press is suppressed.
held.Clear(); ExpectAction(true); held.Add(0x78); ExpectAction(true, PlaybackAction.Play);
if (controls.Set(PlaybackAction.Stop, 0x78)) throw new Exception("Duplicate playback binding accepted.");
controls.Set(PlaybackAction.Play, 0); held.Clear(); ExpectAction(true); held.Add(0x78); ExpectAction(true);
if (PlaybackBindings.NextIndex(0, -1, -1) != -1 || PlaybackBindings.NextIndex(3, -1, -1) != 0
    || PlaybackBindings.NextIndex(3, 0, 2) != 1 || PlaybackBindings.NextIndex(3, 2, 0) != 0
    || PlaybackBindings.NextIndex(3, -1, 1) != 2 || PlaybackBindings.NextIndex(1, 0, 0) != 0)
    throw new Exception("Next song navigation failed.");
Console.WriteLine("PASS: Play/Pause/Stop/Next keyboard and mouse shortcuts, press edges, capture/focus guard, clear/conflicts, next ordering and wrap.");
var sends = new List<bool>();
var ptt = new ProximityHold(down => { sends.Add(down); return true; });
void Update(bool music = false, bool talking = false, bool focused = true, bool routed = true, bool capturing = false, bool enabled = true)
    => ptt.Update(enabled, focused, routed, capturing, music, talking);
void Expect(params bool[] expected)
{
    if(!sends.SequenceEqual(expected)) throw new Exception("Incorrect proximity K transitions: " + string.Join(",", sends));
    sends.Clear();
}
Update(music: true, focused: false); Expect();
Update(music: true); Update(music: true); Update(music: true, talking: true); Expect(true);
Update(talking: true); Expect(); // Song ended, speaker is still holding PTT.
Update(); Update(); Expect(false);
Update(talking: true); Update(); Expect(true, false); // No song: talk press/release.
Update(music: true); Update(music: false); Expect(true, false); // Pause/end.
Update(music: true); Update(music: true, focused: false); Expect(true, false);
Update(music: true); Update(talking: true, routed: false); Expect(true, false);
Update(music: true); Update(music: true, capturing: true); Expect(true, false);
Update(music: true); Update(music: true, enabled: false); Expect(true, false);
Update(music: true); ptt.Release(); ptt.Release(); Expect(true, false); // Shutdown.
ptt.SetPhysicalK(true); Update(talking: true); Expect(); // Physical K already reaches the game.
ptt.SetPhysicalK(false); Update(); Expect();
Update(music: true); Expect(true);
ptt.SetPhysicalK(true); ptt.SetPhysicalK(false); Update(music: true); Expect(true); // Restore music PTT after physical K-up.
ptt.Release(); Expect(false);
var acceptsInput = false;
var retry = new ProximityHold(_ => acceptsInput);
if(retry.Update(true, true, true, false, true, false) || retry.Holding) throw new Exception("Failed injection treated as success.");
acceptsInput = true; retry.Update(true, true, true, false, true, false);
acceptsInput = false;
if(retry.Release() || !retry.Holding) throw new Exception("Failed release was not retained for retry.");
acceptsInput = true; retry.Release();
if(retry.Holding) throw new Exception("Retry did not release K.");
Console.WriteLine("PASS: automatic K during music/talk, pause/end, focus, disconnect, capture, disable, shutdown, physical K, input failure retries.");
var bind = new TalkBinding();
bind.BeginCapture();
if(bind.IsTalking(true)) throw new Exception("Mic opened during binding capture.");
bind.Set(0x05); // Mouse side button 1
if(bind.IsTalking(true)) throw new Exception("Binding press opened microphone before release.");
bind.IsTalking(false);
if(!bind.IsTalking(true) || bind.IsTalking(false)) throw new Exception("Mouse hold/release failed.");
bind.BeginCapture(); bind.CancelCapture();
if(bind.VirtualKey != 0x05) throw new Exception("Cancel changed binding.");
if(bind.Set(0) || bind.Set(255)) throw new Exception("Invalid binding accepted.");
bind.Set(0xA3); // Right Control
bind.IsTalking(false);
if(!bind.IsTalking(true)) throw new Exception("Keyboard hold failed.");
Console.WriteLine("PASS: keyboard/mouse binding, release guard, capture mute, cancel, invalid binding.");
AudioEngine.VerifySignalPath();
foreach(var url in new[] { "https://www.youtube.com/watch?v=example", "https://youtu.be/example", "https://music.youtube.com/watch?v=example" })
 if(!LibraryStore.ValidUrl(url)) throw new Exception("Valid link rejected");
foreach(var url in new[] { "http://youtube.com/x", "https://youtube.com.attacker.test/x", "file:///tmp/test", "--exec bad", "https://other.test/" })
 if(LibraryStore.ValidUrl(url)) throw new Exception("Invalid link accepted");
var isolatedLibraryRoot = Path.Combine(Path.GetTempPath(), "FreeTapes-library-" + Guid.NewGuid().ToString("N"));
var store = new LibraryStore(isolatedLibraryRoot);
var fixture = Path.Combine(store.Root, "verify-" + Guid.NewGuid().ToString("N") + ".wav");
var imported = Path.Combine(store.Music, Path.GetFileName(fixture));
try
{
 using(var writer = new NAudio.Wave.WaveFileWriter(fixture, new NAudio.Wave.WaveFormat(48000, 16, 1))) writer.Write(new byte[9600], 0, 9600);
 store.Import(fixture); store.Import(fixture);
 if(!new LibraryStore(isolatedLibraryRoot).List().Any(t => t.Path == imported)) throw new Exception("Offline library persistence failed.");
 Console.WriteLine("PASS: gain 1–200%, clamp, pause/resume, ducking, URL validation, offline library and duplicate import.");
}
finally { Directory.Delete(isolatedLibraryRoot, true); }
