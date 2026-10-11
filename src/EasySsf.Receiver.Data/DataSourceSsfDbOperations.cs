using System.Data;
using System.Data.Common;
using System.Text;

namespace EasySsf.Receiver.Data;

/// <summary>
/// <see cref="ISsfDbOperations"/> on plain ADO.NET over a <see cref="DbDataSource"/> or a
/// connection factory. Every statement opens a connection and closes it; the statements of the
/// stores are single statements, so no transaction is started.
/// <para>
/// The <c>?</c> placeholders of the stores become named parameters <c>@p0</c>, <c>@p1</c>, ...,
/// which SQLite, PostgreSQL (Npgsql), MySQL and SQL Server accept; set <see cref="ParameterPrefix"/>
/// to <c>:</c> for Oracle. A unique constraint violation and a missing table are recognised from
/// the SQLSTATE of the provider's exception where it gives one (PostgreSQL, MySQL), otherwise from
/// its message; <see cref="IsDuplicateKey"/> and <see cref="IsMissingTable"/> adapt that for a
/// provider the defaults do not know.
/// </para>
/// </summary>
public class DataSourceSsfDbOperations : ISsfDbOperations
{
    /// <summary>
    /// SQLSTATE classes of a statement the database did not understand, among them a missing table:
    /// standard class 42, and the classes some databases use instead.
    /// </summary>
    private static readonly HashSet<string> BadGrammarClasses = new(["07", "21", "2A", "37", "42", "65", "S0"], StringComparer.Ordinal);

    private static readonly string[] DuplicateKeyMessages =
    [
        "UNIQUE constraint failed", // SQLite
        "PRIMARY KEY constraint failed", // SQLite before 3.8
        "duplicate key", // PostgreSQL, SQL Server
        "Duplicate entry", // MySQL, MariaDB
        "Violation of PRIMARY KEY constraint", // SQL Server
        "Violation of UNIQUE KEY constraint", // SQL Server
        "ORA-00001", // Oracle
    ];

    private static readonly string[] MissingTableMessages =
    [
        "no such table", // SQLite
        "does not exist", // PostgreSQL
        "doesn't exist", // MySQL, MariaDB
        "Invalid object name", // SQL Server
        "ORA-00942", // Oracle
    ];

    private readonly Func<DbConnection> _connections;

    /// <param name="dataSource">the data source of the application, used for a connection per statement</param>
    public DataSourceSsfDbOperations(DbDataSource dataSource)
        : this(Connections(dataSource))
    {
    }

    /// <param name="connectionFactory">
    /// creates a new, closed connection per statement, for providers without a
    /// <see cref="DbDataSource"/>; the connection is disposed after the statement
    /// </param>
    public DataSourceSsfDbOperations(Func<DbConnection> connectionFactory)
    {
        ArgumentNullException.ThrowIfNull(connectionFactory);
        _connections = connectionFactory;
    }

    /// <summary>
    /// What the names of the parameters start with: <c>@</c> by default, <c>:</c> for Oracle.
    /// </summary>
    public string ParameterPrefix { get; set; } = "@";

    /// <summary>
    /// Tells whether the exception of the provider reports a unique constraint violation: SQLSTATE
    /// class 23 or a known message by default.
    /// </summary>
    public Func<DbException, bool> IsDuplicateKey { get; set; } = DefaultIsDuplicateKey;

    /// <summary>
    /// Tells whether the exception of the provider reports a table that does not exist: SQLSTATE
    /// class 42 (and the classes some databases use instead) or a known message by default.
    /// </summary>
    public Func<DbException, bool> IsMissingTable { get; set; } = DefaultIsMissingTable;

    /// <summary>
    /// Whether the connections are to an embedded database (SQLite), which an application creates
    /// the tables in without a migration tool.
    /// </summary>
    public bool IsEmbeddedDatabase()
    {
        using DbConnection connection = _connections();
        return connection.GetType().Name.Contains("Sqlite", StringComparison.OrdinalIgnoreCase);
    }

