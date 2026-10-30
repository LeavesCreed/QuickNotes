using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Threading;
using QuickNotes.Application;
using QuickNotes.C6.Harness;
using QuickNotes.Desktop.Hosting;
using QuickNotes.Desktop.Wpf;
using QuickNotes.Domain;

namespace QuickNotes.C6.UiProbe;

/// <summary>
/// Opt-in WPF probe for C6 Task 5. It hosts the real desktop host, real
/// <see cref="NoteWindowManager"/>, and real note windows against synthetic
/// <c>%TEMP%\QuickNotes-C6\&lt;runId&gt;</c> data. It never constructs
/// <c>App</c>, so no tray icon, global hotkey, HKCU Run entry, or workspace
/// window is created. It emits timings, window counts, and process resources
/// only, never note text.
/// </summary>
internal static class Program
{
    private static readonly JsonSerializerOptions SerializerOptions = CreateSerializerOptions();

    [STAThread]
    private static int Main(string[] args)
    {
        var processStartedAt = Stopwatch.GetTimestamp();
        UiProbeOptions options;
        try
        {
            options = UiProbeOptions.Parse(args);
        }
        catch (ArgumentException exception)
        {
            Console.Error.WriteLine($"c6-ui-probe: {exception.Message}");
            return 2;
        }

        var application = new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        UiProbeResult? result = null;
        application.DispatcherUnhandledException += (_, eventArgs) =>
        {
            result = Failure(
                options,
                "dispatcher",
                eventArgs.Exception.GetType().Name,
                processStartedAt);
            eventArgs.Handled = true;
            TryWriteResult(options, result);
            application.Shutdown(1);
        };
        AppDomain.CurrentDomain.UnhandledException += (_, eventArgs) =>
        {
            var errorCode = eventArgs.ExceptionObject?.GetType().Name ?? "unhandled";
            Console.Error.WriteLine($"c6-ui-probe: unhandled {errorCode}");
            if (result is null)
            {
                TryWriteResult(
                    options,
                    Failure(options, "unhandled", errorCode, processStartedAt));
            }
        };
        application.Startup += async (_, _) =>
        {
            try
            {
                result = await RunAsync(options, processStartedAt);
            }
            catch (UiProbeException exception)
            {
                result = Failure(
                    options,
                    exception.Stage,
                    exception.ErrorCode,
                    processStartedAt);
            }
            catch (Exception exception)
            {
                result = Failure(
                    options,
                    "probe",
                    exception.GetType().Name,
                    processStartedAt);
            }
            finally
            {
                TryWriteResult(options, result);
                application.Shutdown(result?.Status == "ok" ? 0 : 1);
            }
        };

        application.Run();
        return result?.Status == "ok" ? 0 : 1;
    }

