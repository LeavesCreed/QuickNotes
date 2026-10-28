using QuickNotes.Application;
using QuickNotes.Domain;
using QuickNotes.Infrastructure.Sqlite;
using Xunit;

namespace QuickNotes.IntegrationTests;

public sealed class SqliteApplicationSettingsRepositoryTests
{
    [Fact]
    public async Task Get_ReturnsSeededDefaults()
    {
        using var database = new TestDatabase();
        await new SqliteSchema(database.Factory).InitializeAsync();
        var repository = new SqliteApplicationSettingsRepository(database.Factory);

        var current = await repository.GetAsync();

        Assert.Equal(1, current.Version);
        Assert.Equal(16, current.DefaultFontSize);
        Assert.False(current.GlobalHidden);
        Assert.True(current.NotificationsEnabled);
        Assert.Equal(new[] { TopBarAction.Pin, TopBarAction.More }, current.TopBar.Actions);
    }

    [Fact]
    public async Task ApplicationSettings_Update_IncrementsVersionAndRejectsStale()
    {
        using var database = new TestDatabase();
        await new SqliteSchema(database.Factory).InitializeAsync();
        var repository = new SqliteApplicationSettingsRepository(database.Factory);
        var current = await repository.GetAsync();

        var updated = await repository.UpdateAsync(
            current with { GlobalHidden = true, DefaultFontSize = 18 },
            current.Version);

        Assert.Equal(current.Version + 1, updated.Version);
        Assert.True(updated.GlobalHidden);
        var exception = await Assert.ThrowsAsync<SettingsConcurrencyException>(() =>
            repository.UpdateAsync(current, current.Version));
        Assert.Equal(current.Version, exception.ExpectedVersion);
        Assert.Equal(current.Version + 1, exception.CurrentVersion);
        Assert.Equal("settings_concurrency_conflict", exception.ErrorCode);
    }

    [Fact]
    public async Task Update_PersistsTopBarActionsAndNotificationFlag()
    {
        using var database = await TestDatabase.CreateAsync();
        var repository = new SqliteApplicationSettingsRepository(database.Factory);
        var current = await repository.GetAsync();
        var changed = current with
        {
            NotificationsEnabled = false,
            TopBar = new TopBarConfig
            {
                Actions = new[] { TopBarAction.Color, TopBarAction.More }
            },
            UpdatedAtUtc = current.UpdatedAtUtc.AddMinutes(1)
        };

        var updated = await repository.UpdateAsync(changed, current.Version);
        var reloaded = await repository.GetAsync();

        Assert.Equal(new[] { TopBarAction.Color, TopBarAction.More }, updated.TopBar.Actions);
        Assert.False(updated.NotificationsEnabled);
        Assert.Equal(new[] { TopBarAction.Color, TopBarAction.More }, reloaded.TopBar.Actions);
        Assert.False(reloaded.NotificationsEnabled);
        Assert.Equal(
            "[\"Color\",\"More\"]",
            await ReadTopBarJsonAsync(database.Factory));
    }

    [Fact]
    public async Task Update_AdvancesChangeSequenceAndStaleUpdateDoesNot()
    {
        using var database = await TestDatabase.CreateAsync();
        var repository = new SqliteApplicationSettingsRepository(database.Factory);
        var current = await repository.GetAsync();
        Assert.Equal(0, await ReadChangeSequenceAsync(database.Factory));

        await repository.UpdateAsync(current with { GlobalHidden = true }, current.Version);
        Assert.Equal(1, await ReadChangeSequenceAsync(database.Factory));

        await Assert.ThrowsAsync<SettingsConcurrencyException>(() =>
            repository.UpdateAsync(current, current.Version));
        Assert.Equal(1, await ReadChangeSequenceAsync(database.Factory));
    }

    [Fact]
    public async Task Update_WithInvalidSettings_ThrowsBeforeWriting()
    {
        using var database = await TestDatabase.CreateAsync();
        var repository = new SqliteApplicationSettingsRepository(database.Factory);
        var current = await repository.GetAsync();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => repository.UpdateAsync(
                current with { DefaultFontSize = NoteRules.MinFontSize - 1 },
                current.Version));
        await Assert.ThrowsAsync<ArgumentException>(
            () => repository.UpdateAsync(
                current with
                {
                    TopBar = new TopBarConfig
                    {
                        Actions = new[] { TopBarAction.Pin, TopBarAction.Pin }
                    }
                },
                current.Version));

        var reloaded = await repository.GetAsync();
        Assert.Equal(current.Version, reloaded.Version);
        Assert.Equal(current.DefaultFontSize, reloaded.DefaultFontSize);
        Assert.Equal(current.GlobalHidden, reloaded.GlobalHidden);
        Assert.Equal(current.NotificationsEnabled, reloaded.NotificationsEnabled);
        Assert.Equal(current.TopBar.Actions, reloaded.TopBar.Actions);
        Assert.Equal(current.UpdatedAtUtc, reloaded.UpdatedAtUtc);
        Assert.Equal(0, await ReadChangeSequenceAsync(database.Factory));
    }

    private static async Task<long> ReadChangeSequenceAsync(SqliteConnectionFactory factory)
    {
        await using var connection = await factory.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT change_sequence FROM database_meta WHERE id = 1;";
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private static async Task<string> ReadTopBarJsonAsync(SqliteConnectionFactory factory)
    {
        await using var connection = await factory.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT top_bar_config_json FROM app_settings WHERE id = 1;";
        return Convert.ToString(await command.ExecuteScalarAsync())!;
    }
}
