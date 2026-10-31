namespace Markwardt.LiveAi.Tests;

/// <summary>
/// A session whose requests run through the real request pipeline over a mocked executor, for testing what is built on <see cref="IPromptSession.Request" /> (loops) without a whole session.
/// </summary>
internal sealed class ExecutorSession
{
    private readonly PromptRequester requester;

    public ExecutorSession(IPromptExecutor executor, PromptRequestLimits? limits = null, TimeProvider? time = null)
    {
        requester = new PromptRequester(executor, null, time);
        Mock<IPromptSession> mock = new();
        mock.Setup(session => session.Request(It.IsAny<object>(), It.IsAny<PromptRequestOptions?>(), It.IsAny<CancellationToken>())).Returns(async (object input, PromptRequestOptions? options, CancellationToken cancellation) => (await requester.Collect(input, options ?? new(), limits ?? new(), cancellation)).Text);
        mock.As<IPromptCollector>().Setup(session => session.Collect(It.IsAny<object>(), It.IsAny<PromptRequestOptions?>(), It.IsAny<PromptRequestLimits?>(), It.IsAny<CancellationToken>())).Returns((object input, PromptRequestOptions? options, PromptRequestLimits? extra, CancellationToken cancellation) => requester.Collect(input, options ?? new(), (limits ?? new()).WithWindowOf(extra), cancellation));
        mock.As<IPromptCollector>().SetupGet(session => session.Time).Returns(time ?? TimeProvider.System);
        Session = mock.Object;
    }

    public IPromptSession Session { get; }
}
