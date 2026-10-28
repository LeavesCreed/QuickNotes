using QuickNotes.Desktop.State;

namespace QuickNotes.Desktop.Tests;

public sealed class ApplicationStateStoreTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 10, 28, 9, 30, 0, TimeSpan.Zero);

    [Fact]
    public async Task Read_MissingState_ReturnsNotFound()
    {
        using var temporary = new TestTemporaryDirectory();
        var store = CreateStore(temporary);

        var result = await store.ReadAsync();

        Assert.Equal(ApplicationStateReadStatus.NotFound, result.Status);
        Assert.Null(result.State);
    }

    [Fact]
    public async Task Read_InvalidJson_ReturnsInvalid()
    {
        using var temporary = new TestTemporaryDirectory();
        var store = CreateStore(temporary);
        Directory.CreateDirectory(Path.GetDirectoryName(store.StatePath)!);
        await File.WriteAllTextAsync(store.StatePath, "{");

        var result = await store.ReadAsync();

        Assert.Equal(ApplicationStateReadStatus.Invalid, result.Status);
        Assert.Equal("state_json_invalid", result.Error);
    }

    [Fact]
    public async Task Read_UnknownFormatVersion_ReturnsInvalid()
    {
        using var temporary = new TestTemporaryDirectory();
        var store = CreateStore(temporary);
        Directory.CreateDirectory(Path.GetDirectoryName(store.StatePath)!);
        await File.WriteAllTextAsync(
            store.StatePath,
            """
            {
              "formatVersion": 99,
              "installationId": "5a0f8c2e-6f4e-4f0e-9f6e-2c9a7b1d3e44",
              "initializationStatus": "Ready",
              "schemaVersion": 1,
              "lastStartupUtc": "2026-10-28T09:30:00+00:00"
            }
            """);

        var result = await store.ReadAsync();

        Assert.Equal(ApplicationStateReadStatus.Invalid, result.Status);
        Assert.Equal("state_invalid", result.Error);
    }

    [Fact]
    public async Task WriteThenRead_RoundTripsAllFields()
    {
        using var temporary = new TestTemporaryDirectory();
        var store = CreateStore(temporary);
        var state = CreateReadyState() with
        {
            WritesBlocked = true,
            WriteBlockReason = "restore_rollback_failed",
            WriteBlockedAtUtc = Now.AddMinutes(1),
            RecoverySourceSha256 = new string('a', 64),
            RecoveryProtectionSnapshotCreated = false,
            LastRecoveryAtUtc = Now.AddMinutes(2)
        };

        await store.WriteAsync(state);
        var result = await store.ReadAsync();

        Assert.Equal(ApplicationStateReadStatus.Found, result.Status);
        Assert.Equal(state, result.State);
    }

    [Fact]
    public async Task Write_ReplacesExistingStateAtomically_NoTempLeftovers()
    {
        using var temporary = new TestTemporaryDirectory();
        var store = CreateStore(temporary);
        await store.WriteAsync(CreateReadyState());
        await store.WriteAsync(CreateReadyState() with { LastStartupUtc = Now.AddMinutes(5) });

        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(store.StatePath)!, "*.tmp"));
        var result = await store.ReadAsync();
        Assert.Equal(Now.AddMinutes(5), result.State!.LastStartupUtc);
    }

    [Fact]
    public async Task TryMarkWritesBlocked_PersistsBlockAndReason()
    {
        using var temporary = new TestTemporaryDirectory();
        var store = CreateStore(temporary);
        await store.WriteAsync(CreateReadyState());

        var persisted = store.TryMarkWritesBlocked(
            "restore_rollback_failed",
            Now.AddMinutes(3),
            out var error);

        Assert.True(persisted);
        Assert.Null(error);
        var result = await store.ReadAsync();
        Assert.True(result.State!.WritesBlocked);
        Assert.Equal("restore_rollback_failed", result.State.WriteBlockReason);
        Assert.Equal(Now.AddMinutes(3), result.State.WriteBlockedAtUtc);
    }

    [Fact]
    public async Task TryMarkWritesBlocked_WhenStateMissing_ReturnsFalse()
    {
        using var temporary = new TestTemporaryDirectory();
        var store = CreateStore(temporary);

        var persisted = store.TryMarkWritesBlocked("reason", Now, out var error);

        Assert.False(persisted);
        Assert.Equal("state_unavailable:NotFound", error);
    }

    [Fact]
    public async Task Write_InvalidReadyState_Throws()
    {
        using var temporary = new TestTemporaryDirectory();
        var store = CreateStore(temporary);

        await Assert.ThrowsAsync<ArgumentException>(
            () => store.WriteAsync(CreateReadyState() with { SchemaVersion = 0 }));
    }

    private static ApplicationStateStore CreateStore(TestTemporaryDirectory temporary) =>
        new(Path.Combine(temporary.RootPath, "application-state.json"));

    private static ApplicationState CreateReadyState() => new()
    {
        FormatVersion = ApplicationState.CurrentFormatVersion,
        InstallationId = Guid.NewGuid(),
        InitializationStatus = ApplicationInitializationStatus.Ready,
        SchemaVersion = 1,
        LastStartupUtc = Now
    };
}
