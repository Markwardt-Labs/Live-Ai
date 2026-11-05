namespace Markwardt.LiveAi.Tests.Unit;

public sealed class TextDiffTests
{
    private readonly TextDiff diff = new();

    private string[] Lines(string text)
        => text.Length == 0 ? [] : text.Split(',');

    [Theory]
    [InlineData("a,b,c", "a,b,c", "")]
    [InlineData("", "", "")]
    [InlineData("", "x", "--- a\n+++ b\n@@ -0,0 +1,1 @@\n+x\n")]
    [InlineData("x", "", "--- a\n+++ b\n@@ -1,1 +0,0 @@\n-x\n")]
    [InlineData("a,b,c", "a,c", "--- a\n+++ b\n@@ -1,3 +1,2 @@\n a\n-b\n c\n")]
    [InlineData("a,c", "a,b,c", "--- a\n+++ b\n@@ -1,2 +1,3 @@\n a\n+b\n c\n")]
    [InlineData("a,b", "x,y", "--- a\n+++ b\n@@ -1,2 +1,2 @@\n-a\n-b\n+x\n+y\n")]
    public void Unified_ShowsTheEditsThatTurnTheFirstIntoTheSecond(string first, string second, string expected)
        => Assert.Equal(expected, diff.Unified("a", "b", Lines(first), Lines(second), 3));

    [Fact]
    public void Unified_WithoutContext_ShowsOnlyTheChangesAndSplitsFarApartOnes()
    {
        string text = diff.Unified("a", "b", Lines("1,2,3,4,5,6,7"), Lines("1,X,3,4,5,Y,7"), 0);

        Assert.Equal("--- a\n+++ b\n@@ -2,1 +2,1 @@\n-2\n+X\n@@ -6,1 +6,1 @@\n-6\n+Y\n", text);
    }

    [Fact]
    public void Unified_MergesChangesWhoseContextTouches()
    {
        string text = diff.Unified("a", "b", Lines("1,2,3,4,5,6,7,8"), Lines("1,X,3,4,5,Y,7,8"), 2);

        Assert.Equal(1, text.Split("@@ -").Length - 1);
    }

    [Fact]
    public void Unified_AppliedBackwardsGivesTheFirstTextAgain()
    {
        string[] a = Lines("the,quick,brown,fox,jumps,over,the,lazy,dog");
        string[] b = Lines("a,quick,red,fox,leaps,over,the,dog,again");

        string text = diff.Unified("a", "b", a, b, 100);
        string[] body = [.. text.Split('\n').Skip(3).Where(line => line.Length > 0)];

        Assert.Equal(a, body.Where(line => line[0] != '+').Select(line => line[1..]));
        Assert.Equal(b, body.Where(line => line[0] != '-').Select(line => line[1..]));
    }

    [Fact]
    public void Unified_GivesUpOnTextsThatDifferInTooManyLines()
        => Assert.Throws<InvalidOperationException>(() => diff.Unified("a", "b", [.. Enumerable.Range(0, 2500).Select(number => $"a{number}")], [.. Enumerable.Range(0, 2500).Select(number => $"b{number}")], 3));
}
