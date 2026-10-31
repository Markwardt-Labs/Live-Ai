namespace Markwardt.LiveAi.Tests;

internal static class SessionTestExtensions
{
    extension(IPromptSession session)
    {
        public Task<PromptResult> Result(object input, PromptRequestOptions? options = null, CancellationToken cancellation = default)
            => ((IPromptCollector)session).Collect(input, options, null, cancellation);

        public async Task<List<string>> Messages(object input, PromptRequestOptions? options = null, CancellationToken cancellation = default)
            => [.. (await session.Result(input, options, cancellation)).Events.OfType<PromptMessageEvent>().Select(message => message.Text)];

        public async Task<List<PromptEvent>> EventList(object input, PromptRequestOptions? options = null, CancellationToken cancellation = default)
            => [.. (await session.Result(input, options, cancellation)).Events];
    }
}
