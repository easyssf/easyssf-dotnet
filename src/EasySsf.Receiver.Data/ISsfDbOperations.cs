using System.Data.Common;

namespace EasySsf.Receiver.Data;

/// <summary>
/// Runs the SQL of the stores. Implement it over whatever the application uses to access its
/// database: <see cref="DataSourceSsfDbOperations"/> over a <see cref="DbDataSource"/> or a
/// connection factory, or a framework's unit of work so that its transactions apply.
/// <para>
/// The statements of the stores use <c>?</c> as placeholder, in the order of the arguments, and
/// standard SQL that runs unchanged on SQLite, PostgreSQL, MySQL and SQL Server.
/// </para>
/// </summary>
public interface ISsfDbOperations
{
    /// <summary>
    /// Runs an insert, update or delete.
    /// </summary>
    /// <param name="sql">the statement with <c>?</c> placeholders</param>
    /// <param name="args">the values of the placeholders, <see langword="null"/> for SQL NULL</param>
    /// <param name="cancellationToken">cancels the call</param>
    /// <returns>the number of rows affected</returns>
    /// <exception cref="SsfDbDuplicateKeyException">if the statement violates a unique constraint</exception>
    /// <exception cref="SsfDbException">if the statement fails otherwise</exception>
    ValueTask<int> UpdateAsync(string sql, object?[] args, CancellationToken cancellationToken = default);

    /// <summary>
    /// Runs a query and returns at most <paramref name="maxRows"/> rows. The limit is applied by
    /// reading no further, which every provider supports, rather than with the SQL for it, which
    /// differs between databases (<c>FETCH FIRST</c>, <c>LIMIT</c>, <c>TOP</c>).
    /// </summary>
    /// <param name="sql">the query with <c>?</c> placeholders</param>
    /// <param name="maxRows">the most rows to return, zero or negative for all</param>
    /// <param name="mapper">maps the current row of the reader</param>
    /// <param name="args">the values of the placeholders</param>
    /// <param name="cancellationToken">cancels the call</param>
    /// <returns>the mapped rows</returns>
    /// <exception cref="SsfDbException">if the query fails</exception>
    ValueTask<IReadOnlyList<T>> QueryAsync<T>(string sql, int maxRows, Func<DbDataReader, T> mapper, object?[] args,
            CancellationToken cancellationToken = default);

    /// <summary>
    /// Runs a query.
    /// </summary>
    /// <param name="sql">the query with <c>?</c> placeholders</param>
    /// <param name="mapper">maps the current row of the reader</param>
    /// <param name="args">the values of the placeholders</param>
    /// <param name="cancellationToken">cancels the call</param>
    /// <returns>the mapped rows</returns>
    /// <exception cref="SsfDbException">if the query fails</exception>
    ValueTask<IReadOnlyList<T>> QueryAsync<T>(string sql, Func<DbDataReader, T> mapper, object?[] args,
            CancellationToken cancellationToken = default)
    {
        return QueryAsync(sql, 0, mapper, args, cancellationToken);
    }

    /// <summary>
    /// Runs a statement that returns nothing, for example DDL.
    /// </summary>
    /// <exception cref="SsfDbException">if the statement fails</exception>
    ValueTask ExecuteAsync(string sql, CancellationToken cancellationToken = default);

    /// <summary>
    /// Whether the table exists.
    /// </summary>
    ValueTask<bool> TableExistsAsync(string table, CancellationToken cancellationToken = default);
}
