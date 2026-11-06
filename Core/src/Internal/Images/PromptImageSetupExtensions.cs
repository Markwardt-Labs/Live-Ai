namespace Markwardt.LiveAi;

/// <summary>
/// What the library does with the options of an image service.
/// </summary>
internal static class PromptImageSetupExtensions
{
    extension(PromptImageSetup options)
    {
        /// <summary>
        /// Checks the options when they are set.
        /// </summary>
        /// <exception cref="ArgumentException">A value is not allowed.</exception>
        public void Validate()
        {
            switch (options)
            {
                case OpenAiImageSetup openAi:
                    ArgumentException.ThrowIfNullOrWhiteSpace(openAi.ApiKey, nameof(options));
                    break;
                case LocalImageSetup stable:
                    ArgumentNullException.ThrowIfNull(stable.Server);

                    PromptCapabilityModels? models = stable.Models;
                    ArgumentNullException.ThrowIfNull(models, nameof(options));

                    if (new[] { models.Fast, models.Balanced, models.Strong, models.Max }.Any(string.IsNullOrWhiteSpace))
                    {
                        throw new ArgumentException("Every capability needs the title of a checkpoint to load.", nameof(options));
                    }

                    break;
                default:
                    throw new ArgumentException($"{options.GetType().Name} is not a kind of image options the library knows.", nameof(options));
            }
        }

        /// <summary>
        /// Makes the generator that talks to the service, by the type of the options.
        /// </summary>
        /// <returns>The generator.</returns>
        /// <exception cref="ArgumentException">The options are of a type the library does not know.</exception>
        public IPromptImageGenerator CreateGenerator()
            => options switch
            {
                OpenAiImageSetup openAi => new OpenAiCapabilityImageGenerator(openAi.ApiKey),
                LocalImageSetup stable => new StableDiffusionImageGenerator(stable.Server, stable.Models, 1024, 1024, [8, 20, 35], 7, null, new HttpClient(new SocketsHttpHandler()) { Timeout = TimeSpan.FromMinutes(10) }),
                _ => throw new ArgumentException($"{options.GetType().Name} is not a kind of image options the library knows.", nameof(options))
            };
    }
}
