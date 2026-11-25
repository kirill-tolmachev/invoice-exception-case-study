using System.Text;
using InvoiceExceptions.Application.Storage;
using InvoiceExceptions.Domain.Model;
using InvoiceExceptions.Infrastructure.Storage;
using Microsoft.Data.Sqlite;

namespace InvoiceExceptions.Tests.Storage;

public sealed class SqliteCaseStoreTests : IDisposable
{
    private static readonly string[] Tables =
    [
        "cases", "input_revisions", "context_snapshots", "documents", "proposals",
        "proposal_line_matches", "confirmations", "evaluations", "assessments",
    ];

    // Non-ASCII, escapes, insignificant whitespace and key order that a JSON round trip would normalise away.
    private const string ContextJson = "{ \"tolerance\" : 0.02,\n  \"supplier\": \"Müller & Söhne GmbH — Zürich\", \"a\":1 }";
    private const string FactsJson = "{\"document_id\":\"INV-1\",\"excerpt\":\"Prix unitaire: 12,50 € \\u00e9 — 数量 3\",\n\t\"emoji\":\"\U0001F9FE\"}";
    private const string ResultJson = "{\"decision\":\"needs_review\",\"questions\":[\"Quantité reçue ?\"],\"x\": null}";

    private readonly string _directory;

