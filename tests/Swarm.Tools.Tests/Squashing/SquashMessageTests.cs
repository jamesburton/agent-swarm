using Swarm.Batching;
using Swarm.RunState;
using Swarm.Squashing;

namespace Swarm.Tools.Tests.Squashing;

public class SquashMessageTests
{
    static readonly MessageContext Ctx = new("E1", 3, "run-1");

    static SourceCommit C(string subject, string name = "Ada", string email = "ada@example.invalid") =>
        new("0123456789abcdef0123456789abcdef01234567", name, email, subject);

    static SquashGroup G(string ticket, params SourceCommit[] commits) =>
        new(ticket, [new LandTask("T1", "task/9933-parser", [])], ["abc123"], commits);

    [Fact]
    public void Build_MultiCommit_ListsCommitsAndStampsTrailers() =>
        Assert.Equal(
            "9933: add parser\n\nSquashed commits:\n- 9933: add parser\n- fix parser\n\nTicket: 9933\nEpic: E1\nBatch: 3\nSwarm-Run: run-1\nTask: T1\nSource-Commit: abc123\nCo-authored-by: Bob <bob@example.invalid>\n",
            SquashMessage.Build(new SquashConfig(), G("9933", C("9933: add parser"), C("fix parser", "Bob", "bob@example.invalid")), Ctx));

    [Fact]
    public void Build_SingleCommit_HasNoList() =>
        Assert.Equal(
            "9933: add parser\n\nTicket: 9933\nEpic: E1\nBatch: 3\nSwarm-Run: run-1\nTask: T1\nSource-Commit: abc123\n",
            SquashMessage.Build(new SquashConfig(), G("9933", C("add parser")), Ctx));

    [Fact]
    public void Build_StackGroup_StampsEveryTaskAndSkipsMissingSources()
    {
        var group = new SquashGroup("9933", [new LandTask("T1", "task/9933-a", []), new LandTask("T2", "task/9933-b", ["T1"])], [null, "def456"], [C("add a")]);
        Assert.EndsWith("Task: T1\nTask: T2\nSource-Commit: def456\n", SquashMessage.Build(new SquashConfig(), group, Ctx));
    }

    [Theory]
    [InlineData("9933: add parser", "add parser")]
    [InlineData("9933 - add parser", "add parser")]
    [InlineData("99330 bigger number", "99330 bigger number")]
    [InlineData("add parser", "add parser")]
    public void Title_StripsLeadingTicket(string subject, string expected) =>
        Assert.Equal(expected, SquashMessage.Title(G("9933", C(subject))));

    [Fact]
    public void Title_WithoutCommits_IsBranchName() => Assert.Equal("task/9933-parser", SquashMessage.Title(G("9933")));

    [Fact]
    public void Subject_ExpandsEveryPlaceholderInOnePass()
    {
        var config = new SquashConfig { SubjectTemplate = "{epic}/{batch}/{runId} {taskIds} {taskId} {branch} [{ticket}] {title}" };
        var group = new SquashGroup("9933", [new LandTask("T1", "task/9933-a", []), new LandTask("T2", "task/9933-b", ["T1"])], [null, null], [C("use {epic} literally\nsecond line")]);
        Assert.Equal("E1/3/run-1 T1+T2 T1 task/9933-a [9933] use {epic} literally second line", SquashMessage.Subject(config, group, Ctx));
    }

    [Fact]
    public void CoAuthors_AreDistinctCaseInsensitive_WithoutAuthorAndTool()
    {
        var group = G("9933", C("a"), C("b", "Bob", "bob@example.invalid"), C("c", "Bob", "BOB@example.invalid"), C("d", "swarm-batch", "swarm-batch@example.invalid"), C("e"));
        Assert.Equal("Ada", SquashMessage.Author(new SquashConfig(), group)!.AuthorName);
        Assert.Equal(new[] { "Bob <bob@example.invalid>" }, SquashMessage.CoAuthors(new SquashConfig(), group));
    }

    [Fact]
    public void ToolAuthorMode_CreditsEveryAuthor()
    {
        var config = new SquashConfig { Author = SquashAuthorModes.Tool };
        var group = G("9933", C("a"), C("b", "Bob", "bob@example.invalid"));
        Assert.Null(SquashMessage.Author(config, group));
        Assert.Equal(new[] { "Ada <ada@example.invalid>", "Bob <bob@example.invalid>" }, SquashMessage.CoAuthors(config, group));
    }
}
