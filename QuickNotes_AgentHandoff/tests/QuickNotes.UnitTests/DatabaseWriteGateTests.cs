using QuickNotes.Application;
using Xunit;

namespace QuickNotes.UnitTests;

public sealed class DatabaseWriteGateTests
{
    [Fact]
    public async Task Save_WaitsForRestore_ThenSucceedsAndBlockedGateFailsSaves()
    {
        var gate = new DatabaseWriteGate();
        await using var restoreLease = await gate.EnterRestoreAsync();
        var coordinator = new NoteSaveCoordinator(
            new FakeNoteRepository(),
            new FakeDraftStore(),
            new SaveRetryOptions
            {
                RetryDelays = new[] { TimeSpan.Zero, TimeSpan.Zero }
            },
            logger: null,
            writeGate: gate);

        var saveTask = coordinator.CreateAsync(TestData.NewNote(), Array.Empty<Guid>());
        await Task.Delay(50);
        Assert.False(saveTask.IsCompleted);

        await restoreLease.DisposeAsync();
        var saved = await saveTask;
        Assert.Equal(SaveStatus.Saved, saved.Status);

        gate.BlockWrites();
        var blocked = await coordinator.CreateAsync(TestData.NewNote(), Array.Empty<Guid>());
        Assert.Equal(SaveStatus.StorageUnavailable, blocked.Status);
        Assert.Equal(SaveErrorCode.StorageUnavailable, blocked.Error!.Code);
        Assert.Equal("database_writes_blocked", blocked.Error.Diagnostics);
        Assert.Equal(0, blocked.Attempts);
    }

    [Fact]
    public async Task BlockWrites_RejectsNewRestoreLeases()
    {
        var gate = new DatabaseWriteGate();
        gate.BlockWrites();

        await Assert.ThrowsAsync<DatabaseWriteBlockedException>(
            () => gate.EnterRestoreAsync());
        await Assert.ThrowsAsync<DatabaseWriteBlockedException>(
            () => gate.EnterWriteAsync());
        Assert.True(gate.IsBlocked);
    }
}
