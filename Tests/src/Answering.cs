namespace Markwardt.LiveAi.Tests;

/// <summary>
/// An HTTP handler that answers every request from a function and never touches the network.
/// </summary>
/// <param name="answer">Makes the answer to a request.</param>
internal sealed class Answering(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
{
    /// <inheritdoc />
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        => Task.FromResult(answer(request));
}
