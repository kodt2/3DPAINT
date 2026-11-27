using Npgsql;

namespace LoginAndReg.Tests;

/// <summary>
/// Direct probes of the test database (the API intentionally never exposes
/// the stored owner, so ownership assertions read the column; the seed
/// helpers insert rows that no API call could create for a foreign owner).
/// </summary>
public static class DbQuery
{
    public static async Task<string?> StoredOwnerAsync(string fileId)
    {
        await using var connection = new NpgsqlConnection(MonolithAppFactory.DbConnection);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT user_id FROM \"UserFiles\" WHERE id::text = @id", connection)
        {
            Parameters = { new NpgsqlParameter("@id", fileId) },
        };

        return (await command.ExecuteScalarAsync()) as string;
    }

    /// <summary>
    /// Inserts a UserFiles row directly with the given string owner and
    /// returns the row id. Used to simulate a legacy file whose owner is a
    /// numeric string (e.g. '4') without any API call.
    /// </summary>
    public static async Task<string> SeedFileAsync(string userId, string fileName)
    {
        var id = Guid.NewGuid();
        await using var connection = new NpgsqlConnection(MonolithAppFactory.DbConnection);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "INSERT INTO \"UserFiles\" (id, user_id, file_name, storage_path, file_size, created_at) " +
            "VALUES (@id, @userId, @fileName, @path, 12, now())",
            connection)
        {
            Parameters =
            {
                new NpgsqlParameter("@id", id),
                new NpgsqlParameter("@userId", userId),
                new NpgsqlParameter("@fileName", fileName),
                new NpgsqlParameter("@path", $"{userId}/{fileName}"),
            },
        };

        await command.ExecuteNonQueryAsync();
        return id.ToString("D");
    }

    /// <summary>Deletes a seeded UserFiles row (test cleanup).</summary>
    public static async Task DeleteFileAsync(string fileId)
    {
        await using var connection = new NpgsqlConnection(MonolithAppFactory.DbConnection);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "DELETE FROM \"UserFiles\" WHERE id::text = @id", connection)
        {
            Parameters = { new NpgsqlParameter("@id", fileId) },
        };

        await command.ExecuteNonQueryAsync();
    }
}
