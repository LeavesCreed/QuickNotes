using System.Text.Json;
using QuickNotes.C6.Harness;

try
{
    var options = HarnessOptions.Parse(args);
    using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(30));
    return await HarnessRunner.RunAsync(options, cancellation.Token);
}
catch (ArgumentException exception)
{
    Console.Error.WriteLine($"c6-harness: {exception.Message}");
    return HarnessRunner.UsageExitCode;
}

namespace QuickNotes.C6.Harness
{
    /// <summary>
    /// Task 1 only establishes the CLI contract. Real save/backup/restore/
    /// migrate/perf scenario bodies are implemented by Tasks 2-5; until then
    /// the runner reports not_implemented and returns a non-zero exit code
    /// instead of pretending a scenario succeeded.
    /// </summary>
    public static class HarnessRunner
    {
        public const int UsageExitCode = 2;
        public const int NotImplementedExitCode = 78;

        public static Task<int> RunAsync(
            HarnessOptions options,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(options);
            cancellationToken.ThrowIfCancellationRequested();

            var status = new
            {
                scenario = HarnessOptions.ToCliName(options.Scenario),
                status = "not_implemented",
                noteCount = options.NoteCount,
                visibleCount = options.VisibleCount
            };
            Console.Out.WriteLine(JsonSerializer.Serialize(status));
            return Task.FromResult(NotImplementedExitCode);
        }
    }
}
