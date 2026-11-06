namespace Markwardt.LiveAi.Tests.Unit;

public sealed class PromptMediaTypesTests
{
    [Theory]
    [InlineData(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A }, "image/png")]
    [InlineData(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 }, "image/jpeg")]
    [InlineData(new byte[] { 0x47, 0x49, 0x46, 0x38, 0x39, 0x61 }, "image/gif")]
    [InlineData(new byte[] { 0x52, 0x49, 0x46, 0x46, 1, 2, 3, 4, 0x57, 0x45, 0x42, 0x50, 9 }, "image/webp")]
    [InlineData(new byte[] { 0x52, 0x49, 0x46, 0x46, 1, 2, 3, 4, 0x57, 0x41, 0x56, 0x45 }, "application/octet-stream")]
    [InlineData(new byte[] { 1, 2, 3 }, "application/octet-stream")]
    [InlineData(new byte[] { }, "application/octet-stream")]
    public void ToImageMediaType_TellsTheTypeOfEncodedBytesByTheirFirstBytes(byte[] bytes, string expected)
        => Assert.Equal(expected, new ReadOnlyMemory<byte>(bytes).ToImageMediaType());
}
