using System.Text.RegularExpressions;

namespace EasySsf.Receiver.Data;

/// <summary>
/// The tables of the database stores. The same statements, with the default table prefix, are in
/// <c>schema.sql</c> of this assembly (<see cref="SchemaSql"/>) to create the tables with a
/// migration tool, and the changes release by release are in <see cref="Migrations"/>. The tables
/// are those of the Java library easyssf: an application of either can share them.
/// </summary>
public static partial class DbSsfSchema
{
    /// <summary>
    /// The default prefix of the table names.
    /// </summary>
    public const string DefaultTablePrefix = "EASYSSF_";

    /// <summary>
    /// Where the statements are, for messages: the embedded <c>schema.sql</c> of the
    /// <c>EasySsf.Receiver.Data</c> assembly, see <see cref="SchemaSql"/>.
    /// </summary>
    public const string SchemaLocation = "schema.sql of EasySsf.Receiver.Data (DbSsfSchema.SchemaSql)";

    /// <summary>
    /// Where the versioned migration scripts are, one per release that changed the schema, named for
    /// Flyway (<c>V0_1_0__...sql</c>) and plain SQL for any other tool, see <see cref="Migrations"/>.
    /// </summary>
    public const string MigrationLocation = "migration/ of EasySsf.Receiver.Data (DbSsfSchema.Migrations)";

    private static readonly string[] MigrationNames =
    [
        "V0_1_0__processed_sets_and_revocations.sql",
        "V0_3_0__dedup_state_and_poll_acks.sql",
        "V0_4_0__state_changed_at.sql",
    ];

    private static readonly Lazy<string> Schema = new(() => Resource("schema.sql"));

    private static readonly Lazy<IReadOnlyList<DbSsfMigration>> MigrationScripts = new(() =>
        MigrationNames.Select(name => new DbSsfMigration(name, Resource("migration/" + name))).ToArray());

    /// <summary>
    /// The statements that create the tables of this release, with the default table prefix, as a
    /// script: <c>schema.sql</c>.
    /// </summary>
    public static string SchemaSql => Schema.Value;

    /// <summary>
    /// The migration scripts, oldest first: a fresh installation runs all of them, or
    /// <see cref="SchemaSql"/>; an installation of an older release runs those it has not run yet.
    /// </summary>
    public static IReadOnlyList<DbSsfMigration> Migrations => MigrationScripts.Value;

    /// <summary>
    /// The name of the table of the <see cref="DbSsfJtiDedupStore"/>.
    /// </summary>
    public static string ProcessedSetTable(string tablePrefix)
    {
        return ValidPrefix(tablePrefix) + "PROCESSED_SET";
    }

    /// <summary>
    /// The name of the table of the <see cref="DbSsfTokenRevocationStore"/>.
    /// </summary>
    public static string RevocationTable(string tablePrefix)
    {
        return ValidPrefix(tablePrefix) + "REVOCATION";
    }

    /// <summary>
    /// The name of the table of the <see cref="DbSsfPollAckStore"/>.
    /// </summary>
    public static string PollAckTable(string tablePrefix)
    {
        return ValidPrefix(tablePrefix) + "POLL_ACK";
    }

    /// <summary>
    /// The statements that create the table of the <see cref="DbSsfJtiDedupStore"/>.
    /// </summary>
    public static IReadOnlyList<string> CreateProcessedSetTable(string tablePrefix)
    {
        string table = ProcessedSetTable(tablePrefix);
        string name = table.Replace('.', '_');
        return
        [
            $"""
            CREATE TABLE {table} (
                ISSUER VARCHAR(255) NOT NULL,
                JTI VARCHAR(255) NOT NULL,
                STATE VARCHAR(16) NOT NULL,
                STATE_CHANGED_AT BIGINT NOT NULL,
                CONSTRAINT {name}_PK PRIMARY KEY (ISSUER, JTI)
            )
            """,
            $"CREATE INDEX {name}_IX1 ON {table} (STATE_CHANGED_AT)",
        ];
    }

    /// <summary>
    /// The columns later releases added to the table of the <see cref="DbSsfJtiDedupStore"/>:
    /// <c>STATE</c> in 0.3.0, and <c>STATE_CHANGED_AT</c> in 0.4.0, which is <c>PROCESSED_AT</c>
    /// renamed because it records the claim as well as the completion.
    /// </summary>
    public static IReadOnlyList<DbSsfSchemaUpgrade> ProcessedSetUpgrades(string tablePrefix)
    {
        string table = ProcessedSetTable(tablePrefix);
        return
        [
            new DbSsfSchemaUpgrade("STATE", "ALTER TABLE " + table + " ADD STATE VARCHAR(16) DEFAULT 'PROCESSED' NOT NULL"),
            new DbSsfSchemaUpgrade("STATE_CHANGED_AT", "ALTER TABLE " + table + " RENAME COLUMN PROCESSED_AT TO STATE_CHANGED_AT"),
        ];
    }

