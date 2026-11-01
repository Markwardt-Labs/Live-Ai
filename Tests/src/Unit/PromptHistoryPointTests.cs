namespace Markwardt.LiveAi.Tests.Unit;

public sealed class PromptHistoryPointTests
{
    [Fact]
    public void Points_AreOrderedByPositionAndThenByIndex()
    {
        PromptHistoryPoint early = new(10, 2);
        PromptHistoryPoint sameRecord = new(10, 3);
        PromptHistoryPoint later = new(20);

        Assert.True(early.CompareTo(sameRecord) < 0);
        Assert.True(later.CompareTo(sameRecord) > 0);
        Assert.Equal(0, early.CompareTo(new PromptHistoryPoint(10, 2)));
        Assert.Equal(new PromptHistoryPoint(10, 2), early);
    }

    [Fact]
    public void AnEntry_HasThePointItWasGiven()
    {
        PromptHistoryEntry entry = new() { Event = new PromptInputEvent { Text = "x" }, Point = new PromptHistoryPoint(5, 1) };

        Assert.Equal(new PromptHistoryPoint(5, 1), entry.Point);
    }

    [Fact]
    public async Task TheSessionTakesAPointAndTheExtensionsTakeAnEntry()
    {
        FakePromptSession session = new FakePromptSession(new PromptProvider(new ClaudeEngine())).Reply("a").Reply("b");
        await session.Request("first");
        await session.Request("second");
        IReadOnlyList<PromptHistoryEntry> history = await session.GetHistory().Take(100).ToListAsync();
        PromptHistoryEntry first = history.Single(entry => entry.Text == "first");

        FakePromptSession byPoint = (FakePromptSession)await session.Fork(first.Point);
        FakePromptSession byEntry = (FakePromptSession)await session.Fork(first);
        IReadOnlyList<PromptHistoryEntry> newer = await session.GetHistory().NewerThan(first.Point).ToListAsync();

        Assert.Equal((await byPoint.GetHistory().Take(100).ToListAsync()).Select(entry => entry.Text), (await byEntry.GetHistory().Take(100).ToListAsync()).Select(entry => entry.Text));
        Assert.Equal(await session.GetHistory().TakeWhile(entry => entry.Point.CompareTo(first.Point) > 0).ToListAsync(), newer);
        Assert.DoesNotContain(newer, entry => entry.Text == "first");
        await Assert.ThrowsAsync<ArgumentNullException>(() => session.Rewind((PromptHistoryEntry)null!));
    }
}
