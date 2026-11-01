using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace FreeTapes;
public sealed record Device(string Id, string Name);
public sealed class AudioEngine : IDisposable
{
    private readonly MMDeviceEnumerator devices = new();
    private WasapiCapture? capture;
    private WasapiOut? output;
    private WasapiOut? monitor;
    private MMDevice? monitorDevice;
    private AudioFileReader? monitorTrack;
    private VolumeSampleProvider? monitorGain;
    private MMDevice? inputDevice, outputDevice;
    private AudioFileReader? track;
    private PcmStream? streamSession;
    private readonly object gate = new();
    private MixingSampleProvider? mixer;
    private VolumeSampleProvider? micGain, musicGain;
    private volatile bool paused;
    private CompletionProvider? completion;
    public bool IsRouted => output != null && capture != null;
    public bool IsMusicPlaying => IsRouted && !paused && completion?.Active == true && (streamSession == null || streamSession.HasAudio);
    public bool HasActivePlayback => (track != null || streamSession != null) && completion?.Active == true;
    public bool IsPaused => HasActivePlayback && paused;
    public event Action<string>? Error;
    public List<Device> Devices(DataFlow flow) => devices.EnumerateAudioEndPoints(flow, DeviceState.Active)
        .Select(d => { using (d) return new Device(d.ID, d.FriendlyName); }).ToList();
    public void Start(string microphone, string destination, string headphones)
    {
        Disconnect();
        try
        {
            inputDevice = devices.GetDevice(microphone); outputDevice = devices.GetDevice(destination);
            capture = new WasapiCapture(inputDevice);
            var buffer = new BufferedWaveProvider(capture.WaveFormat) { DiscardOnBufferOverflow = true, BufferDuration = TimeSpan.FromMilliseconds(250) };
            capture.DataAvailable += (_, e) => buffer.AddSamples(e.Buffer, 0, e.BytesRecorded);
            capture.RecordingStopped += (_, e) => { if(e.Exception != null) Error?.Invoke(e.Exception.Message); };
            mixer = new MixingSampleProvider(WaveFormat.CreateIeeeFloatWaveFormat(48000, 2)) { ReadFully = true };
            micGain = new VolumeSampleProvider(Convert(buffer.ToSampleProvider())) { Volume = 0 };
            mixer.AddMixerInput(micGain);
            output = new WasapiOut(outputDevice, AudioClientShareMode.Shared, true, 60);
            output.PlaybackStopped += (_, e) => { if(e.Exception != null) Error?.Invoke(e.Exception.Message); };
            output.Init(new Limiter(mixer)); capture.StartRecording(); output.Play();
            monitorDevice = devices.GetDevice(headphones);
        }
        catch { Disconnect(); throw; }
    }
    private static ISampleProvider Convert(ISampleProvider source)
    {
        if (source.WaveFormat.Channels == 1) source = new MonoToStereoSampleProvider(source);
        if (source.WaveFormat.Channels != 2) throw new InvalidOperationException("Choose a mono or stereo audio device/file.");
        return source.WaveFormat.SampleRate == 48000 ? source : new WdlResamplingSampleProvider(source, 48000);
    }
    public void Play(string path)
    {
        lock(gate)
        {
            if(mixer == null) throw new InvalidOperationException("Start audio routing first.");
            Stop();
            try
            {
                track = new AudioFileReader(path); completion = new CompletionProvider(Convert(track)); musicGain = new VolumeSampleProvider(new PauseProvider(completion, () => paused)) { Volume = 0 }; mixer.AddMixerInput(musicGain);
                monitorTrack = new AudioFileReader(path);
                monitorGain = new VolumeSampleProvider(new PauseProvider(Convert(monitorTrack), () => paused)) { Volume = 0 };
                monitor = new WasapiOut(monitorDevice!, AudioClientShareMode.Shared, true, 60);
                monitor.Init(new Limiter(monitorGain)); monitor.Play();
            }
            catch { Stop(); throw; }
        }
    }
    public void PlayStream(YouTubeStream media)
    {
        lock (gate)
        {
            if (mixer == null) throw new InvalidOperationException("Start audio routing first.");
            Stop();
            try
            {
                streamSession = new PcmStream(media, message => Error?.Invoke(message));
                var session = streamSession;
                completion = new CompletionProvider(new LiveProvider(session.Game.ToSampleProvider(), () => session.Completed));
                musicGain = new VolumeSampleProvider(new PauseProvider(completion, () => paused)) { Volume = 0 };
                mixer.AddMixerInput(musicGain);
                monitorGain = new VolumeSampleProvider(new PauseProvider(new LiveProvider(session.Monitor.ToSampleProvider(), () => session.Completed), () => paused)) { Volume = 0 };
                monitor = new WasapiOut(monitorDevice!, AudioClientShareMode.Shared, true, 60);
                monitor.PlaybackStopped += (_, e) => { if(e.Exception != null) Error?.Invoke(e.Exception.Message); };
                monitor.Init(new Limiter(monitorGain)); monitor.Play();
            }
            catch { Stop(); throw; }
        }
    }
    public void Pause()
    {
        lock(gate)
        {
            if ((track == null && streamSession == null) || completion?.Active != true || mixer == null || musicGain == null || monitor == null) return;
            paused = !paused;
            // A paused headphone device must stop and flush its render buffer,
            // rather than keep rendering an endless stream of generated silence.
            // Keep the microphone mixer/output running so hold-to-talk still works.
            try { ApplyPause(mixer, musicGain, monitor.Stop, monitor.Play, paused); }
            catch { paused = true; throw; }
        }
    }
    private static void ApplyPause(MixingSampleProvider mixer, ISampleProvider music, Action stopMonitor, Action startMonitor, bool pause)
    {
        if (pause)
        {
            mixer.RemoveMixerInput(music);
            stopMonitor();
        }
        else
        {
            mixer.AddMixerInput(music);
            try { startMonitor(); }
            catch { mixer.RemoveMixerInput(music); throw; }
        }
    }
    internal static float EffectiveMusicGain(float music, float song, bool talking, float duck) => music * song * (talking ? duck : 1);
    public void SetLevels(float gameMusic, float headphoneMusic, float mic, bool talking, float duck, float song = 1)
    {
        lock(gate) ApplyLevels(musicGain, monitorGain, micGain, gameMusic, headphoneMusic, mic, talking, duck, song);
    }
    private static void ApplyLevels(VolumeSampleProvider? game, VolumeSampleProvider? headphones, VolumeSampleProvider? microphone,
        float gameMusic, float headphoneMusic, float mic, bool talking, float duck, float song)
    {
        if(game != null) game.Volume = EffectiveMusicGain(gameMusic, song, talking, duck);
        if(headphones != null) headphones.Volume = EffectiveMusicGain(headphoneMusic, song, talking, duck);
        if(microphone != null) microphone.Volume = talking ? mic : 0;
    }
    public void Stop()
    {
        lock(gate) { monitor?.Stop(); monitor?.Dispose(); monitor = null; monitorTrack?.Dispose(); monitorTrack = null; monitorGain = null; if(musicGain != null) mixer?.RemoveMixerInput(musicGain); musicGain = null; completion = null; track?.Dispose(); track = null; streamSession?.Dispose(); streamSession = null; paused = false; }
    }
    public void Disconnect()
    {
        output?.Stop(); output?.Dispose(); output = null;
        capture?.StopRecording(); capture?.Dispose(); capture = null;
        Stop(); mixer = null; micGain = null; inputDevice?.Dispose(); outputDevice?.Dispose(); monitorDevice?.Dispose(); monitorDevice = null; inputDevice = outputDevice = null;
    }
    public void Dispose() { Disconnect(); devices.Dispose(); }
    internal static void VerifySignalPath()
    {
        var source = new SignalGenerator(48000, 1) { Type = SignalGeneratorType.Sin, Frequency = 440, Gain = 0.8 };
        var pause = false;
        var music = new VolumeSampleProvider(new PauseProvider(Convert(source), () => pause)) { Volume = 2 };
        var mix = new MixingSampleProvider(new[] { music }) { ReadFully = true };
        var limit = new Limiter(mix); var samples = new float[4800];
        limit.Read(samples, 0, samples.Length);
        if(samples.Max(Math.Abs) > 1 || samples.Max(Math.Abs) < 0.99) throw new Exception("200% gain / clamp failed.");
        pause = true; limit.Read(samples, 0, samples.Length);
        if(samples.Any(s => s != 0)) throw new Exception("Pause failed.");
        pause = false; music.Volume = 0.15f; limit.Read(samples, 0, samples.Length);
        if(samples.Max(Math.Abs) > 0.121 || samples.Max(Math.Abs) < 0.11) throw new Exception("Ducking failed.");
        music.Volume = 0.01f; limit.Read(samples, 0, samples.Length);
        if(samples.Max(Math.Abs) > 0.0081 || samples.Max(Math.Abs) < 0.007) throw new Exception("1% gain failed.");
        music.Volume = EffectiveMusicGain(0.5f, 0.25f, false, 0.2f); limit.Read(samples, 0, samples.Length);
        if(Math.Abs(samples.Max(Math.Abs) - 0.1f) > 0.001f) throw new Exception("Master and song volumes did not multiply.");
        music.Volume = EffectiveMusicGain(0.5f, 0.25f, true, 0.2f); limit.Read(samples, 0, samples.Length);
        if(Math.Abs(samples.Max(Math.Abs) - 0.02f) > 0.001f) throw new Exception("Song volume did not preserve talk ducking.");
        long now = 1000;
        var ending = new CompletionProvider(new OffsetSampleProvider(new SignalGenerator(48000, 2)) { Take = TimeSpan.FromMilliseconds(1) }, () => now);
        ending.Read(samples, 0, samples.Length); ending.Read(samples, 0, samples.Length);
        if(!ending.Active) throw new Exception("Playback tail released PTT too early.");
        now += 151;
        if(ending.Active) throw new Exception("Natural track end did not clear playback state.");
        var liveBuffer = new BufferedWaveProvider(WaveFormat.CreateIeeeFloatWaveFormat(48000, 2)) { ReadFully = false };
        var done = false;
        var live = new LiveProvider(liveBuffer.ToSampleProvider(), () => done);
        if(live.Read(samples, 0, samples.Length) != samples.Length) throw new Exception("Stream underrun incorrectly ended playback.");
        done = true;
        if(live.Read(samples, 0, samples.Length) != 0) throw new Exception("Completed stream did not end playback.");
        VerifyPausedOutputs();
        VerifyIndependentOutputLevels();
    }
    private static void VerifyIndependentOutputLevels()
    {
        VolumeSampleProvider Tone() => new(new SignalGenerator(48000, 2) { Frequency = 440, Gain = 0.5 });
        var game = Tone(); var headphones = Tone(); var microphone = Tone(); var buffer = new float[4800];
        float Peak(VolumeSampleProvider provider) { provider.Read(buffer, 0, buffer.Length); return buffer.Max(Math.Abs); }
        void Near(float actual, float expected) { if(Math.Abs(actual - expected) > 0.001f) throw new Exception("Output levels were not independent."); }
        ApplyLevels(game, headphones, microphone, 0.8f, 0.2f, 1, false, 0.1f, 0.5f);
        Near(Peak(game), 0.2f); Near(Peak(headphones), 0.05f); Near(Peak(microphone), 0);
        ApplyLevels(game, headphones, microphone, 0.8f, 2, 1, false, 0.1f, 0.5f);
        Near(Peak(game), 0.2f); Near(Peak(headphones), 0.5f); // Changing personal volume leaves the game unchanged.
        ApplyLevels(game, headphones, microphone, 0.01f, 2, 1, false, 0.1f, 0.5f);
        Near(Peak(game), 0.0025f); Near(Peak(headphones), 0.5f); // Changing game volume leaves headphones unchanged.
        ApplyLevels(game, headphones, microphone, 0.8f, 0.2f, 1.2f, true, 0.1f, 0.5f);
        Near(Peak(game), 0.02f); Near(Peak(headphones), 0.005f); Near(Peak(microphone), 0.6f);
        Console.WriteLine("PASS: independent game/headphone audio levels, per-song multiplication, talk ducking and microphone gain.");
    }
    private static void VerifyPausedOutputs()
    {
        var song = new TrackingProvider(new SignalGenerator(44100, 2) { Frequency = 440, Gain = 0.5 });
        var songInput = new WdlResamplingSampleProvider(song, 48000);
        var mic = new VolumeSampleProvider(new SignalGenerator(48000, 2) { Frequency = 220, Gain = 0.1 }) { Volume = 0 };
        var pausedMixer = new MixingSampleProvider(new ISampleProvider[] { mic, songInput }) { ReadFully = true };
        var buffer = new float[4096];
        pausedMixer.Read(buffer, 0, 2048);
        var monitorRunning = true; var stops = 0; var starts = 0;
        void StopMonitor() { monitorRunning = false; stops++; }
        void StartMonitor() { monitorRunning = true; starts++; }
        for(var cycle = 0; cycle < 3; cycle++)
        {
            ApplyPause(pausedMixer, songInput, StopMonitor, StartMonitor, true);
            var position = song.SamplesRead;
            if(monitorRunning) throw new Exception("Paused headphone render output was not stopped/flushed.");
            foreach(var count in new[] { 96, 960, 2048, 3000 })
            {
                Array.Fill(buffer, 0.75f);
                if(pausedMixer.Read(buffer, 5, count) != count || buffer.Skip(5).Take(count).Any(v => v != 0))
                    throw new Exception("Paused output contains stale audio instead of exact silence.");
                if(buffer[4] != 0.75f || buffer[5 + count] != 0.75f) throw new Exception("Pause wrote outside the output buffer.");
            }
            mic.Volume = 1;
            pausedMixer.Read(buffer, 0, 2048);
            if(buffer.Take(2048).Max(Math.Abs) < 0.09f) throw new Exception("Microphone was stopped by music pause.");
            mic.Volume = 0;
            if(song.SamplesRead != position) throw new Exception("Paused song position advanced.");
            ApplyPause(pausedMixer, songInput, StopMonitor, StartMonitor, false);
            pausedMixer.Read(buffer, 0, 2048);
            if(!monitorRunning || buffer.Take(2048).Max(Math.Abs) < 0.45f || pausedMixer.MixerInputs.Count() != 2)
                throw new Exception("Resume failed or duplicated music in the mixer.");
        }
        if(stops != 3 || starts != 3) throw new Exception("Pause/resume output transitions were unbalanced.");
        Console.WriteLine("PASS: pause stops and flushes headphone rendering, reused-buffer silence, frozen song position, mic passthrough and repeated resume.");
    }
    private sealed class TrackingProvider(ISampleProvider source) : ISampleProvider
    {
        public int SamplesRead { get; private set; }
        public WaveFormat WaveFormat => source.WaveFormat;
        public int Read(float[] buffer, int offset, int count) { var n = source.Read(buffer, offset, count); SamplesRead += n; return n; }
    }
    private sealed class PauseProvider(ISampleProvider source, Func<bool> paused) : ISampleProvider
    {
        public WaveFormat WaveFormat => source.WaveFormat;
        public int Read(float[] buffer, int offset, int count) { if(!paused()) return source.Read(buffer, offset, count); Array.Clear(buffer, offset, count); return count; }
    }
    private sealed class LiveProvider(ISampleProvider source, Func<bool> completed) : ISampleProvider
    {
        public WaveFormat WaveFormat => source.WaveFormat;
        public int Read(float[] buffer, int offset, int count)
        {
            var read = source.Read(buffer, offset, count);
            if (completed()) return read;
            Array.Clear(buffer, offset + read, count - read);
            return count; // Temporary underruns must not remove this stream from the mixer.
        }
    }
    private sealed class CompletionProvider(ISampleProvider source, Func<long>? clock = null) : ISampleProvider
    {
        private long endTime = long.MaxValue;
        private long Now => clock?.Invoke() ?? Environment.TickCount64;
        public bool Active => Now < Interlocked.Read(ref endTime);
        public WaveFormat WaveFormat => source.WaveFormat;
        public int Read(float[] buffer, int offset, int count)
        {
            var read = source.Read(buffer, offset, count);
            // Let the WASAPI output buffer drain before releasing game PTT.
            if (read == 0) Interlocked.CompareExchange(ref endTime, Now + 150, long.MaxValue);
            return read;
        }
    }
    private sealed class Limiter(ISampleProvider source) : ISampleProvider
    {
        public WaveFormat WaveFormat => source.WaveFormat;
        public int Read(float[] buffer, int offset, int count) { var n = source.Read(buffer, offset, count); for(var i=offset;i<offset+n;i++) buffer[i] = Math.Clamp(buffer[i], -1f, 1f); return n; }
    }
}
