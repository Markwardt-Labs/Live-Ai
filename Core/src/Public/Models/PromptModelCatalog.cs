namespace Markwardt.LiveAi;

/// <summary>
/// Lists the models a tool can run, so a caller can choose them for the capabilities of a <see cref="PromptModels" /> before it builds the engine that uses them. A catalog needs no engine: it is made from the same few arguments the tool's engine takes.
/// </summary>
public interface IPromptModelCatalog
{
    /// <summary>
    /// Lists the models that can be used to run prompts: a model that cannot call tools, such as an embedding model, is left out when the tool tells which those are. Listing never fails because the tool is not there: a tool that is not installed, a server that is not running and an answer that cannot be read all give an empty list, which is also what a tool with no models gives.
    /// </summary>
    /// <param name="cancellation">Cancels the listing.</param>
    /// <returns>The models, in the order the tool lists them.</returns>
    Task<IReadOnlyList<PromptModel>> List(CancellationToken cancellation = default);
}