    private static async Task<UiProbeResult> RunAsync(
        UiProbeOptions options,
        long processStartedAt)
    {
        var dataRoot = C6HarnessPaths.RequireUnderRunRoot(
            options.DataRoot,
            nameof(options.DataRoot));
        var host = new DesktopApplicationHost(
            new DesktopStartupOptions { DataRoot = dataRoot });
        NoteWindowManager? windows = null;
        try
        {
            var hostStartedAt = Stopwatch.GetTimestamp();
            var started = await host.StartAsync().ConfigureAwait(true);
            var hostStartMs = Stopwatch.GetElapsedTime(hostStartedAt).TotalMilliseconds;
            if (!started.CanShowWindow || started.Assessment?.CanEnterNormalUse != true)
            {
                throw new UiProbeException(
                    "startup",
                    $"startup_mode_{started.Assessment?.Mode}");
            }

            var noteCount = await EnsureVisibleCountAsync(host, options.VisibleWindows)
                .ConfigureAwait(true);
            windows = new NoteWindowManager(host);
            await windows.StartAsync().ConfigureAwait(true);
            await WaitForWindowsAsync(options.VisibleWindows, TimeSpan.FromSeconds(10))
                .ConfigureAwait(true);

            var readyMs = Stopwatch.GetElapsedTime(processStartedAt).TotalMilliseconds;
            var ready = CaptureProcessMetrics();
            var unexpectedWindowCount = CountUnexpectedWindows();
            var cycleTimes = options.ShowHideCycles > 0
                ? await RunShowHideCyclesAsync(options.ShowHideCycles).ConfigureAwait(true)
                : new List<double>();

            SoakEvidence? soak = null;
            if (options.SoakSeconds > 0)
            {
                soak = await RunSoakAsync(options).ConfigureAwait(true);
            }

            var final = CaptureProcessMetrics();
            return new UiProbeResult
            {
                Status = "ok",
                Mode = options.SoakSeconds > 0 ? "soak" : "startup",
                VisibleWindows = options.VisibleWindows,
                NoteCount = noteCount,
                WindowCount = WindowList().Length,
                ReadyMs = readyMs,
                HostStartMs = hostStartMs,
                StartupMode = started.Assessment?.Mode.ToString(),
                WorkingSetBytes = ready.WorkingSetBytes,
                PrivateBytes = ready.PrivateBytes,
                HandleCount = ready.HandleCount,
                ThreadCount = ready.ThreadCount,
                ProcessResponding = ready.Responding,
                UnexpectedWindowCount = unexpectedWindowCount,
                FinalWorkingSetBytes = final.WorkingSetBytes,
                FinalHandleCount = final.HandleCount,
                ShowHideCycles = cycleTimes.Count,
                ShowHideP50Ms = cycleTimes.Count == 0 ? null : Percentile(cycleTimes, 50),
                ShowHideP95Ms = cycleTimes.Count == 0 ? null : Percentile(cycleTimes, 95),
                ShowHideMaxMs = cycleTimes.Count == 0 ? null : cycleTimes.Max(),
                Soak = soak,
                Message = "The WPF probe completed without tray, hotkey, or HKCU side effects."
            };
        }
        finally
        {
            if (windows is not null)
            {
                await windows.DisposeAsync().ConfigureAwait(true);
            }

            await host.DisposeAsync().ConfigureAwait(true);
        }
    }

    private static async Task<int> EnsureVisibleCountAsync(
        DesktopApplicationHost host,
        int targetVisible)
    {
        var services = host.EditingServices
            ?? throw new UiProbeException("services", "editing_services_missing");
        var active = await services.Notes.QueryAsync(new NoteQuery
        {
            Lifecycle = NoteLifecycle.Active
        }).ConfigureAwait(true);
        if (active.Count < targetVisible)
        {
            throw new UiProbeException(
                "visible",
                $"active_notes_{active.Count}_below_{targetVisible}");
        }

        var visible = active
            .Where(note => note.DisplayIntent == NoteDisplayIntent.Visible)
            .OrderBy(note => note.ModifiedAtUtc)
            .ToList();
        if (visible.Count > targetVisible)
        {
            foreach (var note in visible.Skip(targetVisible))
            {
                await services.Notes.SetLifecycleAndDisplayAsync(
                    note.Id,
                    NoteLifecycle.Active,
                    NoteDisplayIntent.Hidden,
                    note.Version,
                    DateTimeOffset.UtcNow).ConfigureAwait(true);
            }
        }
        else if (visible.Count < targetVisible)
        {
            var hidden = active
                .Where(note => note.DisplayIntent != NoteDisplayIntent.Visible)
                .OrderBy(note => note.ModifiedAtUtc)
                .Take(targetVisible - visible.Count);
            foreach (var note in hidden)
            {
                await services.Notes.SetLifecycleAndDisplayAsync(
                    note.Id,
                    NoteLifecycle.Active,
                    NoteDisplayIntent.Visible,
                    note.Version,
                    DateTimeOffset.UtcNow).ConfigureAwait(true);
            }
        }

        return active.Count;
    }

    private static async Task WaitForWindowsAsync(int expected, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var windows = WindowList();
            if (windows.Length == expected && windows.All(window => window.IsVisible))
            {
                await Dispatcher.Yield(DispatcherPriority.Render);
                return;
            }

            await Task.Delay(25);
        }

