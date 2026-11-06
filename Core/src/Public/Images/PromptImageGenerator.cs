namespace Markwardt.LiveAi;

/// <summary>
/// Makes images for the AI of a session, which asks for them with the <c>generate_image</c> tool once one is set as <see cref="IPromptSessionConfiguration.ImageGenerator" />. <see cref="PromptImageGeneratorExtensions" /> makes the ones the library has, for the OpenAI image API and for a local Stable Diffusion server; another one is a class that implements this interface.
/// </summary>
public interface IPromptImageGenerator
{
    /// <summary>
    /// Gets what the generator can do beyond the basics, which decides what the AI is offered.
    /// </summary>
    PromptImageFeatures Features { get; }

    /// <summary>
    /// Gets advice on how to describe an image to this generator, which is added to the description of the tool, or <see langword="null" /> for none.
    /// </summary>
    string? PromptGuide { get; }

    /// <summary>
    /// Makes images.
    /// </summary>
    /// <param name="request">What to make.</param>
    /// <param name="cancellation">Cancels the work.</param>
    /// <returns>The images, which can be fewer than asked for when the generator makes fewer. A failure, a refusal included, is thrown, and its message is what the AI reads.</returns>
    /// <exception cref="InvalidOperationException">The generator could not make the image, with the reason.</exception>
    Task<IReadOnlyList<PromptImageResult>> Generate(PromptImageRequest request, CancellationToken cancellation = default);
}

/// <summary>
/// Makes the image generators of the library.
/// </summary>
public static class PromptImageGeneratorExtensions
{
    extension(IPromptImageGenerator)
    {
        /// <summary>
        /// Makes a generator for the OpenAI image API, which also works with a server that copies its <c>/images/generations</c> endpoint. It can make images with a transparent background, except with <c>gpt-image-2</c> itself (and its dated snapshots), which the API refuses it for, so the AI is not offered it there. It sends a size from the three the image models take, picked by the shape asked for, and the quality as <c>low</c>, <c>medium</c> or <c>high</c>.
        /// </summary>
        /// <param name="apiKey">The API key. It is only sent to <paramref name="baseUrl" /> and is never stored or logged.</param>
        /// <param name="model">The model to use, <c>gpt-image-2.6-flare</c> by default, which can make a transparent background. Names change and models differ in price and in what they can do (see <see cref="PromptImageModel.Features" />); <see cref="OpenAiModels" /> lists what the account can use.</param>
        /// <param name="baseUrl">The address of the API with its version path, or <see langword="null" /> for <c>https://api.openai.com/v1</c>.</param>
        /// <returns>The generator.</returns>
        /// <exception cref="ArgumentException"><paramref name="apiKey" /> is empty.</exception>
        public static IPromptImageGenerator OpenAi(string apiKey, string model = "gpt-image-2.6-flare", Uri? baseUrl = null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
            return new OpenAiImageGenerator(apiKey, model, baseUrl ?? new Uri("https://api.openai.com/v1"), new HttpClient(new SocketsHttpHandler()) { Timeout = TimeSpan.FromMinutes(5) });
        }

        /// <summary>
        /// Lists the image models of the OpenAI API, or a server that copies it, in the order of their names, each with what it can do (see <see cref="PromptImageModel.Features" />). Pass the <see cref="PromptImageModel.Id" /> of one to <see cref="OpenAi" /> to make images with it. Only the models the generator can use are listed (the GPT Image ones), and the list is for the account of the key, so it can differ from the one in the documentation.
        /// </summary>
        /// <param name="apiKey">The API key. It is only sent to <paramref name="baseUrl" /> and is never stored or logged.</param>
        /// <param name="baseUrl">The address of the API with its version path, or <see langword="null" /> for <c>https://api.openai.com/v1</c>.</param>
        /// <param name="cancellation">Cancels the listing.</param>
        /// <returns>The models.</returns>
        /// <exception cref="ArgumentException"><paramref name="apiKey" /> is empty.</exception>
        /// <exception cref="InvalidOperationException">The service refused (a key it does not accept, say), could not be reached or answered something unreadable.</exception>
        public static async Task<IReadOnlyList<PromptImageModel>> OpenAiModels(string apiKey, Uri? baseUrl = null, CancellationToken cancellation = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
            using HttpClient http = new() { Timeout = TimeSpan.FromSeconds(30) };
            return await new OpenAiImageCatalog(apiKey, baseUrl ?? new Uri("https://api.openai.com/v1"), http).List(cancellation);
        }

