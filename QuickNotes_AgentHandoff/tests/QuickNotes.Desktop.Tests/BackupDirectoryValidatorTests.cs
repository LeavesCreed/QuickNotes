using QuickNotes.Desktop.Backup;

namespace QuickNotes.Desktop.Tests;

public sealed class BackupDirectoryValidatorTests
{
    [Fact]
    public async Task ValidExistingDirectory_IsAccepted()
    {
        using var temporary = new TestTemporaryDirectory();
        var dataRoot = Path.Combine(temporary.RootPath, "data");
        var backup = Path.Combine(temporary.RootPath, "backups");
        Directory.CreateDirectory(dataRoot);
        Directory.CreateDirectory(backup);

        var result = await BackupDirectoryValidator.ValidateAsync(backup, dataRoot);

        Assert.Equal(BackupDirectoryValidationStatus.Valid, result.Status);
    }

    [Fact]
    public async Task DataRootAndNestedDirectory_AreRejected()
    {
        using var temporary = new TestTemporaryDirectory();
        var dataRoot = Path.Combine(temporary.RootPath, "data");
        Directory.CreateDirectory(dataRoot);

        Assert.Equal(
            BackupDirectoryValidationStatus.SameAsDataRoot,
            (await BackupDirectoryValidator.ValidateAsync(dataRoot, dataRoot)).Status);
        Assert.Equal(
            BackupDirectoryValidationStatus.SameAsDataRoot,
            (await BackupDirectoryValidator.ValidateAsync(
                Path.Combine(dataRoot, "backups"),
                dataRoot)).Status);
    }

    [Fact]
    public async Task RelativeMissingAndUnwritableDirectories_AreRejected()
    {
        using var temporary = new TestTemporaryDirectory();
        var dataRoot = Path.Combine(temporary.RootPath, "data");
        Directory.CreateDirectory(dataRoot);

        Assert.Equal(
            BackupDirectoryValidationStatus.Empty,
            (await BackupDirectoryValidator.ValidateAsync(null, dataRoot)).Status);
        Assert.Equal(
            BackupDirectoryValidationStatus.NotAbsolute,
            (await BackupDirectoryValidator.ValidateAsync("backups", dataRoot)).Status);
        Assert.Equal(
            BackupDirectoryValidationStatus.Missing,
            (await BackupDirectoryValidator.ValidateAsync(
                Path.Combine(temporary.RootPath, "missing"),
                dataRoot)).Status);
    }
}
