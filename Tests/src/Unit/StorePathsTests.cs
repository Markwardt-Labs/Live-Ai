namespace Markwardt.LiveAi.Tests.Unit;

public sealed class StorePathsTests
{
    [Theory]
    [InlineData("", "")]
    [InlineData("/", "")]
    [InlineData(".", "")]
    [InlineData("a", "a")]
    [InlineData("a/b/c", "a/b/c")]
    [InlineData("a/", "a")]
    [InlineData("/a", "a")]
    [InlineData("a//b", "a/b")]
    [InlineData("a/./b", "a/b")]
    [InlineData("./a/./", "a")]
    [InlineData("a/b/..", "a")]
    [InlineData("a/b/../c", "a/c")]
    [InlineData("a/..", "")]
    [InlineData("a/../b", "b")]
    [InlineData("a/b/../../c", "c")]
    [InlineData("a...b/..c", "a...b/..c")]
    public void Resolved_GivesThePathInItsSimplestForm(string path, string expected)
        => Assert.Equal(expected, path.Resolved());

    [Theory]
    [InlineData("..")]
    [InlineData("../a")]
    [InlineData("a/../..")]
    [InlineData("a/b/../../../c")]
    public void Resolved_WhenThePathClimbsAboveTheTop_IsNull(string path)
        => Assert.Null(path.Resolved());
}
