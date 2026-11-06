namespace Markwardt.LiveAi;

/// <summary>
/// What an application plugs into an <see cref="IPromptSession" /> object, besides its stored <see cref="IPromptSessionConfiguration" />: code and connections that cannot be written to a file. None of it is stored, so a session that is loaded starts without them and the application sets them again. Like the configuration, they can be changed at any time and are taken together when the next prompt starts, and once the session is disposed every change throws <see cref="ObjectDisposedException" />.
/// </summary>
public interface IPromptSessionTransientConfiguration
{
    /// <summary>
    /// Gets the functions of the caller's that the AI can call, which can be added to, removed from and cleared. The names of the library's file tools (<c>list_directory</c>, <c>find_files</c>, <c>search_files</c>, <c>read_file</c>, <c>write_file</c>, <c>edit_file</c>, <c>delete_file</c>, <c>move_file</c>, <c>copy_file</c>) <c>shell</c> and <c>ask_user</c> are taken. The library serves them to the AI's tool on a local port, protected by a secret that only that tool gets. A name that is not 1 to 64 letters, digits, underscores and hyphens, is taken or is the name of another tool throws <see cref="ArgumentException" /> when it is added.
    /// </summary>
    IList<PromptTool> Tools { get; }

    /// <summary>
    /// Gets the external MCP servers the AI can use besides the library's tools, each by name, which can be added to, removed from and cleared. Their tools are offered as they are and the AI may call them all (the tool is told to approve them), so add only servers that deserve that trust. A name is 1 to 64 letters, digits, underscores and hyphens, and is not <c>liveai</c>; a server has either a command or an http(s) address. Adding something that is not allowed throws at once and changes nothing.
    /// </summary>
    IDictionary<string, PromptMcpServer> McpServers { get; }

    /// <summary>
    /// Gets or sets the function that answers the questions of the AI (a <see cref="PromptAsker" />), <see langword="null" /> by default. While it is set, the AI is offered a tool, <c>ask_user</c>, to put a question to the user and get the reply, and the tool calls this function with the question and returns the text it gives back as the user's reply; the AI waits for it, so the function can take as long as a person does. Without a function the tool is not offered. It is read when a prompt starts to decide whether to offer the tool, and again when the tool is called; a question that arrives when it has been cleared is answered with an error that says no one is there to ask. An exception from the function is returned to the AI as the error of the tool. The name <c>ask_user</c> is taken.
    /// </summary>
    PromptAsker? Asker { get; set; }
}
