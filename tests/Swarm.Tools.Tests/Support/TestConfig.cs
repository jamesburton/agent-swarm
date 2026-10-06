using System.Text.Json;
using Swarm.RunState;

namespace Swarm.Tools.Tests.Support;

public static class TestConfig
{
    public static SwarmConfig For(TempRepo repo, params string[] fakeSuiteArgs) => new()
    {
        Slots = 1,
        ExpirySec = 6,
        HeartbeatSec = 1,
        PollMs = 50,
        MaxWaitSec = 60,
        Epic = "E1",
        TestCommand = FakeSuite.Command(fakeSuiteArgs),
        StateDir = repo.StateDir,
        WorktreeRoot = repo.WorktreeRoot,
    };

    public static string Write(TempRepo repo, SwarmConfig config)
    {
        var path = Path.Combine(repo.Sandbox, "batch.json");
        File.WriteAllText(path, JsonSerializer.Serialize(config, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        return path;
    }
}
