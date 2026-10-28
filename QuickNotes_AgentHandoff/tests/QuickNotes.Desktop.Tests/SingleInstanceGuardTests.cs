using QuickNotes.Desktop.Diagnostics;
using QuickNotes.Desktop.SingleInstance;

namespace QuickNotes.Desktop.Tests;

public sealed class SingleInstanceGuardTests
{
    [Fact]
    public async Task SecondInstance_WhenOwnerRunning_ActivatesOwner()
    {
        using var temporary = new TestTemporaryDirectory();
        await using var owner = CreateGuard(temporary.RootPath);
        Assert.Equal(
            SingleInstanceStartResult.Owner,
            await owner.StartAsync(TimeSpan.FromSeconds(2)));
        var activated = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        owner.ActivationRequested += () => activated.TrySetResult();

        await using var second = CreateGuard(temporary.RootPath);
        var result = await second.StartAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(SingleInstanceStartResult.ActivatedExisting, result);
        await activated.Task.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task SecondInstance_WhenActivationPipeMissing_ReturnsUnresponsive()
    {
        using var temporary = new TestTemporaryDirectory();
        var names = SingleInstanceGuard.CreateNames(temporary.RootPath);
        Directory.CreateDirectory(temporary.RootPath);
        using var heldLock = new FileStream(
            names.LockFilePath,
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None);
        await using var second = CreateGuard(temporary.RootPath);

        var result = await second.StartAsync(TimeSpan.FromMilliseconds(200));

        Assert.Equal(SingleInstanceStartResult.Unresponsive, result);
    }

    [Fact]
    public async Task Dispose_ReleasesOwnership_NextInstanceBecomesOwner()
    {
        using var temporary = new TestTemporaryDirectory();
        var first = CreateGuard(temporary.RootPath);
        Assert.Equal(
            SingleInstanceStartResult.Owner,
            await first.StartAsync(TimeSpan.FromSeconds(2)));
        await first.DisposeAsync();

        await using var second = CreateGuard(temporary.RootPath);
        Assert.Equal(
            SingleInstanceStartResult.Owner,
            await second.StartAsync(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task TwoDifferentDataRoots_DoNotConflict()
    {
        using var firstRoot = new TestTemporaryDirectory();
        using var secondRoot = new TestTemporaryDirectory();
        await using var first = CreateGuard(firstRoot.RootPath);
        await using var second = CreateGuard(secondRoot.RootPath);

        Assert.Equal(
            SingleInstanceStartResult.Owner,
            await first.StartAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(
            SingleInstanceStartResult.Owner,
            await second.StartAsync(TimeSpan.FromSeconds(2)));
    }

    private static SingleInstanceGuard CreateGuard(string dataRoot) =>
        new(dataRoot, new DiagnosticLog(Path.Combine(dataRoot, "logs")));
}
