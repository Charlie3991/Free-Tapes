using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
namespace FreeTapes;
public sealed record Tape(string Path)
{
    public string Title => System.IO.Path.GetFileNameWithoutExtension(Path);
    public double VolumePercent { get; set; } = 100;
}
public sealed class LibraryStore
{
    public string Root { get; }
    public string Music => Path.Combine(Root, "Library");
    private sealed record Catalog(List<string> Playlist, Dictionary<string, string> Families, Dictionary<string, double>? Volumes = null);
    private Catalog? catalog;
    private string CatalogPath => Path.Combine(Root, "library-state.json");
    public LibraryStore(string? root = null)
    {
        Root = Path.GetFullPath(root ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FreeTapes"));
        Directory.CreateDirectory(Music);
    }
    private List<Tape> Files() => Directory.EnumerateFiles(Music).Where(p => new[] { ".mp3", ".wav", ".m4a" }.Contains(Path.GetExtension(p).ToLowerInvariant())).OrderBy(p => p).Select(p => new Tape(p)).ToList();
    public List<Tape> List()
    {
        var state = State();
        return Files().Select(t => t with { VolumePercent = SavedVolume(state, Path.GetFileName(t.Path)) }).ToList();
    }
    private Catalog State()
    {
        if (catalog == null)
        {
            catalog = File.Exists(CatalogPath) ? JsonSerializer.Deserialize<Catalog>(File.ReadAllText(CatalogPath)) : null;
            catalog = catalog == null ? new Catalog([], new(StringComparer.OrdinalIgnoreCase), new(StringComparer.OrdinalIgnoreCase))
                : new Catalog(catalog.Playlist, new Dictionary<string, string>(catalog.Families, StringComparer.OrdinalIgnoreCase),
                    new Dictionary<string, double>(catalog.Volumes ?? [], StringComparer.OrdinalIgnoreCase));
        }
        var files = Files(); var changed = false;
        foreach (var tape in files)
        {
            var name = Path.GetFileName(tape.Path);
            if (catalog.Families.ContainsKey(name)) continue;
            catalog.Families[name] = InferFamily(name, files);
            catalog.Playlist.Add(name); changed = true;
        }
        if (changed) SaveCatalog();
        return catalog;
    }
    private static string InferFamily(string name, List<Tape> files)
    {
        var stem = Path.GetFileNameWithoutExtension(name);
        var originalStem = Regex.Replace(stem, @"( \[trim [a-fA-F0-9]{8}\])+$", "");
        if (originalStem == stem) return name;
        var originals = files.Where(t => !Regex.IsMatch(t.Title, @" \[trim [a-fA-F0-9]{8}\]$"))
            .Where(t => t.Title == originalStem || (t.Title.Length > 100 && t.Title[..100] == originalStem)).ToList();
        // Ambiguous older filenames are not grouped, to avoid deleting another song.
        return originals.Count == 1 ? Path.GetFileName(originals[0].Path) : originals.Count == 0 ? originalStem + Path.GetExtension(name) : name;
    }
    private void SaveCatalog()
    {
        var staging = CatalogPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(staging, JsonSerializer.Serialize(catalog)); File.Move(staging, CatalogPath, true); }
        finally { if (File.Exists(staging)) File.Delete(staging); }
    }
    private static double SavedVolume(Catalog state, string name)
    {
        var percent = state.Volumes?.GetValueOrDefault(name, 100) ?? 100;
        return double.IsFinite(percent) && percent is >= 1 and <= 200 ? percent : 100;
    }
    public double SongVolume(string path) => SavedVolume(State(), OwnedName(path));
    public void SetSongVolume(string path, double percent, bool persist = true)
    {
        if (!double.IsFinite(percent) || percent is < 1 or > 200) throw new ArgumentOutOfRangeException(nameof(percent), "Song volume must be between 1% and 200%.");
        var name = OwnedName(path); var state = State();
        state.Volumes![name] = percent;
        if (persist) SaveCatalog();
    }
    public void SavePendingChanges() { if (catalog != null) SaveCatalog(); }
    private string OwnedName(string path)
    {
        var absolute = Path.GetFullPath(path);
        if (!string.Equals(Path.GetDirectoryName(absolute), Path.GetFullPath(Music), StringComparison.OrdinalIgnoreCase)
            || (File.GetAttributes(Music) & FileAttributes.ReparsePoint) != 0
            || (File.Exists(absolute) && (File.GetAttributes(absolute) & FileAttributes.ReparsePoint) != 0)
            || !new[] { ".mp3", ".wav", ".m4a" }.Contains(Path.GetExtension(absolute).ToLowerInvariant()))
            throw new InvalidOperationException("This action is limited to audio files inside the Free Tapes library.");
        return Path.GetFileName(absolute);
    }
    public List<Tape> Playlist()
    {
        var state = State(); var files = List().ToDictionary(t => Path.GetFileName(t.Path), StringComparer.OrdinalIgnoreCase);
        return state.Playlist.Distinct(StringComparer.OrdinalIgnoreCase).Where(files.ContainsKey).Select(name => files[name]).ToList();
    }
    public List<Tape> OutsidePlaylist()
    {
        var queued = Playlist().Select(t => t.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return List().Where(t => !queued.Contains(t.Path)).ToList();
    }
    public void AddToPlaylist(string path)
    {
        var name = OwnedName(path); if (!File.Exists(path)) throw new FileNotFoundException("Song no longer exists.");
        var state = State(); if (!state.Playlist.Contains(name, StringComparer.OrdinalIgnoreCase)) { state.Playlist.Add(name); SaveCatalog(); }
    }
    public void RemoveFromPlaylist(string path)
    {
        var name = OwnedName(path); var state = State();
        state.Playlist.RemoveAll(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase)); SaveCatalog();
    }
    public void RegisterSaved(string path, string? sourceFileName = null)
    {
        var name = OwnedName(path); var state = State();
        if (sourceFileName != null)
        {
            if (Path.GetFileName(sourceFileName) != sourceFileName) throw new ArgumentException("Expected a library filename.");
            state.Families[name] = state.Families.GetValueOrDefault(sourceFileName, sourceFileName);
            SaveCatalog();
        }
    }
    public List<Tape> Family(string path)
    {
        var name = OwnedName(path); var state = State();
        var family = state.Families.GetValueOrDefault(name, name);
        return List().Where(t => string.Equals(state.Families.GetValueOrDefault(Path.GetFileName(t.Path), Path.GetFileName(t.Path)), family, StringComparison.OrdinalIgnoreCase)).ToList();
    }
    public void DeleteFamily(string path, IReadOnlyCollection<string>? confirmedFiles = null)
    {
        var files = Family(path);
        if(confirmedFiles != null && !files.Select(t => t.Path).ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(confirmedFiles))
            throw new InvalidOperationException("The song's versions changed after confirmation. Review the list and try again.");
        foreach (var file in files) OwnedName(file.Path); // Validate every target before deleting any file.
        try { foreach (var file in files) File.Delete(file.Path); }
        finally
        {
            var state = State();
            foreach (var file in files.Where(t => !File.Exists(t.Path)))
            {
                var name = Path.GetFileName(file.Path);
                state.Playlist.RemoveAll(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase)); state.Families.Remove(name); state.Volumes?.Remove(name);
            }
            SaveCatalog();
        }
    }
    public void Import(string path)
    {
        var destination = Path.Combine(Music, Path.GetFileName(path));
        if (!File.Exists(destination)) { State(); File.Copy(path, destination); RegisterSaved(destination); }
    }
    public static bool ValidUrl(string value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == "https" &&
        new[] { "youtube.com", "www.youtube.com", "m.youtube.com", "music.youtube.com", "youtu.be", "www.youtu.be" }.Contains(uri.Host.ToLowerInvariant());
    public async Task Download(string url, CancellationToken token, ClipRange? trim = null)
    {
        State();
        if (!ValidUrl(url)) throw new ArgumentException("Enter an HTTPS YouTube video link.");
        var tools = Path.Combine(AppContext.BaseDirectory, "tools");
        var executable = Path.Combine(tools, "yt-dlp.exe");
        if (!File.Exists(executable) || !File.Exists(Path.Combine(tools, "ffmpeg.exe"))) throw new InvalidOperationException("Run Setup-Tools.ps1 to install yt-dlp and FFmpeg beside the app.");
        var staging = Path.Combine(Root, "Imports", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(staging);
        try
        {
            var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach(var arg in new[] { "--ignore-config", "--no-playlist", "--no-progress", "--windows-filenames", "--js-runtimes", "deno:" + Path.Combine(tools, "deno.exe"), "--ffmpeg-location", tools, "-x", "--audio-format", "mp3", "--audio-quality", "0", "-o", Path.Combine(staging, "%(title).150B [%(id)s].%(ext)s"), "--", url }) info.ArgumentList.Add(arg);
            using var process = Process.Start(info) ?? throw new IOException("Could not start downloader.");
            var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
            using var registration = token.Register(() => { try { if(!process.HasExited) process.Kill(true); } catch(InvalidOperationException) { } });
            await process.WaitForExitAsync(); await stdout; var errors = await stderr; token.ThrowIfCancellationRequested();
            if(process.ExitCode != 0) throw new IOException(errors.Length > 1600 ? errors[^1600..] : errors);
            var files = Directory.GetFiles(staging, "*.mp3");
            if(files.Length == 0) throw new IOException("The downloader returned no MP3 file.");
            foreach(var file in files)
            {
                var saved = trim != null && (trim.StartSeconds > 0 || trim.EndSeconds != null)
                    ? await MediaTools.ExportTrim(file, staging, trim, token) : file;
                var destination = Path.Combine(Music, Path.GetFileName(saved));
                File.Move(saved, destination, true);
                RegisterSaved(destination, saved != file ? Path.GetFileName(file) : null);
            }
        }
        finally { Directory.Delete(staging, true); }
    }
}
