using Swarm.Delivery;
using Swarm.Git;
using Swarm.RunState;
using Swarm.Tools.Tests.Support;

namespace Swarm.Tools.Tests.Delivery;

public class EpicStoreTests
{
    static EpicRecord Record(string id) =>
        new(1, id, "auth", $"epic/{id}-auth", "main", new string('a', 40), new DateTime(2026, 10, 3, 0, 0, 0, DateTimeKind.Utc), EpicStates.Open, null, null, null);

    [Fact]
    public void SaveFindAll()
    {
        using var dir = new TempDir();
        var store = new EpicStore(new StateLayout(dir.Dir));
        store.Save(Record("42"));
        store.Save(Record("7"));
        Assert.Equal("epic/42-auth", store.Get("42").Branch);
        Assert.Null(store.Find("99"));
        Assert.Equal(new[] { "42", "7" }, store.All().Records.Select(r => r.Id).Order(StringComparer.Ordinal));
        Assert.DoesNotContain('\r', File.ReadAllText(store.PathOf("42")));
    }

    [Fact]
    public void Get_Unknown_IsBadInputWithHint()
    {
        using var dir = new TempDir();
        var e = Assert.Throws<ToolException>(() => new EpicStore(new StateLayout(dir.Dir)).Get("42"));
        Assert.Equal(ExitCodes.BadInput, e.ExitCode);
        Assert.Contains("epic open 42", e.ErrorLine);
    }

    [Fact]
    public void UnsafeId_IsUsage()
    {
        using var dir = new TempDir();
        Assert.Equal(ExitCodes.Usage, Assert.Throws<ToolException>(() => new EpicStore(new StateLayout(dir.Dir)).Find("../x")).ExitCode);
    }

    [Fact]
    public void All_StrayFiles_AreReportedPerFileAndDoNotHideTheOthers()
    {
        using var dir = new TempDir();
        var store = new EpicStore(new StateLayout(dir.Dir));
        store.Save(Record("7"));
        store.Save(Record("42"));
        File.WriteAllText(Path.Combine(store.Dir, "notes copy.json"), "{}");
        File.WriteAllText(Path.Combine(store.Dir, "9.json"), "{ not json");

        var listing = store.All();

        Assert.Equal(new[] { "42", "7" }, listing.Records.Select(r => r.Id));
        Assert.Equal(new[] { "9.json", "notes copy.json" }, listing.Unreadable.Select(u => Path.GetFileName(u.Path)));
        Assert.Contains("not a safe name", listing.Unreadable[1].Error);
        Assert.Contains("is not valid", listing.Unreadable[0].Error);
        Assert.All(listing.Unreadable, u => Assert.DoesNotContain('\n', u.Error));
    }

    [Fact]
    public void CorruptFile_IsBadInput()
    {
        using var dir = new TempDir();
        var store = new EpicStore(new StateLayout(dir.Dir));
        Directory.CreateDirectory(store.Dir);
        File.WriteAllText(store.PathOf("42"), "{ not json");
        Assert.Equal(ExitCodes.BadInput, Assert.Throws<ToolException>(() => store.Get("42")).ExitCode);
    }
}
