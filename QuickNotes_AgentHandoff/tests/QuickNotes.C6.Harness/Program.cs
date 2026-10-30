using System.Text.Json;
using System.Text.Json.Serialization;
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
    /// Task 2 realizes the save and backup bodies. Task 3 adds the restore,
    /// migration, seed, and restart bodies. Task 5 realizes the perf body and
    /// gates its verdict on <c>C6_BUDGETS_CONFIRMED=1</c>; without that exact
    /// value the perf result is baseline-only (<c>budgetStatus=unconfirmed</c>).
    /// </summary>
    public static class HarnessRunner
    {
        public const int UsageExitCode = 2;
        public const int ScenarioFailedExitCode = 1;
        public const int NotImplementedExitCode = 78;

        private static readonly JsonSerializerOptions SerializerOptions = CreateSerializerOptions();

        public static async Task<int> RunAsync(
            HarnessOptions options,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(options);
            cancellationToken.ThrowIfCancellationRequested();

            if (options.Scenario == HarnessScenario.Perf)
            {
                var perf = await PerfScenario.RunAsync(
                        options,
                        C6Budget.IsConfirmedInEnvironment(),
                        cancellationToken)
                    .ConfigureAwait(false);
                Console.Out.WriteLine(JsonSerializer.Serialize(perf, SerializerOptions));
                return perf.Passed ? 0 : ScenarioFailedExitCode;
            }

            HarnessScenarioResult? result = options.Scenario switch
            {
                HarnessScenario.Save => await SaveScenario.RunAsync(options, cancellationToken)
                    .ConfigureAwait(false),
                HarnessScenario.Backup => await BackupScenario.RunAsync(options, cancellationToken)
                    .ConfigureAwait(false),
                HarnessScenario.Restore => await RestoreScenario.RunAsync(options, cancellationToken)
                    .ConfigureAwait(false),
                HarnessScenario.Migrate => await MigrationScenario.RunAsync(options, cancellationToken)
                    .ConfigureAwait(false),
                HarnessScenario.Seed => await SeedScenario.RunAsync(options, cancellationToken)
                    .ConfigureAwait(false),
                HarnessScenario.Restart => await RestartScenario.RunAsync(options, cancellationToken)
                    .ConfigureAwait(false),
                _ => null
            };

            if (result is null)
            {
                var status = new
                {
                    scenario = HarnessOptions.ToCliName(options.Scenario),
                    status = "not_implemented",
                    noteCount = options.NoteCount,
                    visibleCount = options.VisibleCount
                };
                Console.Out.WriteLine(JsonSerializer.Serialize(status, SerializerOptions));
                return NotImplementedExitCode;
            }

            Console.Out.WriteLine(JsonSerializer.Serialize(result, SerializerOptions));
            return result.Passed ? 0 : ScenarioFailedExitCode;
        }

        private static JsonSerializerOptions CreateSerializerOptions()
        {
            var options = new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
            };
            options.Converters.Add(new JsonStringEnumConverter());
            return options;
        }
    }
}
