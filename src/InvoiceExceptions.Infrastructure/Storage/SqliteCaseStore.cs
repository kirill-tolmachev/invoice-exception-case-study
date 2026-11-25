using System.Globalization;
using InvoiceExceptions.Application.Storage;
using InvoiceExceptions.Domain.Model;
using Microsoft.Data.Sqlite;

namespace InvoiceExceptions.Infrastructure.Storage;

/// <summary>
/// <see cref="ICaseStore"/> on a single SQLite file, with hand-written SQL.
/// <para>
/// The store is append-only: every table has <c>BEFORE UPDATE</c> and <c>BEFORE DELETE</c> triggers that abort,
/// so history cannot be rewritten even by code that bypasses this class (a bug, a migration script, a person
/// with a SQLite shell). The rule lives next to the data instead of in a convention.
/// </para>
/// <para>
/// No connection outlives an operation: each call opens its own connection, with pooling switched off, and disposes
/// it. That serves both a CLI that runs one process per step (everything is committed and the file is released when
/// <see cref="Append"/> returns) and a web app with concurrent requests (SQLite serialises writers through
/// <c>BEGIN IMMEDIATE</c>; readers take one read transaction per <see cref="Load"/> so they see a consistent snapshot).
/// </para>
/// </summary>
public sealed class SqliteCaseStore : ICaseStore
{
    public const string DefaultFileName = "invoice-exceptions.db";
    public const int SchemaVersion = 1;

    private readonly string _connectionString;

    public SqliteCaseStore(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);

