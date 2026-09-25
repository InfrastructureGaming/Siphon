using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Siphon.Core.Capture;
using Siphon.Core.Recording;
using Siphon.Harness;

const double Tolerance = 0.020;

return args switch
{
    ["record", var secs, .. var rest] => await RecordAsync(double.Parse(secs), rest.FirstOrDefault()),
    ["gaptest", .. var rest] => await GapTestAsync(rest.FirstOrDefault() ?? RecordingNames.DefaultFolder),
    ["killtest", .. var rest] => await KillTestAsync(rest.FirstOrDefault() ?? RecordingNames.DefaultFolder),
    ["inspect", var path] => Inspect(path),
    ["envelope", var path, .. var rest] => Envelope(path, rest is [var ms] ? int.Parse(ms) : 250),
    _ => Usage(),
};

static int Envelope(string path, int windowMs)
{
    var info = WavInfo.Read(path);
    float[] peaks = info.ReadFramePeaks();
    int window = info.SampleRate * windowMs / 1000;
    for (int start = 0; start < peaks.Length; start += window)
    {
        float p = 0;
        for (int i = start; i < Math.Min(start + window, peaks.Length); i++)
            p = Math.Max(p, peaks[i]);
        double db = p > 0 ? 20 * Math.Log10(p) : double.NegativeInfinity;
        Console.WriteLine($"{(double)start / info.SampleRate,8:F3} s  {db,7:F1} dBFS  {new string('#', p > 0 ? Math.Max(0, (int)((db + 60) / 2)) : 0)}");
    }
    return 0;
}

static int Usage()
{
    Console.WriteLine("""
        Siphon harness
          record <seconds> [file.wav]   record system audio (Ctrl+C stops early)
          gaptest [folder]              play 5 s tone, 10 s silence, 5 s tone; check timing
          killtest [folder]             kill a recording mid-way; check the file survives
          inspect <file.wav>            show header vs on-disk sizes
        """);
    return 2;
}

static async Task<int> RecordAsync(double seconds, string? path)
{
    path ??= RecordingNames.NewRecordingPath(RecordingNames.DefaultFolder, null, DateTime.Now);
    using var source = new SystemLoopbackSource();
    Console.WriteLine($"Device: {source.DeviceName}");
    Console.WriteLine($"Format: {source.Format}");
    source.Start();

    var session = new CaptureSession(source, new WavSink(path, source.Format));
    session.Start();
    Console.WriteLine($"Recording to {path}");

    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
    while (!cts.IsCancellationRequested && !session.Completion.IsCompleted)
    {
        await Task.WhenAny(Task.Delay(1000, cts.Token), session.Completion);
        Console.WriteLine($"  {session.Elapsed:hh\\:mm\\:ss}");
    }

    var result = await session.StopAsync();
    source.Stop();
    Console.WriteLine($"Stopped ({result.Reason}) after {result.Duration.TotalSeconds:F3} s{(result.Error is null ? "" : $": {result.Error.Message}")}");
    return result.Error is null ? 0 : 1;
}