    public SqliteCaseStoreTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "sqlite-case-store-tests", Guid.NewGuid().ToString("N"));
    }

    public void Dispose()
    {
        // Fails if any connection still holds the database file open.
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private string DatabasePath => Path.Combine(_directory, SqliteCaseStore.DefaultFileName);

    private SqliteCaseStore NewStore() => SqliteCaseStore.InDirectory(_directory);

    [Fact]
    public void InDirectory_creates_directory_and_database_and_reopening_is_idempotent()
    {
        Assert.False(Directory.Exists(_directory));

        var store = NewStore();

        Assert.True(File.Exists(DatabasePath));
        Assert.Equal(DatabasePath, store.DatabasePath);
        Assert.Empty(store.ListCaseIds());

        store.Append(FullCommit("case-1"));
        var reopened = NewStore();
        var again = new SqliteCaseStore(DatabasePath);
        Assert.Equal(["case-1"], reopened.ListCaseIds());
        Assert.Equal(["case-1"], again.ListCaseIds());
        Assert.Equal(1, UserVersion());
    }

    [Fact]
    public void Load_returns_null_for_unknown_case()
    {
        Assert.Null(NewStore().Load("nope"));
    }

    [Fact]
    public void Round_trips_every_record_type_and_field()
    {
        var store = NewStore();
        var commit = FullCommit("case-1");

        store.Append(commit);
        var loaded = store.Load("case-1");

        AssertCase(Expected(commit), loaded);
    }

    [Fact]
    public void Json_payloads_are_stored_verbatim_byte_for_byte()
    {
        NewStore().Append(FullCommit("case-1"));

        using var connection = RawConnection();
        Assert.Equal(Encoding.UTF8.GetBytes(ContextJson), Blob(connection, "SELECT CAST(payload_json AS BLOB) FROM context_snapshots"));
        Assert.Equal(Encoding.UTF8.GetBytes(FactsJson), Blob(connection, "SELECT CAST(facts_json AS BLOB) FROM documents WHERE document_id = 'INV-1'"));
        Assert.Equal(Encoding.UTF8.GetBytes(ResultJson), Blob(connection, "SELECT CAST(result_json AS BLOB) FROM assessments WHERE assessment_id = 'A-case-1-INV-1-r1'"));
    }

    [Fact]
    public void RecordedAt_round_trips_the_exact_instant_and_is_stored_as_utc_text()
    {
        var recordedAt = new DateTimeOffset(2026, 11, 25, 23, 59, 59, TimeSpan.FromHours(-5)).AddTicks(1234567);
        var store = NewStore();
        store.Append(FullCommit("case-1") with { Revision = Revision(1, recordedAt) });

        var loaded = store.Load("case-1")!.Revisions.Single().RecordedAt;

        Assert.Equal(recordedAt.UtcTicks, loaded.UtcTicks);
        using var connection = RawConnection();
        Assert.Equal("2026-11-26T04:59:59.1234567Z", Text(connection, "SELECT recorded_at FROM input_revisions"));
    }

    [Fact]
    public void Data_survives_a_new_store_instance_on_the_same_path()
    {
        var commit1 = FullCommit("case-1");
        var commit2 = SecondCommit("case-1");
        NewStore().Append(commit1);
        new SqliteCaseStore(DatabasePath).Append(commit2);

        var loaded = new SqliteCaseStore(DatabasePath).Load("case-1");

        AssertCase(Expected(commit1, commit2), loaded);
    }

    [Fact]
    public void Revisions_accumulate_and_earlier_conclusions_stay_unchanged()
    {
        var store = NewStore();
        var commit1 = FullCommit("case-1");
        store.Append(commit1);
        var before = store.Load("case-1")!;

        var commit2 = SecondCommit("case-1");
        var commit3 = Commit("case-1", 3) with
        {
            Evaluations =
            [
                new EvaluationRecord(3, "INV-1", "new", null, null,
                    new AssessmentRecord("A-case-1-INV-1-r3", "core-v1", "ctx-2", "clear", "{\"decision\":\"clear\"}")),
            ],
        };
        store.Append(commit2);
        store.Append(commit3);

        var after = store.Load("case-1")!;
        AssertCase(Expected(commit1, commit2, commit3), after);
        Assert.Equal([1, 2, 3], after.Revisions.Select(r => r.Revision));
        Assert.Equal(before.Evaluations, after.Evaluations.Where(e => e.Revision == 1));
        Assert.Equal(
            ["A-case-1-INV-1-r1", "A-case-1-INV-3-r1", "A-case-1-INV-1-r2", "A-case-1-INV-1-r3"],
            after.Evaluations.Where(e => e.Assessment is not null).Select(e => e.Assessment!.AssessmentId));
        Assert.Equal(["needs_review", "needs_information", "clear"],
            after.Evaluations.Where(e => e.DocumentId == "INV-1").Select(e => e.Assessment!.Decision));
    }

    [Fact]
    public void A_second_version_of_a_documents_facts_is_stored_next_to_the_first()
    {
        var store = NewStore();
        store.Append(FullCommit("case-1"));
        store.Append(SecondCommit("case-1"));

        var versions = store.Load("case-1")!.Documents.Where(d => d.DocumentId == "INV-1").ToList();

        Assert.Equal(2, versions.Count);
        Assert.Equal((1, FactsJson), (versions[0].Revision, versions[0].FactsJson));
        Assert.Equal((2, "{\"document_id\":\"INV-1\",\"corrected\":true}"), (versions[1].Revision, versions[1].FactsJson));
        Assert.NotEqual(versions[0].ContentSha256, versions[1].ContentSha256);
    }

    public static TheoryData<string> BrokenSecondCommits => new()
    {
        "confirmation-of-unknown-proposal",
        "duplicate-proposal-id-in-commit",
        "proposal-id-reused-from-earlier-revision",
        "proposal-confirmed-twice",
        "duplicate-assessment-id",
        "duplicate-document-in-revision",
    };

    [Theory]
    [MemberData(nameof(BrokenSecondCommits))]
    public void A_commit_that_fails_part_way_leaves_no_trace(string scenario)
    {
        var store = NewStore();
        var commit1 = FullCommit("case-1");
        store.Append(commit1);
        var valid = SecondCommit("case-1");
        var broken = scenario switch
        {
            "confirmation-of-unknown-proposal" => valid with { Confirmations = [new ConfirmationRecord(2, "P-missing", "jane")] },
            "duplicate-proposal-id-in-commit" => valid with { Proposals = [valid.Proposals[0], valid.Proposals[0]] },
            "proposal-id-reused-from-earlier-revision" => valid with { Proposals = [valid.Proposals[0], commit1.Proposals[0] with { Revision = 2 }] },
            "proposal-confirmed-twice" => valid with { Confirmations = [new ConfirmationRecord(2, "P-1", "someone else")] },
            "duplicate-assessment-id" => valid with
            {
                Evaluations = [valid.Evaluations[0], valid.Evaluations[1] with { Assessment = valid.Evaluations[0].Assessment }],
            },
            "duplicate-document-in-revision" => valid with { Documents = [valid.Documents[0], valid.Documents[0]] },
            _ => throw new ArgumentOutOfRangeException(nameof(scenario)),
        };

        Assert.Throws<SqliteException>(() => store.Append(broken));

        AssertCase(Expected(commit1), store.Load("case-1"));
        using (var connection = RawConnection())
        {
            foreach (var table in Tables)
            {
                Assert.Equal(0L, Scalar(connection, $"SELECT COUNT(*) FROM {table} WHERE {RevisionColumn(table)} >= 2"));
            }
        }

        // Revision 2 is still free.
        store.Append(valid);
        AssertCase(Expected(commit1, valid), store.Load("case-1"));
    }

    [Fact]
    public void A_new_case_whose_commit_fails_part_way_does_not_exist_afterwards()
    {
        var store = NewStore();
        var broken = FullCommit("case-1") with { Confirmations = [new ConfirmationRecord(1, "P-missing", "jane")] };

        Assert.Throws<SqliteException>(() => store.Append(broken));

        Assert.Empty(store.ListCaseIds());
        Assert.Null(store.Load("case-1"));
        store.Append(FullCommit("case-1"));
        Assert.Equal(["case-1"], store.ListCaseIds());
    }

    [Theory]
    [InlineData(3, "expected revision 2, got 3")]
    [InlineData(1, "expected revision 2, got 1")]
    [InlineData(0, "expected revision 2, got 0")]
    public void A_revision_that_is_not_the_successor_is_a_conflict(int revision, string expectedMessage)
    {
        var store = NewStore();
        store.Append(FullCommit("case-1"));

        var ex = Assert.Throws<RevisionConflictException>(() => store.Append(Commit("case-1", revision)));

        Assert.Contains("'case-1'", ex.Message);
        Assert.Contains("is at revision 1", ex.Message);
        Assert.Contains(expectedMessage, ex.Message);
        Assert.Single(store.Load("case-1")!.Revisions);
    }

    [Fact]
    public void Repeating_the_latest_revision_after_several_is_a_conflict()
    {
        var store = NewStore();
        store.Append(FullCommit("case-1"));
        store.Append(SecondCommit("case-1"));

        var ex = Assert.Throws<RevisionConflictException>(() => store.Append(Commit("case-1", 2)));

        Assert.Contains("expected revision 3, got 2", ex.Message);
    }

    [Fact]
    public void Creating_an_existing_case_again_is_a_conflict()
    {
        var store = NewStore();
        var commit = FullCommit("case-1");
        store.Append(commit);

        var ex = Assert.Throws<RevisionConflictException>(() => store.Append(commit));

        Assert.Contains("'case-1' already exists", ex.Message);
        AssertCase(Expected(commit), store.Load("case-1"));
    }

    [Fact]
    public void Creating_a_case_at_a_revision_other_than_1_is_a_conflict()
    {
        var store = NewStore();
        var commit = Commit("case-1", 2) with { NewCase = Header("case-1") };

        var ex = Assert.Throws<RevisionConflictException>(() => store.Append(commit));

        Assert.Contains("'case-1'", ex.Message);
        Assert.Contains("must be 1", ex.Message);
        Assert.Empty(store.ListCaseIds());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void Appending_to_an_unknown_case_is_a_conflict(int revision)
    {
        var store = NewStore();

        var ex = Assert.Throws<RevisionConflictException>(() => store.Append(Commit("ghost", revision)));

        Assert.Contains("'ghost' does not exist", ex.Message);
        Assert.Contains($"got revision {revision}", ex.Message);
        Assert.Empty(store.ListCaseIds());
    }

    [Fact]
    public void Child_records_with_a_different_revision_are_rejected()
    {
        var store = NewStore();
        var commit = FullCommit("case-1");

        Assert.Throws<ArgumentException>(() => store.Append(commit with { Context = commit.Context! with { Revision = 2 } }));
        Assert.Throws<ArgumentException>(() => store.Append(commit with { Documents = [commit.Documents[0] with { Revision = 0 }] }));
        Assert.Throws<ArgumentException>(() => store.Append(commit with { Proposals = [commit.Proposals[0] with { Revision = 2 }] }));
        Assert.Throws<ArgumentException>(() => store.Append(commit with { Confirmations = [commit.Confirmations[0] with { Revision = 2 }] }));
        Assert.Throws<ArgumentException>(() => store.Append(commit with { Evaluations = [commit.Evaluations[0] with { Revision = 2 }] }));
        Assert.Throws<ArgumentException>(() => store.Append(commit with { NewCase = Header("other") }));

        Assert.Empty(store.ListCaseIds());
    }

    [Theory]
    [MemberData(nameof(TableNames))]
    public void Every_table_rejects_update_and_delete(string table)
    {
        var store = NewStore();
        store.Append(FullCommit("case-1"));

        using var connection = RawConnection();
        Assert.True(Scalar(connection, $"SELECT COUNT(*) FROM {table}") > 0, $"{table} should have rows for the trigger to fire on");

        var update = Assert.Throws<SqliteException>(() => Exec(connection, $"UPDATE {table} SET case_id = case_id"));
        Assert.Contains($"{table} is append-only", update.Message);
        var delete = Assert.Throws<SqliteException>(() => Exec(connection, $"DELETE FROM {table}"));
        Assert.Contains($"{table} is append-only", delete.Message);

        AssertCase(Expected(FullCommit("case-1")), store.Load("case-1"));
    }

    public static TheoryData<string> TableNames => new(Tables);

    [Fact]
    public void Cases_do_not_see_each_others_data()
    {
        var store = NewStore();
        var a1 = FullCommit("case-a");
        var b1 = FullCommit("case-b");
        var a2 = SecondCommit("case-a");
        store.Append(a1);
        store.Append(b1);
        store.Append(a2);

        AssertCase(Expected(a1, a2), store.Load("case-a"));
        AssertCase(Expected(b1), store.Load("case-b"));
        // Revision numbering is per case.
        store.Append(SecondCommit("case-b"));
        Assert.Equal(2, store.Load("case-b")!.Revisions.Count);
    }

    [Fact]
    public void ListCaseIds_is_in_ordinal_order()
    {
        var store = NewStore();
        string[] ids = ["b", "a-2", "B", "ä", "a-10", "A", "\uFF21", "\U0001F600"];
        foreach (var id in ids)
        {
            store.Append(Commit(id, 1) with { NewCase = Header(id) });
        }

        Assert.Equal(ids.Order(StringComparer.Ordinal), store.ListCaseIds());
    }

    [Fact]
    public void Opening_a_database_with_a_newer_schema_version_fails()
    {
        NewStore();
        using (var connection = RawConnection())
        {
            Exec(connection, "PRAGMA user_version = 2");
        }

        var ex = Assert.Throws<InvalidOperationException>(NewStore);

        Assert.Contains("schema version 2", ex.Message);
        Assert.Contains("version 1", ex.Message);
    }

    [Fact]
    public void Concurrent_appends_of_the_same_revision_admit_exactly_one()
    {
        NewStore().Append(FullCommit("case-1"));

        var outcomes = Enumerable.Range(0, 8)
            .AsParallel()
            .WithDegreeOfParallelism(8)
            .Select(i =>
            {
                try
                {
                    new SqliteCaseStore(DatabasePath).Append(Commit("case-1", 2) with { Revision = Revision(2) with { Summary = $"writer {i}" } });
                    return true;
                }
                catch (RevisionConflictException)
                {
                    return false;
                }
            })
            .ToList();

        Assert.Equal(1, outcomes.Count(ok => ok));
        Assert.Equal([1, 2], NewStore().Load("case-1")!.Revisions.Select(r => r.Revision));
    }

    // ---- fixtures ----

    private static CaseHeader Header(string caseId) => new(caseId, "core-v1", "2026-11-01");

    private static RevisionRecord Revision(int revision, DateTimeOffset? at = null) =>
        new(revision, "cli", "agent", null, $"revision {revision}", at ?? new DateTimeOffset(2026, 11, 25, 9, 0, revision, TimeSpan.Zero));

    private static CaseCommit Commit(string caseId, int revision) =>
        new(caseId, null, Revision(revision), null, [], [], [], []);

    /// <summary>Revision 1 of a case with every record type and every optional field both set and null.</summary>
    private static CaseCommit FullCommit(string caseId) => new(
        caseId,
        Header(caseId),
        new RevisionRecord(1, "mcp", "agent", "Agent Smith — Ünïcødé", "Import of 3 invoices",
            new DateTimeOffset(2026, 11, 25, 10, 15, 30, TimeSpan.FromHours(2)).AddTicks(42)),
        new ContextRecord(1, "ctx-1", ContextJson),
        [
            new DocumentRecord(1, "INV-1", "sha-inv-1-v1", FactsJson, @"C:\in\INV-1.pdf"),
            new DocumentRecord(1, "INV-2", "sha-inv-2", "{\"document_id\":\"INV-2\"}", null),
            new DocumentRecord(1, "INV-3", "sha-inv-3", "{\"document_id\":\"INV-3\"}", "/data/ä/INV-3.pdf"),
        ],
        [
            new ProposalRecord(1, "P-1", "INV-1", "PO-7",
                [new LineMatch("L3", "PO-7-1"), new LineMatch("L1", "PO-7-3"), new LineMatch("L2", "PO-7-2")],
                "Same SKU, quantité différente"),
            new ProposalRecord(1, "P-0", "INV-3", "PO-8", [], null),
        ],
        [new ConfirmationRecord(1, "P-1", "jane.doe")],
        [
            new EvaluationRecord(1, "INV-1", "new", null, null,
                new AssessmentRecord($"A-{caseId}-INV-1-r1", "core-v1", "ctx-1", "needs_review", ResultJson)),
            new EvaluationRecord(1, "INV-2", "duplicate", "INV-1", $"A-{caseId}-INV-1-r1", null),
            new EvaluationRecord(1, "INV-3", "conflict", "INV-1", null,
                new AssessmentRecord($"A-{caseId}-INV-3-r1", "core-v1", "ctx-1", "blocked", "{}")),
        ]);

    /// <summary>Revision 2: a new context, a corrected INV-1, a new proposal confirmed in the same revision.</summary>
    private static CaseCommit SecondCommit(string caseId) => new(
        caseId,
        null,
        new RevisionRecord(2, "web", "human", "jane.doe", "Corrected quantities", new DateTimeOffset(2026, 11, 26, 8, 0, 0, TimeSpan.Zero)),
        new ContextRecord(2, "ctx-2", "{\"tolerance\":0.05}"),
        [new DocumentRecord(2, "INV-1", "sha-inv-1-v2", "{\"document_id\":\"INV-1\",\"corrected\":true}", null)],
        [new ProposalRecord(2, "P-2", "INV-1", "PO-9", [new LineMatch("L1", "PO-9-1")], "second try")],
        [new ConfirmationRecord(2, "P-2", "jane.doe")],
        [
            new EvaluationRecord(2, "INV-1", "new", null, null,
                new AssessmentRecord($"A-{caseId}-INV-1-r2", "core-v1", "ctx-2", "needs_information", "{\"r\":2}")),
            new EvaluationRecord(2, "INV-2", "duplicate", "INV-1", $"A-{caseId}-INV-1-r2", null),
        ]);

    private static CaseRecord Expected(params CaseCommit[] commits) => new(
        commits[0].NewCase!,
        commits.Select(c => c.Revision).ToList(),
        commits.Where(c => c.Context is not null).Select(c => c.Context!).ToList(),
        commits.SelectMany(c => c.Documents).ToList(),
        commits.SelectMany(c => c.Proposals).ToList(),
        commits.SelectMany(c => c.Confirmations).ToList(),
        commits.SelectMany(c => c.Evaluations).ToList());

    // Records holding lists compare those lists by reference, so compare them element by element.
    private static void AssertCase(CaseRecord expected, CaseRecord? actual)
    {
        Assert.NotNull(actual);
        Assert.Equal(expected.Header, actual.Header);
        Assert.Equal(expected.Revisions, actual.Revisions);
        Assert.Equal(expected.Contexts, actual.Contexts);
        Assert.Equal(expected.Documents, actual.Documents);
        Assert.Equal(expected.Confirmations, actual.Confirmations);
        Assert.Equal(expected.Evaluations, actual.Evaluations);
        Assert.Equal(expected.Proposals.Count, actual.Proposals.Count);
        foreach (var (e, a) in expected.Proposals.Zip(actual.Proposals))
        {
            Assert.Equal(e with { LineMatches = [] }, a with { LineMatches = [] }, ProposalWithoutMatches.Instance);
            Assert.Equal(e.LineMatches, a.LineMatches);
        }
    }

    private sealed class ProposalWithoutMatches : IEqualityComparer<ProposalRecord>
    {
        public static readonly ProposalWithoutMatches Instance = new();

        public bool Equals(ProposalRecord? x, ProposalRecord? y) =>
            x is not null && y is not null
            && (x.Revision, x.ProposalId, x.DocumentId, x.OrderId, x.Rationale) == (y.Revision, y.ProposalId, y.DocumentId, y.OrderId, y.Rationale);

        public int GetHashCode(ProposalRecord obj) => obj.ProposalId.GetHashCode(StringComparison.Ordinal);
    }

    private static string RevisionColumn(string table) => table switch
    {
        "cases" => "0",
        "proposal_line_matches" => "(SELECT p.revision FROM proposals p WHERE p.case_id = proposal_line_matches.case_id AND p.proposal_id = proposal_line_matches.proposal_id)",
        _ => "revision",
    };

    // ---- raw SQLite access ----

    private SqliteConnection RawConnection()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
        }.ToString());
        connection.Open();
        return connection;
    }

    private int UserVersion()
    {
        using var connection = RawConnection();
        return (int)Scalar(connection, "PRAGMA user_version");
    }

    private static void Exec(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static long Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (long)command.ExecuteScalar()!;
    }

    private static string Text(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (string)command.ExecuteScalar()!;
    }

    private static byte[] Blob(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (byte[])command.ExecuteScalar()!;
    }
}