        throw new UiProbeException(
            "windows",
            $"window_count_{WindowList().Length}_expected_{expected}");
    }

    private static async Task<List<double>> RunShowHideCyclesAsync(int cycles)
    {
        var windows = WindowList();
        if (windows.Length == 0)
        {
            throw new UiProbeException("windows", "no_note_windows");
        }

        var times = new List<double>(cycles);
        for (var cycle = 0; cycle < cycles; cycle++)
        {
            var startedAt = Stopwatch.GetTimestamp();
            foreach (var window in windows)
            {
                window.Hide();
            }

            await Dispatcher.Yield(DispatcherPriority.Render);
            foreach (var window in windows)
            {
                window.Show();
            }

            await Dispatcher.Yield(DispatcherPriority.Render);
            times.Add(Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds);
        }

        return times;
    }

    private static async Task<SoakEvidence> RunSoakAsync(UiProbeOptions options)
    {
        var samples = new List<SoakSample>();
        var startedAt = Stopwatch.GetTimestamp();
        var nextSampleAt = TimeSpan.Zero;
        var interval = TimeSpan.FromSeconds(options.SampleSeconds);
        var duration = TimeSpan.FromSeconds(options.SoakSeconds);
        while (true)
        {
            var elapsed = Stopwatch.GetElapsedTime(startedAt);
            if (elapsed >= duration)
            {
                break;
            }

            if (nextSampleAt > elapsed)
            {
                await Task.Delay(nextSampleAt - elapsed);
            }

            var cycleTimes = await RunShowHideCyclesAsync(1);
            var metrics = CaptureProcessMetrics();
            var sample = new SoakSample
            {
                ElapsedSeconds = Math.Round(Stopwatch.GetElapsedTime(startedAt).TotalSeconds, 3),
                WorkingSetBytes = metrics.WorkingSetBytes,
                PrivateBytes = metrics.PrivateBytes,
                HandleCount = metrics.HandleCount,
                ThreadCount = metrics.ThreadCount,
                WindowCount = WindowList().Length,
                ProcessResponding = metrics.Responding,
                UnexpectedWindowCount = CountUnexpectedWindows(),
                CycleMs = cycleTimes[0]
            };
            samples.Add(sample);
            if (!string.IsNullOrWhiteSpace(options.SamplesPath))
            {
                AppendSample(options.SamplesPath, sample);
            }

            nextSampleAt += interval;
        }

        return new SoakEvidence
        {
            DurationSeconds = options.SoakSeconds,
            SampleSeconds = options.SampleSeconds,
            SampleCount = samples.Count,
            Completed = Stopwatch.GetElapsedTime(startedAt) >= duration,
            PeakWorkingSetBytes = samples.Count == 0 ? 0 : samples.Max(item => item.WorkingSetBytes),
            PeakHandleCount = samples.Count == 0 ? 0 : samples.Max(item => item.HandleCount),
            FirstWorkingSetBytes = samples.Count == 0 ? null : samples[0].WorkingSetBytes,
            LastWorkingSetBytes = samples.Count == 0 ? null : samples[^1].WorkingSetBytes,
            FirstHandleCount = samples.Count == 0 ? null : samples[0].HandleCount,
            LastHandleCount = samples.Count == 0 ? null : samples[^1].HandleCount,
            Samples = samples
        };
    }

    private static NoteWindow[] WindowList() =>
        System.Windows.Application.Current?.Windows.OfType<NoteWindow>().ToArray()
        ?? Array.Empty<NoteWindow>();

    /// <summary>
    /// Counts non-note WPF windows. A modal dialog or unexpected window during
    /// the soak is a failure signal rather than a normal part of the probe.
    /// </summary>
    private static int CountUnexpectedWindows() =>
        System.Windows.Application.Current?.Windows
            .OfType<Window>()
            .Count(window => window is not NoteWindow)
        ?? 0;

    private static ProcessMetrics CaptureProcessMetrics()
    {
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        return new ProcessMetrics(
            process.WorkingSet64,
            process.PrivateMemorySize64,
            process.HandleCount,
            process.Threads.Count,
            process.Responding);
    }

    private static double Percentile(IReadOnlyList<double> values, int percentile)
    {
        var ordered = values.OrderBy(value => value).ToArray();
        var rank = (int)Math.Ceiling(percentile / 100.0 * ordered.Length);
        rank = Math.Clamp(rank, 1, ordered.Length);
        return ordered[rank - 1];
    }

    private static void AppendSample(string path, SoakSample sample)
    {
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.AppendAllText(
            fullPath,
            JsonSerializer.Serialize(sample, SerializerOptions) + Environment.NewLine);
    }

    private static void TryWriteResult(UiProbeOptions options, UiProbeResult? result)
    {
        if (result is null)
        {
            return;
        }

        try
        {
            var json = JsonSerializer.Serialize(result, SerializerOptions);
            if (!string.IsNullOrWhiteSpace(options.OutputPath))
            {
                var fullPath = Path.GetFullPath(options.OutputPath);
                var directory = Path.GetDirectoryName(fullPath);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                var temporary = $"{fullPath}.{Guid.NewGuid():N}.tmp";
                File.WriteAllText(temporary, json);
                File.Move(temporary, fullPath, overwrite: true);
            }

            Console.Out.WriteLine(json);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"c6-ui-probe: result write failed ({exception.GetType().Name})");
        }
    }

    private static UiProbeResult Failure(
        UiProbeOptions options,
        string stage,
        string errorCode,
        long processStartedAt) =>
        new()
        {
            Status = "failed",
            Mode = options.SoakSeconds > 0 ? "soak" : "startup",
            VisibleWindows = options.VisibleWindows,
            NoteCount = 0,
            WindowCount = 0,
            ReadyMs = Stopwatch.GetElapsedTime(processStartedAt).TotalMilliseconds,
            HostStartMs = 0,
            StartupMode = null,
            WorkingSetBytes = 0,
            PrivateBytes = 0,
            HandleCount = 0,
            ThreadCount = 0,
            ProcessResponding = false,
            UnexpectedWindowCount = 0,
            FinalWorkingSetBytes = 0,
            FinalHandleCount = 0,
            ShowHideCycles = 0,
            ShowHideP50Ms = null,
            ShowHideP95Ms = null,
            ShowHideMaxMs = null,
            Soak = null,
            FailureStage = stage,
            ErrorCode = errorCode,
            Message = "The WPF probe failed; no note text was written."
        };

    private static JsonSerializerOptions CreateSerializerOptions()
    {
        var serializerOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };
        serializerOptions.Converters.Add(new JsonStringEnumConverter());
        return serializerOptions;
    }

    private sealed record ProcessMetrics(
        long WorkingSetBytes,
        long PrivateBytes,
        int HandleCount,
        int ThreadCount,
        bool Responding);
}