static async Task<int> GapTestAsync(string folder)
{
    Console.WriteLine("Gap test: 5 s tone, 10 s silence, 5 s tone. Keep other audio quiet for ~20 s.");
    string path = RecordingNames.NewRecordingPath(folder, "gaptest", DateTime.Now);

    using var source = new SystemLoopbackSource();
    Console.WriteLine($"Device: {source.DeviceName} ({source.Format})");
    source.Start();
    await Task.Delay(300);

    var session = new CaptureSession(source, new WavSink(path, source.Format));
    session.Start();

    TimeSpan play1 = await PlayToneAsync(session, source.Format, TimeSpan.FromSeconds(5));
    await Task.Delay(TimeSpan.FromSeconds(10));
    TimeSpan play2 = await PlayToneAsync(session, source.Format, TimeSpan.FromSeconds(5));
    TimeSpan stoppedAt = session.Elapsed;
    var result = await session.StopAsync();
    source.Stop();

    if (result.Error is not null)
    {
        Console.WriteLine($"FAIL: recording error: {result.Error}");
        return 1;
    }

    var info = WavInfo.Read(path);
    info.Print();

    float[] peaks = info.ReadFramePeaks();
    int rate = info.SampleRate;
    long onset1 = FindOnset(peaks, 0);
    long onset2 = onset1 < 0 ? -1 : FindOnset(peaks, onset1 + 7 * rate);
    if (onset1 < 0 || onset2 < 0)
    {
        Console.WriteLine("FAIL: couldn't find both tones in the recording.");
        return 1;
    }
    long end1 = onset2 - 1;
    while (end1 > onset1 && peaks[end1] <= 0.01f)
        end1--;
    float gapPeak = 0;
    for (long i = end1 + rate / 10; i < onset2 - rate / 10; i++)
        gapPeak = Math.Max(gapPeak, peaks[i]);

    double fileSeconds = info.HeaderSeconds;
    double lengthError = fileSeconds - stoppedAt.TotalSeconds;
    double onsetDelta = (double)(onset2 - onset1) / rate;
    double playDelta = (play2 - play1).TotalSeconds;
    double gapError = onsetDelta - playDelta;

    Console.WriteLine();
    Console.WriteLine($"  recording ran:       {stoppedAt.TotalSeconds:F3} s   file: {fileSeconds:F3} s   error {lengthError * 1000:+0.0;-0.0} ms");
    Console.WriteLine($"  tones started apart: {playDelta:F3} s   in file: {onsetDelta:F3} s   error {gapError * 1000:+0.0;-0.0} ms");
    Console.WriteLine($"  first tone length:   {(double)(end1 - onset1) / rate:F3} s");
    Console.WriteLine($"  peak during gap:     {gapPeak:F4}{(gapPeak > 0 ? "  (other audio was playing?)" : "  (digital silence)")}");

    bool pass = Math.Abs(lengthError) <= Tolerance && Math.Abs(gapError) <= Tolerance;
    Console.WriteLine(pass ? "PASS" : "FAIL");
    return pass ? 0 : 1;
}

static long FindOnset(float[] peaks, long from)
{
    for (long i = Math.Max(0, from); i < peaks.Length; i++)
        if (peaks[i] > 0.01f)
            return i;
    return -1;
}

static async Task<TimeSpan> PlayToneAsync(CaptureSession session, WaveFormat format, TimeSpan duration)
{
    using var device = SystemLoopbackSource.GetDefaultRenderDevice();
    using var output = new WasapiOut(device, AudioClientShareMode.Shared, useEventSync: true, latency: 20);
    var tone = new SignalGenerator(format.SampleRate, Math.Min(format.Channels, 2))
    {
        Type = SignalGeneratorType.Sin,
        Frequency = 440,
        Gain = 0.05, // about -26 dBFS
    };
    var stopped = new TaskCompletionSource();
    output.PlaybackStopped += (_, _) => stopped.TrySetResult();
    output.Init(tone.Take(duration));
    TimeSpan startedAt = session.Elapsed;
    output.Play();
    await stopped.Task;
    return startedAt;
}

static async Task<int> KillTestAsync(string folder)
{
    string path = RecordingNames.NewRecordingPath(folder, "killtest", DateTime.Now);
    var psi = new ProcessStartInfo(Environment.ProcessPath!, ["record", "60", path]) { UseShellExecute = false };
    Console.WriteLine("Kill test: starting a 60 s recording, killing it after ~5.5 s.");
    using var child = Process.Start(psi)!;
    await Task.Delay(5500);
    child.Kill(entireProcessTree: true);
    await child.WaitForExitAsync();
    Console.WriteLine($"Killed (exit code {child.ExitCode}).");

    var info = WavInfo.Read(path);
    info.Print();
    info.ReadFramePeaks(); // proves every frame the header claims is actually readable

    bool pass = info.HeaderSeconds >= 3.5 && info.DataSizeInHeader <= info.DataBytesOnDisk
        && info.DiskSeconds - info.HeaderSeconds <= 1.2;
    Console.WriteLine(pass ? "PASS" : "FAIL");
    return pass ? 0 : 1;
}

static int Inspect(string path)
{
    WavInfo.Read(path).Print();
    return 0;
}
