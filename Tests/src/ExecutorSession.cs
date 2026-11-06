namespace Markwardt.LiveAi.Tests;

/// <summary>
/// A session whose requests run through the real request pipeline over a mocked executor, for testing the request pipeline without a whole session.
/// </summary>
internal sealed class ExecutorSession
{
    private readonly PromptRequester requester;

    public ExecutorSession(IPromptExecutor executor)
    {
        requester = new PromptRequester(executor);
        Mock<IPromptSession> mock = new();
        mock.Setup(session => session.Request(It.IsAny<object>(), It.IsAny<PromptRequestOptions?>(), It.IsAny<CancellationToken>())).Returns(async (object input, PromptRequestOptions? options, CancellationToken cancellation) => (await requester.Collect(input, options ?? new(), cancellation)).Text);
        mock.As<IPromptCollector>().Setup(session => session.Collect(It.IsAny<object>(), It.IsAny<PromptRequestOptions?>(), It.IsAny<CancellationToken>())).Returns((object input, PromptRequestOptions? options, CancellationToken cancellation) => requester.Collect(input, options ?? new(), cancellation));
        Session = mock.Object;
    }

    public IPromptSession Session { get; }
}
