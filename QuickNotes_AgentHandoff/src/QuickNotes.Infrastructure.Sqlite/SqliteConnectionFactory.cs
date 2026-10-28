using Microsoft.Data.Sqlite;

namespace QuickNotes.Infrastructure.Sqlite;

public sealed class SqliteConnectionFactory
{
    private readonly string _connectionString;
    private readonly int _busyTimeoutMilliseconds;
    private readonly Action<SqliteConnection>? _configureConnection;

    public SqliteConnectionFactory(
        string databasePath,
        int busyTimeoutMilliseconds = 250,
        Action<SqliteConnection>? configureConnection = null)
    {
        if (busyTimeoutMilliseconds < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(busyTimeoutMilliseconds),
                busyTimeoutMilliseconds,
                "Busy timeout must be non-negative.");
        }

        _busyTimeoutMilliseconds = busyTimeoutMilliseconds;
        _configureConnection = configureConnection;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
            DefaultTimeout = Math.Max(1, (int)Math.Ceiling(busyTimeoutMilliseconds / 1000.0))
        }.ToString();
    }

    public async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken = default)
    {
        var connection = new SqliteConnection(_connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText =
                $"PRAGMA foreign_keys=ON; PRAGMA busy_timeout={_busyTimeoutMilliseconds};";
            await command.ExecuteNonQueryAsync(cancellationToken);
            _configureConnection?.Invoke(connection);
            return connection;
        }
        catch (SqliteException exception)
        {
            await connection.DisposeAsync();
            throw SqliteErrorTranslator.Translate(exception, "open", noteId: null, cancellationToken);
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }
}