    public async ValueTask<int> UpdateAsync(string sql, object?[] args, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sql);
        ArgumentNullException.ThrowIfNull(args);
        try
        {
            await using DbConnection connection = _connections();
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using DbCommand command = Command(connection, sql, args);
            return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbException ex)
        {
            if (IsDuplicateKey(ex))
            {
                throw new SsfDbDuplicateKeyException("Duplicate key: " + sql, ex.SqlState, ex);
            }
            throw new SsfDbException($"Could not run {sql}: {ex.Message}", ex.SqlState, ex);
        }
    }

    public async ValueTask<IReadOnlyList<T>> QueryAsync<T>(string sql, int maxRows, Func<DbDataReader, T> mapper, object?[] args,
            CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sql);
        ArgumentNullException.ThrowIfNull(mapper);
        ArgumentNullException.ThrowIfNull(args);
        try
        {
            await using DbConnection connection = _connections();
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using DbCommand command = Command(connection, sql, args);
            await using DbDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            List<T> rows = [];
            while ((maxRows <= 0 || rows.Count < maxRows) && await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                rows.Add(mapper(reader));
            }
            return rows;
        }
        catch (DbException ex)
        {
            throw new SsfDbException($"Could not run {sql}: {ex.Message}", ex.SqlState, ex);
        }
    }

    public async ValueTask ExecuteAsync(string sql, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sql);
        try
        {
            await using DbConnection connection = _connections();
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using DbCommand command = connection.CreateCommand();
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbException ex)
        {
            throw new SsfDbException($"Could not run {sql}: {ex.Message}", ex.SqlState, ex);
        }
    }

    public async ValueTask<bool> TableExistsAsync(string table, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(table);
        string sql = "SELECT COUNT(*) FROM " + table + " WHERE 1 = 0";
        try
        {
            await using DbConnection connection = _connections();
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using DbCommand command = connection.CreateCommand();
            command.CommandText = sql;
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (DbException ex)
        {
            if (IsMissingTable(ex))
            {
                return false;
            }
            throw new SsfDbException($"Could not run {sql}: {ex.Message}", ex.SqlState, ex);
        }
    }

    /// <summary>
    /// Whether the exception reports a unique constraint violation: SQLSTATE class 23, or one of the
    /// messages of SQLite, PostgreSQL, MySQL, SQL Server and Oracle.
    /// </summary>
    public static bool DefaultIsDuplicateKey(DbException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        if (exception.SqlState is { Length: >= 2 } sqlState)
        {
            return sqlState.StartsWith("23", StringComparison.Ordinal);
        }
        return DuplicateKeyMessages.Any(message => exception.Message.Contains(message, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Whether the exception reports a table that does not exist: a SQLSTATE class of a statement
    /// the database did not understand, or one of the messages of SQLite, PostgreSQL, MySQL, SQL
    /// Server and Oracle.
    /// </summary>
    public static bool DefaultIsMissingTable(DbException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        if (exception.SqlState is { Length: >= 2 } sqlState)
        {
            return BadGrammarClasses.Contains(sqlState[..2]);
        }
        return MissingTableMessages.Any(message => exception.Message.Contains(message, StringComparison.OrdinalIgnoreCase));
    }

    private DbCommand Command(DbConnection connection, string sql, object?[] args)
    {
        DbCommand command = connection.CreateCommand();
        command.CommandText = NameParameters(sql, args.Length);
        for (int i = 0; i < args.Length; i++)
        {
            DbParameter parameter = command.CreateParameter();
            parameter.ParameterName = ParameterPrefix + "p" + i;
            parameter.Value = args[i] ?? DBNull.Value;
            if (args[i] is null)
            {
                parameter.DbType = DbType.String;
            }
            command.Parameters.Add(parameter);
        }
        return command;
    }

    /// <summary>
    /// Replaces the <c>?</c> placeholders by named parameters, in order.
    /// </summary>
    private string NameParameters(string sql, int count)
    {
        StringBuilder named = new(sql.Length + count * 4);
        int index = 0;
        foreach (char c in sql)
        {
            if (c == '?')
            {
                if (index >= count)
                {
                    throw new ArgumentException($"The statement has more placeholders than arguments ({count}): {sql}", nameof(sql));
                }
                named.Append(ParameterPrefix).Append('p').Append(index++);
            }
            else
            {
                named.Append(c);
            }
        }
        if (index != count)
        {
            throw new ArgumentException($"The statement has {index} placeholder(s) but {count} argument(s): {sql}", nameof(sql));
        }
        return named.ToString();
    }

    private static Func<DbConnection> Connections(DbDataSource dataSource)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        return dataSource.CreateConnection;
    }
}
