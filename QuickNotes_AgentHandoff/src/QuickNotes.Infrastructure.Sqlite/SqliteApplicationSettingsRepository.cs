using System.Data;
using System.Globalization;
using Microsoft.Data.Sqlite;
using QuickNotes.Application;
using QuickNotes.Domain;

namespace QuickNotes.Infrastructure.Sqlite;

public sealed class SqliteApplicationSettingsRepository : IApplicationSettingsRepository
{
    private const string SelectSql = """
        SELECT version, default_font_size, global_hidden, notifications_enabled, top_bar_config_json, updated_at_utc
        FROM app_settings
        WHERE id = 1;
        """;

    private readonly SqliteConnectionFactory _connectionFactory;

    public SqliteApplicationSettingsRepository(SqliteConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public async Task<ApplicationSettings> GetAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);
        return await ReadAsync(connection, transaction: null, cancellationToken)
            ?? throw new InvalidOperationException("The app_settings row is missing.");
    }

    public async Task<ApplicationSettings> UpdateAsync(
        ApplicationSettings settings,
        long expectedVersion,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (expectedVersion < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(expectedVersion),
                expectedVersion,
                "Expected version must be at least 1.");
        }

        if (settings.Version != expectedVersion)
        {
            throw new ArgumentException(
                "Settings version must match the expected version.",
                nameof(settings));
        }

        settings.Validate();

        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(IsolationLevel.Serializable, deferred: true);
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE app_settings
                SET version = version + 1,
                    default_font_size = $default_font_size,
                    global_hidden = $global_hidden,
                    notifications_enabled = $notifications_enabled,
                    top_bar_config_json = $top_bar_config_json,
                    updated_at_utc = $updated_at_utc
                WHERE id = 1 AND version = $expected_version;
                """;
            command.Parameters.AddWithValue("$expected_version", expectedVersion);
            command.Parameters.AddWithValue("$default_font_size", settings.DefaultFontSize);
            command.Parameters.AddWithValue("$global_hidden", settings.GlobalHidden ? 1 : 0);
            command.Parameters.AddWithValue("$notifications_enabled", settings.NotificationsEnabled ? 1 : 0);
            command.Parameters.AddWithValue(
                "$top_bar_config_json",
                SqliteTypeMapping.SerializeTopBarConfig(settings.TopBar));
            command.Parameters.AddWithValue("$updated_at_utc", SqliteTypeMapping.UtcString(settings.UpdatedAtUtc));
            var affectedRows = await command.ExecuteNonQueryAsync(cancellationToken);
            if (affectedRows == 0)
            {
                var currentVersion = await ReadCurrentVersionAsync(connection, transaction, cancellationToken);
                throw new SettingsConcurrencyException(expectedVersion, currentVersion);
            }
        }

        await SqliteChangeMarker.BumpAsync(connection, transaction, cancellationToken);
        var updated = await ReadAsync(connection, transaction, cancellationToken)
            ?? throw new InvalidOperationException("The app_settings row disappeared during update.");

        cancellationToken.ThrowIfCancellationRequested();
        await transaction.CommitAsync(CancellationToken.None);
        return updated;
    }

    private static async Task<ApplicationSettings?> ReadAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = SelectSql;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var settings = new ApplicationSettings
        {
            Version = reader.GetInt64(0),
            DefaultFontSize = reader.GetInt32(1),
            GlobalHidden = reader.GetInt64(2) != 0,
            NotificationsEnabled = reader.GetInt64(3) != 0,
            TopBar = SqliteTypeMapping.DeserializeTopBarConfig(reader.GetString(4)),
            UpdatedAtUtc = SqliteTypeMapping.ReadUtc(reader, 5)
        };
        try
        {
            settings.Validate();
        }
        catch (ArgumentException exception)
        {
            throw new InvalidOperationException(
                "The app_settings row contains invalid settings.",
                exception);
        }

        return settings;
    }

    private static async Task<long?> ReadCurrentVersionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT version FROM app_settings WHERE id = 1;";
        var value = await command.ExecuteScalarAsync(cancellationToken);
        if (value is null or DBNull)
        {
            return null;
        }

        return Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }
}
