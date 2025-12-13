using Npgsql;

namespace ObsidianWebhook;

public class DataStore
{
    private readonly string _connectionString;

    public DataStore(string connectionString)
    {
        _connectionString = connectionString;
    }

    public async Task<string?> GetVaultApiKeyAsync(string vaultName)
    {
        using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();

        var query = "SELECT \"ApiKey\" FROM public.\"VaultConfig\" WHERE public.\"VaultConfig\".\"Name\" = @name;";
        using var command = new NpgsqlCommand(query, connection);
        command.Parameters.AddWithValue("@name", vaultName);

        using var reader = await command.ExecuteReaderAsync();
        if (await reader.ReadAsync())
        {
            return reader.GetString(0);
        }

        return null;
    }

    public async Task<int> InsertNoteToQueueAsync(string vault, string note)
    {
        using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();

        var insertQuery = @"INSERT INTO public.""NoteQueue""(""Vault"", ""Note"")
                            VALUES (@vault, @note)
                            RETURNING ""Id"";";
        using var command = new NpgsqlCommand(insertQuery, connection);
        command.Parameters.AddWithValue("@vault", vault);
        command.Parameters.AddWithValue("@note", note);

        var result = await command.ExecuteScalarAsync();
        return (int)(result ?? 0);
    }

    public async Task DeleteNoteFromQueueAsync(int noteId)
    {
        using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();

        var deleteQuery = @"DELETE FROM public.""NoteQueue"" WHERE ""Id"" = @id;";
        using var command = new NpgsqlCommand(deleteQuery, connection);
        command.Parameters.AddWithValue("@id", noteId);

        await command.ExecuteNonQueryAsync();
    }

    public async Task<List<(int Id, string Note)>> GetQueuedNotesForVaultAsync(string vault)
    {
        var queuedNotes = new List<(int Id, string Note)>();

        using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();

        var selectQuery = @"SELECT ""Id"", ""Note""
                           FROM public.""NoteQueue""
                           WHERE ""Vault"" = @vault
                           ORDER BY ""CreatedAt"" ASC;";
        using var command = new NpgsqlCommand(selectQuery, connection);
        command.Parameters.AddWithValue("@vault", vault);

        using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            queuedNotes.Add((reader.GetInt32(0), reader.GetString(1)));
        }

        return queuedNotes;
    }

    public async Task DeleteMultipleNotesFromQueueAsync(int[] noteIds)
    {
        if (noteIds.Length == 0)
        {
            return;
        }

        using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();

        var deleteQuery = @"DELETE FROM public.""NoteQueue"" WHERE ""Id"" = ANY(@ids);";
        using var command = new NpgsqlCommand(deleteQuery, connection);
        command.Parameters.AddWithValue("@ids", noteIds);

        await command.ExecuteNonQueryAsync();
    }

    public async Task<(bool Success, string Message, object? Data)> TestDatabaseConnectionAsync()
    {
        try
        {
            using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            using var command = new NpgsqlCommand("SELECT version()", connection);
            var version = await command.ExecuteScalarAsync();

            return (true, "Database connection successful!", new
            {
                postgresVersion = version?.ToString(),
                database = connection.Database,
                host = connection.Host,
                port = connection.Port
            });
        }
        catch (Exception ex)
        {
            return (false, "Database connection failed", new
            {
                error = ex.Message,
                stackTrace = ex.StackTrace
            });
        }
    }
}