    /// <summary>
    /// The statements that create the table of the <see cref="DbSsfTokenRevocationStore"/>.
    /// </summary>
    public static IReadOnlyList<string> CreateRevocationTable(string tablePrefix)
    {
        string table = RevocationTable(tablePrefix);
        string name = table.Replace('.', '_');
        return
        [
            $"""
            CREATE TABLE {table} (
                KIND VARCHAR(16) NOT NULL,
                ISSUER VARCHAR(255) NOT NULL,
                ID VARCHAR(255) NOT NULL,
                REVOKED_AT BIGINT NOT NULL,
                EXPIRES_AT BIGINT NOT NULL,
                CONSTRAINT {name}_PK PRIMARY KEY (KIND, ISSUER, ID)
            )
            """,
            $"CREATE INDEX {name}_IX1 ON {table} (EXPIRES_AT)",
        ];
    }

    /// <summary>
    /// The statements that create the table of the <see cref="DbSsfPollAckStore"/>.
    /// </summary>
    public static IReadOnlyList<string> CreatePollAckTable(string tablePrefix)
    {
        string table = PollAckTable(tablePrefix);
        string name = table.Replace('.', '_');
        return
        [
            $"""
            CREATE TABLE {table} (
                ISSUER VARCHAR(255) NOT NULL,
                JTI VARCHAR(255) NOT NULL,
                ERROR_CODE VARCHAR(64),
                ERROR_DESCRIPTION VARCHAR(1024),
                RECORDED_AT BIGINT NOT NULL,
                CONSTRAINT {name}_PK PRIMARY KEY (ISSUER, JTI)
            )
            """,
            $"CREATE INDEX {name}_IX1 ON {table} (RECORDED_AT)",
        ];
    }

    /// <summary>
    /// Makes sure a table exists.
    /// </summary>
    /// <param name="db">runs the statements</param>
    /// <param name="table">the name of the table</param>
    /// <param name="createStatements">the statements that create it</param>
    /// <param name="create">whether to create the table if it does not exist</param>
    /// <param name="hint">
    /// what else the application could do, appended to the message of the exception, may be
    /// <see langword="null"/>
    /// </param>
    /// <param name="cancellationToken">cancels the call</param>
    /// <exception cref="InvalidOperationException">if the table does not exist and is not to be created</exception>
    public static ValueTask PrepareTableAsync(ISsfDbOperations db, string table, IReadOnlyList<string> createStatements, bool create,
            string? hint, CancellationToken cancellationToken = default)
    {
        return PrepareTableAsync(db, table, createStatements, [], create, hint, cancellationToken);
    }

    /// <summary>
    /// Makes sure a table exists and has the columns of this release.
    /// </summary>
    /// <param name="db">runs the statements</param>
    /// <param name="table">the name of the table</param>
    /// <param name="createStatements">the statements that create it</param>
    /// <param name="upgrades">the columns older releases lack and the statements that add them</param>
    /// <param name="create">
    /// whether to create a missing table, and to add missing columns to an existing one
    /// </param>
    /// <param name="hint">
    /// what else the application could do, appended to the message of the exception, may be
    /// <see langword="null"/>
    /// </param>
    /// <param name="cancellationToken">cancels the call</param>
    /// <exception cref="InvalidOperationException">
    /// if the table does not exist or lacks a column and is not to be changed; the message carries
    /// the statements to run
    /// </exception>
    public static async ValueTask PrepareTableAsync(ISsfDbOperations db, string table, IReadOnlyList<string> createStatements,
            IReadOnlyList<DbSsfSchemaUpgrade> upgrades, bool create, string? hint, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentException.ThrowIfNullOrWhiteSpace(table);
        ArgumentNullException.ThrowIfNull(createStatements);
        ArgumentNullException.ThrowIfNull(upgrades);
        string orElse = string.IsNullOrWhiteSpace(hint) ? "" : ", " + hint;
        if (!await db.TableExistsAsync(table, cancellationToken).ConfigureAwait(false))
        {
            if (!create)
            {
                throw new InvalidOperationException($"The table {table} of the easyssf receiver does not exist. Create it with the statements in "
                    + SchemaLocation + orElse + ".");
            }
            foreach (string statement in createStatements)
            {
                await db.ExecuteAsync(statement, cancellationToken).ConfigureAwait(false);
            }
            return;
        }
        IReadOnlyList<DbSsfSchemaUpgrade> missing = await MissingUpgradesAsync(db, table, upgrades, cancellationToken).ConfigureAwait(false);
        if (missing.Count == 0)
        {
            return;
        }
        if (!create)
        {
            throw new InvalidOperationException(PredatesMessage(table, missing) + orElse + ".");
        }
        foreach (DbSsfSchemaUpgrade upgrade in missing)
        {
            try
            {
                await db.ExecuteAsync(upgrade.Statement, cancellationToken).ConfigureAwait(false);
            }
            catch (SsfDbException)
            {
                // another instance starting at the same time may have added the column
                if (!await ColumnExistsAsync(db, table, upgrade.Column, cancellationToken).ConfigureAwait(false))
                {
                    throw;
                }
            }
        }
    }