internal sealed class UiProbeException : Exception
{
    public UiProbeException(string stage, string errorCode)
        : base(errorCode)
    {
        Stage = stage;
        ErrorCode = errorCode;
    }

    public string Stage { get; }

    public string ErrorCode { get; }
}

internal sealed record UiProbeResult
{
    public string Scenario { get; init; } = "ui_probe";
    public required string Status { get; init; }
    public required string Mode { get; init; }
    public required int VisibleWindows { get; init; }
    public required int NoteCount { get; init; }
    public required int WindowCount { get; init; }
    public required double ReadyMs { get; init; }
    public required double HostStartMs { get; init; }
    public string? StartupMode { get; init; }
    public required long WorkingSetBytes { get; init; }
    public required long PrivateBytes { get; init; }
    public required int HandleCount { get; init; }
    public required int ThreadCount { get; init; }
    public required bool ProcessResponding { get; init; }
    public required int UnexpectedWindowCount { get; init; }
    public required long FinalWorkingSetBytes { get; init; }
    public required int FinalHandleCount { get; init; }
    public required int ShowHideCycles { get; init; }
    public double? ShowHideP50Ms { get; init; }
    public double? ShowHideP95Ms { get; init; }
    public double? ShowHideMaxMs { get; init; }
    public SoakEvidence? Soak { get; init; }
    public string? FailureStage { get; init; }
    public string? ErrorCode { get; init; }
    public string? Message { get; init; }
}

