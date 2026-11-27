using Npgsql;

namespace LoginAndReg.Tests;

/// <summary>
/// Direct read-only probes of the test database (the API intentionally
/// never exposes the stored owner, so ownership assertions read the column).
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
}
