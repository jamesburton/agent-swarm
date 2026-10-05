using Swarm.Batching;
using Swarm.Delivery;
using Swarm.Git;
using Swarm.RunState;
using Swarm.Squashing;
using Swarm.Tools.Tests.Support;

namespace Swarm.Tools.Tests.Delivery;

public class MergeMessageTests
{
    static readonly EpicRecord Epic = new(1, "42", "auth", "epic/42-auth", "main", new string('0', 40), DateTime.UtcNow, EpicStates.Open, null, null, null);

    // Built by Plan B's own SquashMessage (two tasks, a commit list, Source-Commit and Co-authored-by), so a change to
    // the trailer block breaks the tests that use it.
    static string PlanBMessage()
    {
        var group = new SquashGroup(
            "9933",
            [new LandTask("T1", "task/9933-login", []), new LandTask("T2", "task/9933-more", ["T1"])],
            [new string('1', 40), null],
            [new SourceCommit(new string('2', 40), "Ada", "ada@example.invalid", "9933: Login form"), new SourceCommit(new string('3', 40), "Bob", "bob@example.invalid", "fix form")]);
        return SquashMessage.Build(new SquashConfig(), group, new MessageContext("42-auth", 3, "run-1"));
    }

    [Fact]
    public void Build_ListsTicketsRunsUntrackedAndForeign()
    {
        var commits = new TrailerCommit[]
        {
            new(new string('a', 40), "9933: Login form", ["9933"], ["42-auth"], ["1"], ["run-a"]),
            new(new string('b', 40), "9934: Token refresh", ["9934"], ["42-auth"], ["2"], ["run-a"]),
            new(new string('c', 40), "batch: merge T9 (task/T9)", [], [], [], []),
            new(new string('e', 40), "9935 - Hotfix", ["9935"], ["42-auth"], ["0"], ["squash-20261004-090000-000-42-auth"]),
            new(new string('f', 40), "Older stamp", ["9936"], ["42"], ["1"], ["run-0"]),
            new(new string('d', 40), "Stray", ["9999"], ["7"], ["2"], ["run-b"]),
        };
        const string expected = """
            Merge epic 42-auth (epic/42-auth) into main

            Epic: 42 (batch epic 42-auth)
            Tickets: 9933, 9934, 9935, 9936, 9999
            Runs: 4

            - 9933 (batch 1): Login form
            - 9934 (batch 2): Token refresh
            - 9935 (manual): Hotfix
            - 9936 (batch 1): Older stamp
            - 9999 (batch 2): Stray

            Commits without a Ticket: trailer: 1
            Commits naming another epic: ddddddd (Epic: 7)
            """;
        Assert.Equal(expected.ReplaceLineEndings("\n"), MergeMessage.Build(Epic, "42-auth", "main", commits));
        Assert.Equal(new[] { "9933", "9934", "9935", "9936", "9999" }, MergeMessage.Tickets(commits));
    }

    [Theory]
    [InlineData("9933: Login form", "Login form")]
    [InlineData("9933 - Login form", "Login form")]
    [InlineData("9933", "9933")]
    [InlineData("99330 bigger number", "99330 bigger number")]
    [InlineData("Login form", "Login form")]
    public void Title_DropsALeadingTicketLikeTheSquashLander(string subject, string title) =>
        Assert.Equal(title, MergeMessage.Title(new TrailerCommit(new string('a', 40), subject, ["9933"], [], [], [])));

    [Fact]
    public void Build_NoTrailers_SaysSo()
    {
        var text = MergeMessage.Build(Epic, null, "main", [new TrailerCommit(new string('a', 40), "x", [], [], [], [])]);
        Assert.StartsWith("Merge epic 42-auth (epic/42-auth) into main\n\nEpic: 42\n", text);
        Assert.Contains("Tickets: none (no Ticket: trailers found)", text);
        Assert.DoesNotContain("Runs:", text);
        Assert.DoesNotContain('\r', text);
        Assert.False(text.EndsWith('\n'));
    }

    [Fact]
    public void Read_ParsesTrailersFromGitOldestFirst()
    {
        using var repo = TempRepo.Create();
        repo.Git("checkout", "-q", "-b", "epic/42-auth");
        repo.Commit("Login form\n\nBody text.\n\nTicket: 9933\nepic: 42-auth\nBatch: 1\nswarm-run: run-1", ("a.txt", "a\n"));
        repo.Commit("No trailers", ("b.txt", "b\n"));
        repo.Git("checkout", "-q", "main");
        var commits = TrailerLog.Read(new GitRunner(repo.Root), "refs/heads/main", "refs/heads/epic/42-auth");
        Assert.Equal(2, commits.Count);
        Assert.Equal(("Login form", "9933", "42-auth", "1", "run-1"), (commits[0].Subject, commits[0].Tickets.Single(), commits[0].Epics.Single(), commits[0].Batches.Single(), commits[0].Runs.Single()));
        Assert.Empty(commits[1].Tickets);
        Assert.Equal(repo.Sha("epic/42-auth"), commits[1].Sha);
    }

    [Fact]
    public void Read_ParsesTheTrailerBlockPlanBWrites()
    {
        using var repo = TempRepo.Create();
        repo.Git("checkout", "-q", "-b", "epic/42-auth");
        repo.Commit(PlanBMessage(), ("a.txt", "a\n"));
        repo.Git("checkout", "-q", "main");
        var c = Assert.Single(TrailerLog.Read(new GitRunner(repo.Root), "refs/heads/main", "refs/heads/epic/42-auth"));
        Assert.Equal(("9933: Login form", "9933", "42-auth", "3", "run-1"), (c.Subject, c.Tickets.Single(), c.Epics.Single(), c.Batches.Single(), c.Runs.Single()));
        var text = MergeMessage.Build(Epic, "42-auth", "main", [c]);
        Assert.Contains("\n- 9933 (batch 3): Login form", text);
        Assert.DoesNotContain("another epic", text);
    }

    [Fact]
    public void Parse_TakesTheExactTrailerBlockPlanBWrites()
    {
        // The trailer paragraph SquashMessage writes, verbatim, as %(trailers:only,unfold) prints it: the last paragraph
        // of the message (Ticket, Epic, Batch, Swarm-Run, Task, Source-Commit, Task, Co-authored-by).
        var message = PlanBMessage();
        var block = message[(message.TrimEnd('\n').LastIndexOf("\n\n", StringComparison.Ordinal) + 2)..];
        Assert.StartsWith("Ticket: 9933\nEpic: 42-auth\nBatch: 3\nSwarm-Run: run-1\nTask: T1\nSource-Commit: ", block);
        Assert.Contains("\nCo-authored-by: Bob <bob@example.invalid>\n", block);
        var sha = new string('a', 40);
        var log = $"{sha}\u001f9933: Login form\u001f{block}\u001e\n{new string('b', 40)}\u001fNo trailers\u001f\u001e\n";

        var commits = TrailerLog.Parse(log);

        Assert.Equal(2, commits.Count);
        var c = commits[0];
        Assert.Equal((sha, "9933: Login form"), (c.Sha, c.Subject));
        Assert.Equal(new[] { "9933" }, c.Tickets);
        Assert.Equal(new[] { "42-auth" }, c.Epics);
        Assert.Equal(new[] { "3" }, c.Batches);
        Assert.Equal(new[] { "run-1" }, c.Runs);
        Assert.Equal(("No trailers", 0, 0, 0, 0), (commits[1].Subject, commits[1].Tickets.Count, commits[1].Epics.Count, commits[1].Batches.Count, commits[1].Runs.Count));
    }
}
