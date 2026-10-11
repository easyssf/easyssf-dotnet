using System.Data.Common;

using Microsoft.Data.Sqlite;

namespace EasySsf.Receiver.Data.Tests;

public sealed class DataSourceSsfDbOperationsTests
{
    [Theory]
    [MemberData(nameof(TestDatabase.All), MemberType = typeof(TestDatabase))]
    public async Task ReportsDuplicateKeysMissingTablesAndOtherFailures(string database)
    {
        await using TestDatabase db = await TestDatabase.OpenAsync(database);
        ISsfDbOperations jdbc = db.Db;
        Assert.False(await jdbc.TableExistsAsync("T"));
        await jdbc.ExecuteAsync("CREATE TABLE T (ID VARCHAR(10) PRIMARY KEY, N INT)");
        Assert.True(await jdbc.TableExistsAsync("T"));
        Assert.Equal(1, await jdbc.UpdateAsync("INSERT INTO T (ID, N) VALUES (?, ?)", ["a", 1]));
        await Assert.ThrowsAsync<SsfDbDuplicateKeyException>(() => jdbc.UpdateAsync("INSERT INTO T (ID, N) VALUES (?, ?)", ["a", 2]).AsTask());
        Assert.Equal([1], await jdbc.QueryAsync("SELECT N FROM T WHERE ID = ?", reader => Convert.ToInt32(reader.GetValue(0)), ["a"]));
        SsfDbException ex = await Assert.ThrowsAsync<SsfDbException>(() => jdbc.UpdateAsync("INSERT INTO NOWHERE VALUES (1)", []).AsTask());
        Assert.IsNotType<SsfDbDuplicateKeyException>(ex);
        Assert.IsAssignableFrom<DbException>(ex.InnerException);
    }

    [Theory]
    [MemberData(nameof(TestDatabase.All), MemberType = typeof(TestDatabase))]
    public async Task LimitsRowsBindsNullsAndRejectsMismatchedPlaceholders(string database)
    {
        await using TestDatabase db = await TestDatabase.OpenAsync(database);
        ISsfDbOperations jdbc = db.Db;
        await jdbc.ExecuteAsync("CREATE TABLE T (ID VARCHAR(10) PRIMARY KEY, NOTE VARCHAR(10))");
        await jdbc.UpdateAsync("INSERT INTO T (ID, NOTE) VALUES (?, ?)", ["a", null]);
        await jdbc.UpdateAsync("INSERT INTO T (ID, NOTE) VALUES (?, ?)", ["b", "note"]);
        await jdbc.UpdateAsync("INSERT INTO T (ID, NOTE) VALUES (?, ?)", ["c", "note"]);
        Assert.Equal(["a", "b"], await jdbc.QueryAsync("SELECT ID FROM T ORDER BY ID", 2, reader => reader.GetString(0), []));
        Assert.Equal(["a", "b", "c"], await jdbc.QueryAsync("SELECT ID FROM T ORDER BY ID", 0, reader => reader.GetString(0), []));
        Assert.Equal(["a"], await jdbc.QueryAsync("SELECT ID FROM T WHERE NOTE IS NULL", reader => reader.GetString(0), []));
        await Assert.ThrowsAsync<ArgumentException>(() => jdbc.UpdateAsync("INSERT INTO T (ID, NOTE) VALUES (?, ?)", ["d"]).AsTask());
        await Assert.ThrowsAsync<ArgumentException>(() => jdbc.UpdateAsync("INSERT INTO T (ID, NOTE) VALUES (?, ?)", ["d", "e", "f"]).AsTask());
    }

    [Fact]
    public void TellsEmbeddedDatabasesApart()
    {
        Assert.True(new DataSourceSsfDbOperations(() => new SqliteConnection("Data Source=:memory:")).IsEmbeddedDatabase());
        Assert.False(new DataSourceSsfDbOperations(() => new Npgsql.NpgsqlConnection()).IsEmbeddedDatabase());
    }

    [Fact]
    public void RecognisesTheMessagesOfTheCommonDatabases()
    {
        Assert.True(DataSourceSsfDbOperations.DefaultIsDuplicateKey(new TestDbException("SQLite Error 19: 'UNIQUE constraint failed: T.ID'.", null)));
        Assert.True(DataSourceSsfDbOperations.DefaultIsDuplicateKey(new TestDbException("Duplicate entry 'a' for key 'T.PRIMARY'", null)));
        Assert.True(DataSourceSsfDbOperations.DefaultIsDuplicateKey(new TestDbException("Violation of PRIMARY KEY constraint 'PK'. Cannot insert duplicate key", null)));
        Assert.True(DataSourceSsfDbOperations.DefaultIsDuplicateKey(new TestDbException("anything", "23505")));
        Assert.False(DataSourceSsfDbOperations.DefaultIsDuplicateKey(new TestDbException("UNIQUE constraint failed", "42P01")));
        Assert.False(DataSourceSsfDbOperations.DefaultIsDuplicateKey(new TestDbException("database is locked", null)));
        Assert.True(DataSourceSsfDbOperations.DefaultIsMissingTable(new TestDbException("SQLite Error 1: 'no such table: T'.", null)));
        Assert.True(DataSourceSsfDbOperations.DefaultIsMissingTable(new TestDbException("Invalid object name 'T'.", null)));
        Assert.True(DataSourceSsfDbOperations.DefaultIsMissingTable(new TestDbException("Table 'db.T' doesn't exist", "42S02")));
        Assert.True(DataSourceSsfDbOperations.DefaultIsMissingTable(new TestDbException("relation \"t\" does not exist", "42P01")));
        Assert.False(DataSourceSsfDbOperations.DefaultIsMissingTable(new TestDbException("connection refused", "08001")));
    }

    private sealed class TestDbException(string message, string? sqlState) : DbException(message)
    {
        public override string? SqlState => sqlState;
    }
}