    /// <summary>
    /// The upgrades whose column the existing table lacks.
    /// </summary>
    public static async ValueTask<IReadOnlyList<DbSsfSchemaUpgrade>> MissingUpgradesAsync(ISsfDbOperations db, string table,
            IReadOnlyList<DbSsfSchemaUpgrade> upgrades, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(upgrades);
        List<DbSsfSchemaUpgrade> missing = [];
        foreach (DbSsfSchemaUpgrade upgrade in upgrades)
        {
            if (!await ColumnExistsAsync(db, table, upgrade.Column, cancellationToken).ConfigureAwait(false))
            {
                missing.Add(upgrade);
            }
        }
        return missing;
    }

    /// <summary>
    /// The message that a table predates this release, with the statements to run.
    /// </summary>
    internal static string PredatesMessage(string table, IReadOnlyList<DbSsfSchemaUpgrade> missing)
    {
        return $"The table {table} of the easyssf receiver predates this release and lacks the column(s) ["
            + string.Join(", ", missing.Select(upgrade => upgrade.Column)) + "]. Run ["
            + string.Join(", ", missing.Select(upgrade => upgrade.Statement)) + "] or the migration scripts in " + MigrationLocation;
    }

    /// <summary>
    /// Splits a script (<see cref="SchemaSql"/>, a migration) into its statements: comments
    /// removed, separated at <c>;</c>, whitespace normalised.
    /// </summary>
    public static IReadOnlyList<string> Statements(string script)
    {
        ArgumentNullException.ThrowIfNull(script);
        return CommentLine().Replace(script, "")
            .Split(';')
            .Select(statement => Whitespace().Replace(statement, " ").Trim())
            .Where(statement => statement.Length > 0)
            .ToArray();
    }

    private static async ValueTask<bool> ColumnExistsAsync(ISsfDbOperations db, string table, string column, CancellationToken cancellationToken)
    {
        try
        {
            await db.QueryAsync("SELECT " + column + " FROM " + table + " WHERE 1 = 0", reader => reader.GetValue(0), [], cancellationToken)
                .ConfigureAwait(false);
            return true;
        }
        catch (SsfDbException)
        {
            return false;
        }
    }

    private static string ValidPrefix(string tablePrefix)
    {
        if (tablePrefix is null || !TablePrefix().IsMatch(tablePrefix))
        {
            throw new ArgumentException("The table prefix may only consist of letters, digits, '_' and '.'", nameof(tablePrefix));
        }
        return tablePrefix;
    }

    private static string Resource(string name)
    {
        using System.IO.Stream? stream = typeof(DbSsfSchema).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException("The resource " + name + " is missing from " + typeof(DbSsfSchema).Assembly.GetName().Name);
        using StreamReader reader = new(stream);
        return reader.ReadToEnd();
    }

    [GeneratedRegex("^[A-Za-z0-9_.]*$")]
    private static partial Regex TablePrefix();

    [GeneratedRegex("^--.*$", RegexOptions.Multiline)]
    private static partial Regex CommentLine();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}

/// <summary>
/// A column a current table has and the statement that adds it to a table of an older release.
/// </summary>
/// <param name="Column">the name of the column</param>
/// <param name="Statement">the <c>ALTER TABLE</c> that adds it, with a default for the rows that exist</param>
public sealed record DbSsfSchemaUpgrade(string Column, string Statement);

/// <summary>
/// A migration script: the schema changes of one release.
/// </summary>
/// <param name="Name">the file name, <c>V&lt;release&gt;__&lt;topic&gt;.sql</c> as Flyway expects it</param>
/// <param name="Sql">the script, see <see cref="DbSsfSchema.Statements"/></param>
public sealed record DbSsfMigration(string Name, string Sql);