internal sealed record SoakEvidence
{
    public required int DurationSeconds { get; init; }
    public required int SampleSeconds { get; init; }
    public required int SampleCount { get; init; }
    public required bool Completed { get; init; }
    public required long PeakWorkingSetBytes { get; init; }
    public required int PeakHandleCount { get; init; }
    public long? FirstWorkingSetBytes { get; init; }
    public long? LastWorkingSetBytes { get; init; }
    public int? FirstHandleCount { get; init; }
    public int? LastHandleCount { get; init; }
    public required IReadOnlyList<SoakSample> Samples { get; init; }
}

internal sealed record SoakSample
{
    public required double ElapsedSeconds { get; init; }
    public required long WorkingSetBytes { get; init; }
    public required long PrivateBytes { get; init; }
    public required int HandleCount { get; init; }
    public required int ThreadCount { get; init; }
    public required int WindowCount { get; init; }
    public required bool ProcessResponding { get; init; }
    public required int UnexpectedWindowCount { get; init; }
    public required double CycleMs { get; init; }
}

internal sealed record UiProbeOptions
{
    public required string DataRoot { get; init; }
    public int VisibleWindows { get; init; } = C6Budget.VisibleWindows;
    public int ShowHideCycles { get; init; }
    public int SoakSeconds { get; init; }
    public int SampleSeconds { get; init; } = 60;
    public string? RunId { get; init; }
    public string? OutputPath { get; init; }
    public string? SamplesPath { get; init; }

    public static UiProbeOptions Parse(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        string? dataRoot = null;
        string? runId = null;
        string? output = null;
        string? samples = null;
        var visible = C6Budget.VisibleWindows;
        var showHideCycles = 0;
        var soakSeconds = 0;
        var sampleSeconds = 60;
        for (var index = 0; index < arguments.Count; index++)
        {
            switch (arguments[index])
            {
                case "--data-root":
                    dataRoot = ReadValue(arguments, ref index, "--data-root");
                    break;
                case "--run-id":
                    runId = ReadValue(arguments, ref index, "--run-id");
                    break;
                case "--output":
                    output = ReadValue(arguments, ref index, "--output");
                    break;
                case "--samples":
                    samples = ReadValue(arguments, ref index, "--samples");
                    break;
                case "--visible":
                    visible = ReadInt(arguments, ref index, "--visible", 1);
                    break;
                case "--show-hide-cycles":
                    showHideCycles = ReadInt(arguments, ref index, "--show-hide-cycles", 0);
                    break;
                case "--soak-seconds":
                    soakSeconds = ReadInt(arguments, ref index, "--soak-seconds", 0);
                    break;
                case "--sample-seconds":
                    sampleSeconds = ReadInt(arguments, ref index, "--sample-seconds", 10);
                    break;
                default:
                    throw new ArgumentException($"Unknown option '{arguments[index]}'.");
            }
        }

        if (string.IsNullOrWhiteSpace(dataRoot))
        {
            throw new ArgumentException("Option --data-root is required.");
        }

        return new UiProbeOptions
        {
            DataRoot = dataRoot,
            VisibleWindows = visible,
            ShowHideCycles = showHideCycles,
            SoakSeconds = soakSeconds,
            SampleSeconds = sampleSeconds,
            RunId = runId,
            OutputPath = output,
            SamplesPath = samples
        };
    }

    private static string ReadValue(
        IReadOnlyList<string> arguments,
        ref int index,
        string option)
    {
        if (index + 1 >= arguments.Count || string.IsNullOrWhiteSpace(arguments[index + 1]))
        {
            throw new ArgumentException($"Option {option} requires a value.");
        }

        index++;
        return arguments[index];
    }

    private static int ReadInt(
        IReadOnlyList<string> arguments,
        ref int index,
        string option,
        int minimum)
    {
        var value = ReadValue(arguments, ref index, option);
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            || parsed < minimum)
        {
            throw new ArgumentException(
                $"Option {option} requires an integer greater than or equal to {minimum}.");
        }

        return parsed;
    }
}
