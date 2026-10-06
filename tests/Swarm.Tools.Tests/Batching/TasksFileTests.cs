using Swarm.Batching;
using Swarm.Git;

namespace Swarm.Tools.Tests.Batching;

public class TasksFileTests
{
    [Fact]
    public void ParsesTasksAndIgnoresTouches()
    {
        var tasks = TasksFile.Parse("""[ { "id": "T1", "branch": "task/T1", "touches": ["bogus.cs"] }, { "id": "T2", "branch": "task/T2", "dependsOn": ["T1"], "title": "x" } ]""", "tasks.json");
        Assert.Equal(new[] { "T1", "T2" }, tasks.Select(t => t.Id));
        Assert.Equal("refs/heads/task/T2", tasks[1].BranchRef);
        Assert.Equal(new[] { "T1" }, tasks[1].DependsOn);
        Assert.Empty(tasks[0].DependsOn);
    }

    [Fact]
    public void CrlfAndTrailingCommas_Accepted() =>
        Assert.Single(TasksFile.Parse("[\r\n  // one task\r\n  { \"id\": \"T1\", \"branch\": \"task/T1\", },\r\n]\r\n", "tasks.json"));

    [Fact]
    public void EmptyArray_IsValid() => Assert.Empty(TasksFile.Parse("[]", "tasks.json"));

    [Theory]
    [InlineData("""[ { "id": """, "invalid JSON")]
    [InlineData("""{ "id": "T1" }""", "invalid JSON")]
    [InlineData("""[ null ]""", "task #1: must be an object")]
    [InlineData("""[ { "branch": "task/T1" } ]""", "task #1: missing 'id'")]
    [InlineData("""[ { "id": "T1" } ]""", "task 'T1': missing 'branch'")]
    [InlineData("""[ { "id": "T 1", "branch": "b" } ]""", "task id 'T 1' is not a safe name")]
    [InlineData("""[ { "id": "T1", "branch": "a" }, { "id": "t1", "branch": "b" } ]""", "duplicate task id 't1'")]
    [InlineData("""[ { "id": "T1", "branch": "-x" } ]""", "task 'T1': branch '-x' is not a valid branch name")]
    [InlineData("""[ { "id": "T1", "branch": "a..b" } ]""", "task 'T1': branch 'a..b' is not a valid branch name")]
    [InlineData("""[ { "id": "T1", "branch": "a", "dependsOn": ["T2"] }, { "id": "T2", "branch": "b" } ]""", "task 'T1': dependsOn 'T2' must name an earlier task")]
    [InlineData("""[ { "id": "T1", "brnach": "a" } ]""", "'brnach'")]
    public void InvalidFile_IsBadInput(string json, string expected)
    {
        var e = Assert.Throws<ToolException>(() => TasksFile.Parse(json, "tasks.json"));
        Assert.Equal(ExitCodes.BadInput, e.ExitCode);
        Assert.Contains(expected, e.Message);
        Assert.StartsWith("tasks.json: ", e.Message);
    }

    [Fact]
    public void MissingFile_IsBadInput() =>
        Assert.Equal(ExitCodes.BadInput, Assert.Throws<ToolException>(() => TasksFile.Load(Path.Combine(Path.GetTempPath(), "nope-" + Guid.NewGuid().ToString("N") + ".json"))).ExitCode);
}