        var fullPath = Path.GetFullPath(databasePath);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        DatabasePath = fullPath;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = fullPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            // A pooled connection keeps the file handle open after Dispose: the file would stay locked on Windows
            // and a CLI process would hold it until exit. Opening a SQLite file is cheap, so we do not pool.
            Pooling = false,
            ForeignKeys = true,
            // Seconds to wait on a lock held by another connection (busy timeout) before failing.
            DefaultTimeout = 30,
        }.ToString();

        EnsureSchema();
    }

    public string DatabasePath { get; }

    public static SqliteCaseStore InDirectory(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        Directory.CreateDirectory(directory);
        return new SqliteCaseStore(Path.Combine(directory, DefaultFileName));
    }

    public IReadOnlyList<string> ListCaseIds()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        // Sorted in .NET: SQLite's BINARY collation compares UTF-8 bytes, which is not the same as .NET's
        // UTF-16 ordinal order for every character.
        command.CommandText = "SELECT case_id FROM cases";
        var ids = new List<string>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            ids.Add(reader.GetString(0));
        }

        ids.Sort(StringComparer.Ordinal);
        return ids;
    }

    public CaseRecord? Load(string caseId)
    {
        ArgumentNullException.ThrowIfNull(caseId);

        using var connection = Open();
        // One read transaction so that a concurrent Append cannot land between the SELECTs.
        using var transaction = connection.BeginTransaction(deferred: true);

        var header = ReadSingle(connection, transaction,
            "SELECT case_id, policy_version, as_of FROM cases WHERE case_id = $case_id",
            caseId,
            r => new CaseHeader(r.GetString(0), r.GetString(1), r.GetString(2)));
        if (header is null)
        {
            return null;
        }

        var revisions = ReadAll(connection, transaction,
            """
            SELECT revision, channel, actor, actor_name, summary, recorded_at
            FROM input_revisions WHERE case_id = $case_id ORDER BY revision
            """,
            caseId,
            r => new RevisionRecord(
                r.GetInt32(0), r.GetString(1), r.GetString(2), GetNullableString(r, 3), r.GetString(4),
                DateTimeOffset.ParseExact(r.GetString(5), "O", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)));

        var contexts = ReadAll(connection, transaction,
            """
            SELECT revision, context_version, payload_json
            FROM context_snapshots WHERE case_id = $case_id ORDER BY revision
            """,
            caseId,
            r => new ContextRecord(r.GetInt32(0), r.GetString(1), r.GetString(2)));

        var documents = ReadAll(connection, transaction,
            """
            SELECT revision, document_id, content_sha256, facts_json, source_path
            FROM documents WHERE case_id = $case_id ORDER BY revision, position
            """,
            caseId,
            r => new DocumentRecord(r.GetInt32(0), r.GetString(1), r.GetString(2), r.GetString(3), GetNullableString(r, 4)));

        var lineMatches = ReadAll(connection, transaction,
            """
            SELECT proposal_id, document_line_id, order_line_id
            FROM proposal_line_matches WHERE case_id = $case_id ORDER BY proposal_id, position
            """,
            caseId,
            r => (ProposalId: r.GetString(0), Match: new LineMatch(r.GetString(1), r.GetString(2))))
            .GroupBy(x => x.ProposalId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<LineMatch>)g.Select(x => x.Match).ToList(), StringComparer.Ordinal);

        var proposals = ReadAll(connection, transaction,
            """
            SELECT revision, proposal_id, document_id, order_id, rationale
            FROM proposals WHERE case_id = $case_id ORDER BY revision, position
            """,
            caseId,
            r => new ProposalRecord(
                r.GetInt32(0), r.GetString(1), r.GetString(2), r.GetString(3),
                lineMatches.GetValueOrDefault(r.GetString(1)) ?? [],
                GetNullableString(r, 4)));

        var confirmations = ReadAll(connection, transaction,
            """
            SELECT revision, proposal_id, confirmed_by
            FROM confirmations WHERE case_id = $case_id ORDER BY revision, position
            """,
            caseId,
            r => new ConfirmationRecord(r.GetInt32(0), r.GetString(1), r.GetString(2)));

        var evaluations = ReadAll(connection, transaction,
            """
            SELECT e.revision, e.document_id, e.import_disposition, e.original_document_id, e.existing_assessment_ref,
                   a.assessment_id, a.policy_version, a.context_version, a.decision, a.result_json
            FROM evaluations e
            LEFT JOIN assessments a
              ON a.case_id = e.case_id AND a.document_id = e.document_id AND a.revision = e.revision
            WHERE e.case_id = $case_id
            ORDER BY e.revision, e.position
            """,
            caseId,
            r => new EvaluationRecord(
                r.GetInt32(0), r.GetString(1), r.GetString(2), GetNullableString(r, 3), GetNullableString(r, 4),
                r.IsDBNull(5)
                    ? null
                    : new AssessmentRecord(r.GetString(5), r.GetString(6), r.GetString(7), r.GetString(8), r.GetString(9))));

        transaction.Commit();
        return new CaseRecord(header, revisions, contexts, documents, proposals, confirmations, evaluations);
    }

    public void Append(CaseCommit commit)
    {
        Validate(commit);
        var caseId = commit.CaseId;
        var revision = commit.Revision.Revision;

        using var connection = Open();
        // BEGIN IMMEDIATE: take the write lock before reading the latest revision, so check-then-insert is atomic.
        // Disposing the transaction without Commit rolls everything back.
        using var transaction = connection.BeginTransaction(deferred: false);

        var exists = Scalar(connection, transaction, "SELECT COUNT(*) FROM cases WHERE case_id = $case_id", caseId) > 0;
        if (commit.NewCase is not null)
        {
            if (exists)
            {
                throw new RevisionConflictException($"Case '{caseId}' already exists; it cannot be created again.");
            }

            if (revision != 1)
            {
                throw new RevisionConflictException(
                    $"Case '{caseId}' is new, so its first revision must be 1, but the commit carries revision {revision}.");
            }

            Execute(connection, transaction,
                "INSERT INTO cases (case_id, policy_version, as_of) VALUES ($case_id, $policy_version, $as_of)",
                ("$case_id", caseId),
                ("$policy_version", commit.NewCase.PolicyVersion),
                ("$as_of", commit.NewCase.AsOf));
        }
        else
        {
            if (!exists)
            {
                throw new RevisionConflictException(
                    $"Case '{caseId}' does not exist; expected a commit that creates it at revision 1, got revision {revision}.");
            }

            var latest = Scalar(connection, transaction,
                "SELECT COALESCE(MAX(revision), 0) FROM input_revisions WHERE case_id = $case_id", caseId);
            if (revision != latest + 1)
            {
                throw new RevisionConflictException(
                    $"Case '{caseId}' is at revision {latest}; expected revision {latest + 1}, got {revision}.");
            }
        }

        var rev = commit.Revision;
        Execute(connection, transaction,
            """
            INSERT INTO input_revisions (case_id, revision, channel, actor, actor_name, summary, recorded_at)
            VALUES ($case_id, $revision, $channel, $actor, $actor_name, $summary, $recorded_at)
            """,
            ("$case_id", caseId),
            ("$revision", revision),
            ("$channel", rev.Channel),
            ("$actor", rev.Actor),
            ("$actor_name", rev.ActorName),
            ("$summary", rev.Summary),
            ("$recorded_at", rev.RecordedAt.UtcDateTime.ToString("O", CultureInfo.InvariantCulture)));

        if (commit.Context is { } context)
        {
            Execute(connection, transaction,
                """
                INSERT INTO context_snapshots (case_id, revision, context_version, payload_json)
                VALUES ($case_id, $revision, $context_version, $payload_json)
                """,
                ("$case_id", caseId),
                ("$revision", revision),
                ("$context_version", context.ContextVersion),
                ("$payload_json", context.PayloadJson));
        }

        for (var i = 0; i < commit.Documents.Count; i++)
        {
            var document = commit.Documents[i];
            Execute(connection, transaction,
                """
                INSERT INTO documents (case_id, document_id, revision, position, content_sha256, facts_json, source_path)
                VALUES ($case_id, $document_id, $revision, $position, $content_sha256, $facts_json, $source_path)
                """,
                ("$case_id", caseId),
                ("$document_id", document.DocumentId),
                ("$revision", revision),
                ("$position", i),
                ("$content_sha256", document.ContentSha256),
                ("$facts_json", document.FactsJson),
                ("$source_path", document.SourcePath));
        }

        for (var i = 0; i < commit.Proposals.Count; i++)
        {
            var proposal = commit.Proposals[i];
            Execute(connection, transaction,
                """
                INSERT INTO proposals (case_id, proposal_id, revision, position, document_id, order_id, rationale)
                VALUES ($case_id, $proposal_id, $revision, $position, $document_id, $order_id, $rationale)
                """,
                ("$case_id", caseId),
                ("$proposal_id", proposal.ProposalId),
                ("$revision", revision),
                ("$position", i),
                ("$document_id", proposal.DocumentId),
                ("$order_id", proposal.OrderId),
                ("$rationale", proposal.Rationale));

            for (var j = 0; j < proposal.LineMatches.Count; j++)
            {
                var match = proposal.LineMatches[j];
                Execute(connection, transaction,
                    """
                    INSERT INTO proposal_line_matches (case_id, proposal_id, position, document_line_id, order_line_id)
                    VALUES ($case_id, $proposal_id, $position, $document_line_id, $order_line_id)
                    """,
                    ("$case_id", caseId),
                    ("$proposal_id", proposal.ProposalId),
                    ("$position", j),
                    ("$document_line_id", match.DocumentLineId),
                    ("$order_line_id", match.OrderLineId));
            }
        }

        for (var i = 0; i < commit.Confirmations.Count; i++)
        {
            var confirmation = commit.Confirmations[i];
            Execute(connection, transaction,
                """
                INSERT INTO confirmations (case_id, proposal_id, revision, position, confirmed_by)
                VALUES ($case_id, $proposal_id, $revision, $position, $confirmed_by)
                """,
                ("$case_id", caseId),
                ("$proposal_id", confirmation.ProposalId),
                ("$revision", revision),
                ("$position", i),
                ("$confirmed_by", confirmation.ConfirmedBy));
        }

        for (var i = 0; i < commit.Evaluations.Count; i++)
        {
            var evaluation = commit.Evaluations[i];
            Execute(connection, transaction,
                """
                INSERT INTO evaluations
                    (case_id, document_id, revision, position, import_disposition, original_document_id, existing_assessment_ref)
                VALUES ($case_id, $document_id, $revision, $position, $import_disposition, $original_document_id, $existing_assessment_ref)
                """,
                ("$case_id", caseId),
                ("$document_id", evaluation.DocumentId),
                ("$revision", revision),
                ("$position", i),
                ("$import_disposition", evaluation.ImportDisposition),
                ("$original_document_id", evaluation.OriginalDocumentId),
                ("$existing_assessment_ref", evaluation.ExistingAssessmentRef));

            if (evaluation.Assessment is { } assessment)
            {
                Execute(connection, transaction,
                    """
                    INSERT INTO assessments
                        (assessment_id, case_id, document_id, revision, policy_version, context_version, decision, result_json)
                    VALUES ($assessment_id, $case_id, $document_id, $revision, $policy_version, $context_version, $decision, $result_json)
                    """,
                    ("$assessment_id", assessment.AssessmentId),
                    ("$case_id", caseId),
                    ("$document_id", evaluation.DocumentId),
                    ("$revision", revision),
                    ("$policy_version", assessment.PolicyVersion),
                    ("$context_version", assessment.ContextVersion),
                    ("$decision", assessment.Decision),
                    ("$result_json", assessment.ResultJson));
            }
        }

        transaction.Commit();
    }

    private static void Validate(CaseCommit commit)
    {
        ArgumentNullException.ThrowIfNull(commit);
        ArgumentException.ThrowIfNullOrWhiteSpace(commit.CaseId, nameof(commit));
        ArgumentNullException.ThrowIfNull(commit.Revision, nameof(commit));
        ArgumentNullException.ThrowIfNull(commit.Documents, nameof(commit));
        ArgumentNullException.ThrowIfNull(commit.Proposals, nameof(commit));
        ArgumentNullException.ThrowIfNull(commit.Confirmations, nameof(commit));
        ArgumentNullException.ThrowIfNull(commit.Evaluations, nameof(commit));

        if (commit.NewCase is not null && !string.Equals(commit.NewCase.CaseId, commit.CaseId, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"The new case header names case '{commit.NewCase.CaseId}' but the commit is for case '{commit.CaseId}'.",
                nameof(commit));
        }

        var revision = commit.Revision.Revision;
        var childRevisions =
            (commit.Context is null ? Enumerable.Empty<(string, int)>() : [("context", commit.Context.Revision)])
            .Concat(commit.Documents.Select(d => ($"document '{d.DocumentId}'", d.Revision)))
            .Concat(commit.Proposals.Select(p => ($"proposal '{p.ProposalId}'", p.Revision)))
            .Concat(commit.Confirmations.Select(c => ($"confirmation of proposal '{c.ProposalId}'", c.Revision)))
            .Concat(commit.Evaluations.Select(e => ($"evaluation of document '{e.DocumentId}'", e.Revision)));
        foreach (var (what, childRevision) in childRevisions)
        {
            if (childRevision != revision)
            {
                throw new ArgumentException(
                    $"The {what} carries revision {childRevision}, but the commit is revision {revision} of case '{commit.CaseId}'.",
                    nameof(commit));
            }
        }
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }

    private void EnsureSchema()
    {
        using var connection = Open();
        // Immediate, so two processes opening a fresh file at once do not both try to create the schema.
        using var transaction = connection.BeginTransaction(deferred: false);

        long version;
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "PRAGMA user_version";
            version = (long)command.ExecuteScalar()!;
        }

        if (version == SchemaVersion)
        {
            return;
        }

        if (version != 0)
        {
            throw new InvalidOperationException(
                $"The case database '{DatabasePath}' has schema version {version}, but this build only understands " +
                $"version {SchemaVersion}. It was probably written by a newer version of the application; refusing to open it.");
        }

        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = SchemaSql + $"\nPRAGMA user_version = {SchemaVersion};";
            command.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    internal static readonly string[] Tables =
    [
        "cases", "input_revisions", "context_snapshots", "documents", "proposals",
        "proposal_line_matches", "confirmations", "evaluations", "assessments",
    ];

    private static string SchemaSql =>
        """
        CREATE TABLE cases (
            case_id        TEXT NOT NULL PRIMARY KEY,
            policy_version TEXT NOT NULL,
            as_of          TEXT NOT NULL
        ) STRICT;

        CREATE TABLE input_revisions (
            case_id     TEXT    NOT NULL REFERENCES cases (case_id),
            revision    INTEGER NOT NULL CHECK (revision >= 1),
            channel     TEXT    NOT NULL,
            actor       TEXT    NOT NULL,
            actor_name  TEXT,
            summary     TEXT    NOT NULL,
            recorded_at TEXT    NOT NULL,
            PRIMARY KEY (case_id, revision)
        ) STRICT;

        CREATE TABLE context_snapshots (
            case_id         TEXT    NOT NULL,
            revision        INTEGER NOT NULL,
            context_version TEXT    NOT NULL,
            payload_json    TEXT    NOT NULL,
            PRIMARY KEY (case_id, revision),
            FOREIGN KEY (case_id, revision) REFERENCES input_revisions (case_id, revision)
        ) STRICT;

        CREATE TABLE documents (
            case_id        TEXT    NOT NULL,
            document_id    TEXT    NOT NULL,
            revision       INTEGER NOT NULL,
            position       INTEGER NOT NULL,
            content_sha256 TEXT    NOT NULL,
            facts_json     TEXT    NOT NULL,
            source_path    TEXT,
            PRIMARY KEY (case_id, document_id, revision),
            UNIQUE (case_id, revision, position),
            FOREIGN KEY (case_id, revision) REFERENCES input_revisions (case_id, revision)
        ) STRICT;

        CREATE TABLE proposals (
            case_id     TEXT    NOT NULL,
            proposal_id TEXT    NOT NULL,
            revision    INTEGER NOT NULL,
            position    INTEGER NOT NULL,
            document_id TEXT    NOT NULL,
            order_id    TEXT    NOT NULL,
            rationale   TEXT,
            PRIMARY KEY (case_id, proposal_id),
            UNIQUE (case_id, revision, position),
            FOREIGN KEY (case_id, revision) REFERENCES input_revisions (case_id, revision)
        ) STRICT;

        CREATE TABLE proposal_line_matches (
            case_id          TEXT    NOT NULL,
            proposal_id      TEXT    NOT NULL,
            position         INTEGER NOT NULL,
            document_line_id TEXT    NOT NULL,
            order_line_id    TEXT    NOT NULL,
            PRIMARY KEY (case_id, proposal_id, position),
            FOREIGN KEY (case_id, proposal_id) REFERENCES proposals (case_id, proposal_id)
        ) STRICT;

        CREATE TABLE confirmations (
            case_id      TEXT    NOT NULL,
            proposal_id  TEXT    NOT NULL,
            revision     INTEGER NOT NULL,
            position     INTEGER NOT NULL,
            confirmed_by TEXT    NOT NULL,
            PRIMARY KEY (case_id, proposal_id),
            UNIQUE (case_id, revision, position),
            FOREIGN KEY (case_id, proposal_id) REFERENCES proposals (case_id, proposal_id),
            FOREIGN KEY (case_id, revision) REFERENCES input_revisions (case_id, revision)
        ) STRICT;

        CREATE TABLE evaluations (
            case_id                 TEXT    NOT NULL,
            document_id             TEXT    NOT NULL,
            revision                INTEGER NOT NULL,
            position                INTEGER NOT NULL,
            import_disposition      TEXT    NOT NULL,
            original_document_id    TEXT,
            existing_assessment_ref TEXT,
            PRIMARY KEY (case_id, document_id, revision),
            UNIQUE (case_id, revision, position),
            FOREIGN KEY (case_id, revision) REFERENCES input_revisions (case_id, revision)
        ) STRICT;

        CREATE TABLE assessments (
            assessment_id   TEXT    NOT NULL PRIMARY KEY,
            case_id         TEXT    NOT NULL,
            document_id     TEXT    NOT NULL,
            revision        INTEGER NOT NULL,
            policy_version  TEXT    NOT NULL,
            context_version TEXT    NOT NULL,
            decision        TEXT    NOT NULL,
            result_json     TEXT    NOT NULL,
            UNIQUE (case_id, document_id, revision),
            FOREIGN KEY (case_id, document_id, revision) REFERENCES evaluations (case_id, document_id, revision)
        ) STRICT;

        """ + string.Concat(Tables.Select(table =>
        $"""
        CREATE TRIGGER {table}_no_update BEFORE UPDATE ON {table}
        BEGIN SELECT RAISE(ABORT, '{table} is append-only: rows cannot be updated'); END;
        CREATE TRIGGER {table}_no_delete BEFORE DELETE ON {table}
        BEGIN SELECT RAISE(ABORT, '{table} is append-only: rows cannot be deleted'); END;

        """));

    private static string? GetNullableString(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static long Scalar(SqliteConnection connection, SqliteTransaction transaction, string sql, string caseId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.Parameters.AddWithValue("$case_id", caseId);
        return (long)command.ExecuteScalar()!;
    }

    private static void Execute(
        SqliteConnection connection, SqliteTransaction transaction, string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        command.ExecuteNonQuery();
    }

    private static List<T> ReadAll<T>(
        SqliteConnection connection, SqliteTransaction transaction, string sql, string caseId, Func<SqliteDataReader, T> map)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.Parameters.AddWithValue("$case_id", caseId);
        using var reader = command.ExecuteReader();
        var rows = new List<T>();
        while (reader.Read())
        {
            rows.Add(map(reader));
        }

        return rows;
    }

    private static T? ReadSingle<T>(
        SqliteConnection connection, SqliteTransaction transaction, string sql, string caseId, Func<SqliteDataReader, T> map)
        where T : class =>
        ReadAll(connection, transaction, sql, caseId, map).SingleOrDefault();
}