        /// <summary>
        /// Lists the checkpoints of a Stable Diffusion server (AUTOMATIC1111 or Forge, started with its API on, or another server with that API), in the order of their titles. Pass the <see cref="PromptImageModel.Id" /> of one to <see cref="StableDiffusion" /> to have the server load it for each image. Every checkpoint takes a negative prompt and a seed, and the steps and guidance that suit it are for the caller to know, since the server does not say.
        /// </summary>
        /// <param name="baseUrl">The address of the server, or <see langword="null" /> for <c>http://localhost:7860</c>.</param>
        /// <param name="cancellation">Cancels the listing.</param>
        /// <returns>The models.</returns>
        /// <exception cref="InvalidOperationException">The server refused, could not be reached (it is not running, or not with its API on) or answered something unreadable.</exception>
        public static async Task<IReadOnlyList<PromptImageModel>> StableDiffusionModels(Uri? baseUrl = null, CancellationToken cancellation = default)
        {
            using HttpClient http = new() { Timeout = TimeSpan.FromSeconds(30) };
            return await new StableDiffusionImageCatalog(baseUrl ?? new Uri("http://localhost:7860"), http).List(cancellation);
        }

        /// <summary>
        /// Makes a generator for a Stable Diffusion server on this machine or the network that has the AUTOMATIC1111 API (AUTOMATIC1111 itself or Forge, started with its API on). It takes a negative prompt and a seed, and reports the seed. The quality becomes a number of steps, which depends on the model (a turbo model needs few, a full one many), so give the numbers that suit the one the server loads.
        /// </summary>
        /// <param name="baseUrl">The address of the server, or <see langword="null" /> for <c>http://localhost:7860</c>.</param>
        /// <param name="model">The checkpoint to load for each image, or <see langword="null" /> for the one the server has loaded.</param>
        /// <param name="width">The width when the AI gives none: what the model was trained at (512 for Stable Diffusion 1.5, 1024 for SDXL).</param>
        /// <param name="height">The height when the AI gives none.</param>
        /// <param name="draftSteps">The sampling steps of a draft.</param>
        /// <param name="standardSteps">The sampling steps of a standard image.</param>
        /// <param name="highSteps">The sampling steps of a high quality image.</param>
        /// <param name="guidance">The classifier-free guidance scale (<c>cfg_scale</c>); turbo models want a low one, around 1 to 2.</param>
        /// <param name="promptGuide">Advice on how to describe an image to the model, or <see langword="null" /> for the usual advice for Stable Diffusion (comma separated phrases). Give plain sentences for a model that follows them, such as the FLUX family.</param>
        /// <returns>The generator.</returns>
        /// <exception cref="ArgumentOutOfRangeException">A size or a number of steps is less than 1, or the guidance is negative.</exception>
        public static IPromptImageGenerator StableDiffusion(Uri? baseUrl = null, string? model = null, int width = 1024, int height = 1024, int draftSteps = 8, int standardSteps = 20, int highSteps = 35, double guidance = 7, string? promptGuide = null)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
            ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
            ArgumentOutOfRangeException.ThrowIfLessThan(draftSteps, 1);
            ArgumentOutOfRangeException.ThrowIfLessThan(standardSteps, 1);
            ArgumentOutOfRangeException.ThrowIfLessThan(highSteps, 1);
            ArgumentOutOfRangeException.ThrowIfNegative(guidance);
            return new StableDiffusionImageGenerator(baseUrl ?? new Uri("http://localhost:7860"), model, width, height, [draftSteps, standardSteps, highSteps], guidance, promptGuide, new HttpClient(new SocketsHttpHandler()) { Timeout = TimeSpan.FromMinutes(10) });
        }
    }
}
