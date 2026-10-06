using Swarm.Batching;
using static Swarm.Tools.Tests.Support.Tasks;

namespace Swarm.Tools.Tests.Batching;

public class BatchPlannerTests
{
    static IReadOnlyList<TaskUnit> Units(params TaskSpec[] tasks) => TaskUnits.Build(tasks);

    [Fact]
    public void Units_GroupStacksAtFirstMemberPosition()
    {
        var units = Units(T("T1"), T("T2"), T("T3", "T1"), T("T4", "T3"));
        Assert.Equal(new[] { "T1+T3+T4", "T2" }, units.Select(u => string.Join('+', u.Ids)));
    }

    [Fact]
    public void Units_IgnoreDependenciesNotInList() =>
        Assert.Equal(2, Units(T("T3", "T1"), T("T4")).Count);

    [Theory]
    [InlineData(4, false, 8)]
    [InlineData(8, false, 8)]
    [InlineData(8, true, 4)]
    [InlineData(4, true, 2)]
    [InlineData(2, true, 2)]
    public void NextSize_DoublesOnGreenHalvesOnRed(int current, bool red, int expected) =>
        Assert.Equal(expected, BatchPlanner.NextSize(current, red, 2, 8));

    [Fact]
    public void PreBatch_SkipsOverlappingUnits()
    {
        var queue = Units(T("T1"), T("T2"), T("T3"));
        var pick = BatchPlanner.PreBatch(queue, 4, (a, b) => (a.Id, b.Id) is ("T1", "T2") or ("T2", "T1"), prebatch: true);
        Assert.Equal(new[] { "T1", "T3" }, pick.Select(u => u.Id));
    }

    [Fact]
    public void PreBatch_Disabled_TakesInOrder()
    {
        var queue = Units(T("T1"), T("T2"), T("T3"));
        Assert.Equal(3, BatchPlanner.PreBatch(queue, 4, (_, _) => true, prebatch: false).Count);
    }

    [Fact]
    public void PreBatch_CountsUnitSizesAndAlwaysTakesTheHead()
    {
        var queue = Units(T("T1"), T("T2", "T1"), T("T3", "T2"), T("T4"));
        var pick = BatchPlanner.PreBatch(queue, 2, (_, _) => false, prebatch: true);
        Assert.Equal(new[] { "T1" }, pick.Select(u => u.Id));
        Assert.Equal(3, pick[0].Size);
    }

    [Fact]
    public void PreBatch_FillsWithSmallerLaterUnits()
    {
        var queue = Units(T("T1"), T("T2"), T("T3", "T2"), T("T4"));
        var pick = BatchPlanner.PreBatch(queue, 2, (_, _) => false, prebatch: true);
        Assert.Equal(new[] { "T1", "T4" }, pick.Select(u => u.Id));
    }

    [Fact]
    public void Halve_LeftIsSmaller()
    {
        var (left, right) = BatchPlanner.Halve(Units(T("T1"), T("T2"), T("T3")));
        Assert.Equal(new[] { "T1" }, left.Select(u => u.Id));
        Assert.Equal(new[] { "T2", "T3" }, right.Select(u => u.Id));
    }
}
