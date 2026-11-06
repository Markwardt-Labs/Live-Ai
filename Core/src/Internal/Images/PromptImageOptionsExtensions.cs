namespace Markwardt.LiveAi;

/// <summary>
/// What the library does with the options of an image service.
/// </summary>
internal static class PromptImageOptionsExtensions
{
    extension(PromptImageOptions options)
    {
        /// <summary>
        /// Checks the options when they are set.
        /// </summary>
        /// <exception cref="ArgumentException">A value is not allowed.</exception>
        public void Validate()
        {
            switch (options)
            {
                case OpenAiImageOptions openAi:
                    ArgumentException.ThrowIfNullOrWhiteSpace(openAi.ApiKey, nameof(options));

                    if (!Enum.IsDefined(openAi.Capability))
                    {
                        throw new ArgumentOutOfRangeException(nameof(options), openAi.Capability, "The capability is not a defined value.");
                    }

                    break;
                case StableDiffusionImageOptions stable:
                    ArgumentNullException.ThrowIfNull(stable.Server);
                    ArgumentOutOfRangeException.ThrowIfLessThan(stable.Width, 1);
                    ArgumentOutOfRangeException.ThrowIfLessThan(stable.Height, 1);
                    ArgumentOutOfRangeException.ThrowIfLessThan(stable.DraftSteps, 1);
                    ArgumentOutOfRangeException.ThrowIfLessThan(stable.StandardSteps, 1);
                    ArgumentOutOfRangeException.ThrowIfLessThan(stable.HighSteps, 1);
                    ArgumentOutOfRangeException.ThrowIfNegative(stable.Guidance);
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
                OpenAiImageOptions openAi => new OpenAiCapabilityImageGenerator(openAi.ApiKey ?? string.Empty, openAi.Capability),
                StableDiffusionImageOptions stable => new StableDiffusionImageGenerator(stable.Server, stable.Model, stable.Width, stable.Height, [stable.DraftSteps, stable.StandardSteps, stable.HighSteps], stable.Guidance, stable.PromptGuide, new HttpClient(new SocketsHttpHandler()) { Timeout = TimeSpan.FromMinutes(10) }),
                _ => throw new ArgumentException($"{options.GetType().Name} is not a kind of image options the library knows.", nameof(options))
            };
    }
}
