using Xunit;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace LoginAndReg.Tests;

/// <summary>
/// The 4.3a schema change <c>UserFilesUserIdToText</c> must preserve the
/// existing numeric owners as strings (5 → '5') so legacy files keep
/// belonging to their owners after the switchover. This test replays the
/// upgrade on a scratch database: the old schema with a legacy row, then the
/// full migration chain.
/// </summary>
public sealed class MigrationPreservationTests
{
    private const string InitialCreateMigration = "20260421091456_InitialCreate";
    private const string ScratchDatabase = "monolith_mig_test";

    [Fact]
    public async Task IntToTextMigration_PreservesLegacyOwners()
    {
        var serverConnection = ServerConnectionString();

        // Fresh scratch database for this test.
        await using (var connection = new NpgsqlConnection(serverConnection))
        {
            await connection.OpenAsync();
            await ExecAsync(connection, $"DROP DATABASE IF EXISTS {ScratchDatabase}");
            await ExecAsync(connection, $"CREATE DATABASE {ScratchDatabase}");
        }

        var scratchConnection = DbConnectionWith(ScratchDatabase);

        try
        {
            // 1. Apply only the original schema (user_files.user_id is int).
            //    EF 8 has no MigrateAsync(target) overload, so the migrator is
            //    driven directly for the single-target application.
            await using (var context = CreateContext(scratchConnection))
            {
                var migrator = (IMigrator)context.GetService<IMigrator>();
                migrator.Migrate(InitialCreateMigration);
            }

            // 2. Seed a legacy row with a numeric owner, like production data.
            await using (var seedConnection = new NpgsqlConnection(scratchConnection))
            {
                await seedConnection.OpenAsync();
                // The old schema has an FK user_files.user_id -> users.id, so
                // the seeded file must reference an existing user (id = 5).
                await ExecAsync(seedConnection,
                    "INSERT INTO \"Users\" (id, login, password_hash, salt) " +
                    "VALUES (5, 'legacy', 'x', 'x')");
                await using var insert = new NpgsqlCommand(
                    "INSERT INTO \"UserFiles\" (id, user_id, file_name, storage_path, file_size, created_at) " +
                    "VALUES (gen_random_uuid(), 5, 'legacy.glb', '5/legacy.glb', 1, now())",
                    seedConnection);
                await insert.ExecuteNonQueryAsync();
            }

            // 3. Run the full migration chain (adds UserFilesUserIdToText).
            await using (var context = CreateContext(scratchConnection))
            {
                await context.Database.MigrateAsync();
            }

            // 4. The column is text now and the value survived as '5'.
            await using var checkConnection = new NpgsqlConnection(scratchConnection);
            await checkConnection.OpenAsync();
            await using var check = new NpgsqlCommand(
                "SELECT uf.user_id::text, format_type(a.atttypid, a.atttypmod) " +
                "FROM \"UserFiles\" uf " +
                "JOIN pg_attribute a " +
                "  ON a.attrelid = '\"UserFiles\"'::regclass AND a.attname = 'user_id' " +
                "WHERE uf.file_name = 'legacy.glb'",
                checkConnection);
            using var reader = (Npgsql.NpgsqlDataReader)await check.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            var value = reader.GetString(0);
            var columnType = reader.GetString(1);
            Assert.Equal("5", value);
            Assert.Equal("text", columnType);
        }
        finally
        {
            // Always clean up the scratch database. Pooled connections keep
            // live sessions to the database, and Postgres refuses to drop a
            // database that has one — clear the pools first.
            try
            {
                NpgsqlConnection.ClearAllPools();
                await using var connection = new NpgsqlConnection(serverConnection);
                await connection.OpenAsync();
                await ExecAsync(connection, $"DROP DATABASE IF EXISTS {ScratchDatabase}");
            }
            catch (NpgsqlException)
            {
                // Best effort: the next run drops it again.
            }
        }
    }

    private static AppDbContext CreateContext(string connectionString)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(connectionString)
            .Options;
        return new AppDbContext(options);
    }

    private static string ServerConnectionString() =>
        MonolithAppFactory.DbConnection.Replace(
            "Database=" + ParseDatabase(MonolithAppFactory.DbConnection) + ";",
            "Database=postgres;");

    private static string DbConnectionWith(string database) =>
        MonolithAppFactory.DbConnection.Replace(
            "Database=" + ParseDatabase(MonolithAppFactory.DbConnection) + ";",
            $"Database={database};");

    private static string ParseDatabase(string connectionString)
    {
        var part = connectionString.Split(';')
            .FirstOrDefault(p => p.StartsWith("Database=", StringComparison.OrdinalIgnoreCase));
        return part is null ? "monolith_test" : part["Database=".Length..].Trim();
    }

    private static async Task ExecAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}
