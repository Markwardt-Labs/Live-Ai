namespace Markwardt.LiveAi.Tests.Unit;

internal static class StartChecks
{
    extension(PromptEngineStart start)
    {
        public bool HasEndpoint()
            => start.Servers.Any(server => server.Name == "liveai");

        public bool OffersOnlyRespond()
            => start.HasEndpoint() && !start.HasShell();

        public bool HasFiles()
            => start.Instructions.Contains("file tools");

        public bool HasShell()
            => start.Instructions.Contains("shell tool") && !start.Instructions.Contains("You have no shell");

        public bool HasWorkspace()
            => start.Instructions.Contains("relative to your workspace");

        public bool ShellInWorkspace()
            => start.Instructions.Contains("The shell tool starts in your workspace.");

        public bool Reaches(ToolEndpoint endpoint)
            => start.Servers.Any(server => server.Url == endpoint.Url);
    }
}
