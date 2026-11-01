using System.Diagnostics;
using System.IO;
using NAudio.Wave;

namespace FreeTapes;
// FFmpeg streams PCM through stdout. The only media buffer lives in bounded RAM.
public sealed class PcmStream : IDisposable
{
    private readonly CancellationTokenSource cancel = new();
    private readonly Process process;
    private readonly Process? fetch;
    private readonly Task pump;
    private int disposed;
    public BufferedWaveProvider Game { get; } = Buffer();
    public BufferedWaveProvider Monitor { get; } = Buffer();
    private volatile bool completed, hasAudio;
    public bool Completed => completed;
    public bool HasAudio => hasAudio;
    private static BufferedWaveProvider Buffer() => new(WaveFormat.CreateIeeeFloatWaveFormat(48000, 2)) { BufferDuration = TimeSpan.FromSeconds(3), ReadFully = false };
    public PcmStream(YouTubeStream media, Action<string> error) : this(BuildCommand(), error, MediaTools.StreamingDownload(media.VideoUrl)) { }
    private static ProcessStartInfo BuildCommand()
    {
        var args = new List<string> { "-hide_banner", "-loglevel", "error", "-nostdin", "-protocol_whitelist", "pipe,crypto", "-i", "pipe:0" };
        args.AddRange(["-vn", "-ac", "2", "-ar", "48000", "-f", "f32le", "pipe:1"]);
        return MediaTools.Command(MediaTools.Executable("ffmpeg.exe"), args);
    }
    internal PcmStream(ProcessStartInfo command, Action<string> error, ProcessStartInfo? upstream = null)
    {
        command.RedirectStandardInput = upstream != null;
        process = Process.Start(command) ?? throw new IOException("Could not start audio streaming.");
        try { if(upstream != null) fetch = Process.Start(upstream) ?? throw new IOException("Could not connect to YouTube."); }
        catch { MediaTools.Kill(process); process.Dispose(); throw; }
        pump = Pump(error);
    }
    private async Task Feed(Task<string> errors)
    {
        if(fetch == null) return;
        try
        {
            await fetch.StandardOutput.BaseStream.CopyToAsync(process.StandardInput.BaseStream, cancel.Token).ConfigureAwait(false);
            await fetch.WaitForExitAsync(cancel.Token).ConfigureAwait(false);
            var details = await errors.ConfigureAwait(false);
            if(fetch.ExitCode != 0) throw new IOException("YouTube stream unavailable: " + MediaTools.ShortError(details));
        }
        finally { process.StandardInput.Close(); }
    }
    private async Task Pump(Action<string> error)
    {
        var errors = process.StandardError.ReadToEndAsync();
        var fetchErrors = fetch?.StandardError.ReadToEndAsync() ?? Task.FromResult("");
        var feed = Feed(fetchErrors);
        try
        {
            var bytes = new byte[32776]; var carry = 0;
            while (true)
            {
                while (Game.BufferedDuration.TotalSeconds > 1 || Monitor.BufferedDuration.TotalSeconds > 1)
                    await Task.Delay(20, cancel.Token).ConfigureAwait(false);
                var count = await process.StandardOutput.BaseStream.ReadAsync(bytes.AsMemory(carry, 32768), cancel.Token).ConfigureAwait(false);
                if (count == 0) break;
                var total = carry + count; var aligned = total - total % 8;
                Game.AddSamples(bytes, 0, aligned); Monitor.AddSamples(bytes, 0, aligned);
                if (aligned != 0) hasAudio = true;
                carry = total - aligned;
                Array.Copy(bytes, aligned, bytes, 0, carry);
            }
            await process.WaitForExitAsync(cancel.Token).ConfigureAwait(false);
            await feed.ConfigureAwait(false);
            var details = await errors.ConfigureAwait(false);
            if (process.ExitCode != 0) throw new IOException("Stream failed: " + MediaTools.ShortError(details));
            if (!HasAudio) throw new IOException("YouTube stream returned no audio.");
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { if (!cancel.IsCancellationRequested) error(e.Message); }
        finally
        {
            MediaTools.Kill(process);
            if(fetch != null) MediaTools.Kill(fetch);
            try { await feed.ConfigureAwait(false); } catch (Exception) { }
            try { await errors.ConfigureAwait(false); } catch (IOException) { }
            try { await fetchErrors.ConfigureAwait(false); } catch (IOException) { }
            completed = true;
        }
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        cancel.Cancel(); MediaTools.Kill(process); if(fetch != null) MediaTools.Kill(fetch);
        _ = pump.ContinueWith(_ => { process.Dispose(); fetch?.Dispose(); cancel.Dispose(); }, TaskScheduler.Default);
    }
}
