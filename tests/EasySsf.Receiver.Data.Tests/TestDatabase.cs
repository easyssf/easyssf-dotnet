using System.Data.Common;

using Microsoft.Data.Sqlite;

using Npgsql;

using Testcontainers.PostgreSql;

namespace EasySsf.Receiver.Data.Tests;

/// <summary>
/// An empty database for one test: none of the tables of the stores exist. SQLite is a file in the
/// temporary directory, PostgreSQL a database of its own in a Testcontainers container shared by
/// the test run. The PostgreSQL case only exists when <c>EASYSSF_DATABASE_TESTS</c> is set, so a
/// normal <c>dotnet test</c> needs no Docker.
/// </summary>
public sealed class TestDatabase : IAsyncDisposable
{
    public const string Sqlite = "sqlite";

    public const string Postgres = "postgres";

    private const string PostgresImage = "postgres:18.4";

    private static readonly Lazy<Task<PostgreSqlContainer>> PostgresContainer = new(async () =>
    {
        PostgreSqlContainer container = new PostgreSqlBuilder(PostgresImage).Build();
        await container.StartAsync();
        return container;
    });

    private readonly Func<ValueTask> _dispose;

    private TestDatabase(string kind, DataSourceSsfDbOperations db, Func<ValueTask> dispose)
    {
        Kind = kind;
        Db = db;
        _dispose = dispose;
    }

    /// <summary>
    /// The databases the tests run against, for <c>[MemberData]</c>.
    /// </summary>
    public static TheoryData<string> All
    {
        get
        {
            TheoryData<string> all = new() { Sqlite };
            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("EASYSSF_DATABASE_TESTS")))
            {
                all.Add(Postgres);
            }
            return all;
        }
    }

    public string Kind { get; }

    public DataSourceSsfDbOperations Db { get; }

    public static async Task<TestDatabase> OpenAsync(string kind)
    {
        return kind switch
        {
            Sqlite => await OpenSqliteAsync(),
            Postgres => await OpenPostgresAsync(),
            _ => throw new ArgumentException("Unknown database " + kind, nameof(kind)),
        };
    }

    public ValueTask DisposeAsync()
    {
        return _dispose();
    }

    public ValueTask ExecuteAsync(string sql)
    {
        return Db.ExecuteAsync(sql);
    }

    public ValueTask<int> UpdateAsync(string sql, params object?[] args)
    {
        return Db.UpdateAsync(sql, args);
    }

    public ValueTask<IReadOnlyList<T>> QueryAsync<T>(string sql, Func<DbDataReader, T> mapper, params object?[] args)
    {
        return Db.QueryAsync(sql, 0, mapper, args);
    }

    public async Task<long> CountAsync(string table)
    {
        return (await QueryAsync("SELECT COUNT(*) FROM " + table, reader => Convert.ToInt64(reader.GetValue(0))))[0];
    }

    private static async Task<TestDatabase> OpenSqliteAsync()
    {
        string file = Path.Combine(Path.GetTempPath(), "easyssf-" + Guid.NewGuid().ToString("N") + ".db");
        string connectionString = new SqliteConnectionStringBuilder { DataSource = file, Mode = SqliteOpenMode.ReadWriteCreate }.ToString();
        DataSourceSsfDbOperations db = new(() => new SqliteConnection(connectionString));
        // several connections write at the same time in the tests of the shared stores
        await db.ExecuteAsync("PRAGMA journal_mode=WAL");
        return new TestDatabase(Sqlite, db, () =>
        {
            SqliteConnection.ClearAllPools();
            foreach (string suffix in new[] { "", "-wal", "-shm" })
            {
                File.Delete(file + suffix);
            }
            return ValueTask.CompletedTask;
        });
    }

    private static async Task<TestDatabase> OpenPostgresAsync()
    {
        PostgreSqlContainer container = await PostgresContainer.Value;
        string database = "t_" + Guid.NewGuid().ToString("N");
        await using (NpgsqlDataSource admin = NpgsqlDataSource.Create(container.GetConnectionString()))
        {
            await using NpgsqlCommand create = admin.CreateCommand("CREATE DATABASE " + database);
            await create.ExecuteNonQueryAsync();
        }
        NpgsqlConnectionStringBuilder builder = new(container.GetConnectionString()) { Database = database };
        NpgsqlDataSource dataSource = NpgsqlDataSource.Create(builder);
        return new TestDatabase(Postgres, new DataSourceSsfDbOperations(dataSource), async () =>
        {
            await dataSource.DisposeAsync();
            await using NpgsqlDataSource admin = NpgsqlDataSource.Create(container.GetConnectionString());
            await using NpgsqlCommand drop = admin.CreateCommand("DROP DATABASE " + database + " WITH (FORCE)");
            await drop.ExecuteNonQueryAsync();
        });
    }
}
