using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace FreeTapes;
public sealed record YouTubeStream(string VideoUrl, string Title, string AudioUrl, Dictionary<string, string> Headers, double? Duration);
public static class MediaTools
{
    internal static string ToolsDirectory { get; set; } = Path.Combine(AppContext.BaseDirectory, "tools");
    public static string Executable(string name)
    {
        var path = Path.Combine(ToolsDirectory, name);
        if (!File.Exists(path)) throw new FileNotFoundException("Missing " + name + ". Run Setup-Tools.ps1 beside the app.");
        return path;
    }
    internal static ProcessStartInfo Command(string executable, IEnumerable<string> arguments, bool stdout = true)
    {
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = stdout, RedirectStandardError = true };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        return info;
    }
    internal static void Kill(Process process) { try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { } catch (System.ComponentModel.Win32Exception) { } }
    internal static string ShortError(string error)
    {
        var safe = System.Text.RegularExpressions.Regex.Replace(error, @"https?://\S+", "[media URL]");
        return safe.Length > 1200 ? safe[^1200..] : safe;
    }
    internal static ProcessStartInfo StreamingDownload(string url)
    {
        if (!LibraryStore.ValidUrl(url)) throw new ArgumentException("Enter an HTTPS YouTube video link.");
        return Command(Executable("yt-dlp.exe"), ["--ignore-config", "--no-playlist", "--no-progress", "--no-cache-dir", "--socket-timeout", "20", "--js-runtimes", "deno:" + Executable("deno.exe"), "-f", "bestaudio[protocol=https]/bestaudio", "-o", "-", "--", url]);
    }
    public static async Task<YouTubeStream> Resolve(string url, CancellationToken token)
    {
        if (!LibraryStore.ValidUrl(url)) throw new ArgumentException("Enter an HTTPS YouTube video link.");
        var arguments = new[] { "--ignore-config", "--no-playlist", "--no-progress", "--no-cache-dir", "--socket-timeout", "20", "--js-runtimes", "deno:" + Executable("deno.exe"), "--skip-download", "--dump-single-json", "-f", "bestaudio[protocol=https]/bestaudio", "--", url };
        using var process = Process.Start(Command(Executable("yt-dlp.exe"), arguments)) ?? throw new IOException("Could not start YouTube resolver.");
        using var cancel = token.Register(() => Kill(process));
        var output = process.StandardOutput.ReadToEndAsync(); var errors = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().ConfigureAwait(false);
        var json = await output.ConfigureAwait(false); var error = await errors.ConfigureAwait(false); token.ThrowIfCancellationRequested();
        if (process.ExitCode != 0) throw new IOException("YouTube stream unavailable: " + ShortError(error));
        using var document = JsonDocument.Parse(json); var root = document.RootElement;
        var audioUrl = root.GetProperty("url").GetString() ?? "";
        if (!Uri.TryCreate(audioUrl, UriKind.Absolute, out var uri) || uri.Scheme != "https") throw new IOException("YouTube did not return a supported HTTPS audio stream.");
        var headers = new Dictionary<string, string>();
        if (root.TryGetProperty("http_headers", out var values))
            foreach (var entry in values.EnumerateObject())
                if (entry.Value.ValueKind == JsonValueKind.String && !entry.Name.Contains('\r') && !entry.Name.Contains('\n') && !entry.Name.Contains(':'))
                {
                    var value = entry.Value.GetString()!;
                    if (!value.Contains('\r') && !value.Contains('\n')) headers[entry.Name] = value;
                }
        double? duration = root.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.Number && d.TryGetDouble(out var seconds) ? seconds : null;
        return new YouTubeStream(url, root.GetProperty("title").GetString() ?? "YouTube stream", audioUrl, headers, duration);
    }
    public static async Task<string> ExportTrim(string source, string library, ClipRange range, CancellationToken token)
    {
        using (var reader = new NAudio.Wave.AudioFileReader(source)) range.Validate(reader.TotalTime.TotalSeconds);
        if (range.StartSeconds == 0 && range.EndSeconds == null) throw new ArgumentException("Enter a start or end time before saving a trimmed copy.");
        var name = Path.GetFileNameWithoutExtension(source);
        if (name.Length > 100) name = name[..100];
        var destination = Path.Combine(library, name + " [trim " + Guid.NewGuid().ToString("N")[..8] + "].mp3");
        var staging = destination + ".partial";
        try
        {
            var args = new List<string> { "-hide_banner", "-loglevel", "error", "-nostdin", "-y", "-ss", ClipRange.Format(range.StartSeconds), "-i", source };
            if (range.LengthSeconds is double length) args.AddRange(["-t", ClipRange.Format(length)]);
            args.AddRange(["-vn", "-codec:a", "libmp3lame", "-q:a", "2", "-f", "mp3", staging]);
            using var process = Process.Start(Command(Executable("ffmpeg.exe"), args)) ?? throw new IOException("Could not start MP3 trimmer.");
            using var cancel = token.Register(() => Kill(process));
            var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync().ConfigureAwait(false); await output.ConfigureAwait(false); var details = await error.ConfigureAwait(false); token.ThrowIfCancellationRequested();
            if (process.ExitCode != 0 || !File.Exists(staging) || new FileInfo(staging).Length == 0) throw new IOException("Trim failed: " + ShortError(details));
            File.Move(staging, destination);
            return destination;
        }
        finally { if (File.Exists(staging)) File.Delete(staging); }
    }
}
