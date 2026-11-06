namespace Markwardt.LiveAi;

/// <summary>
/// Which service makes the images the AI asks for, and how it is reached. It has no members of its own: each service has a type that holds just what that service needs, and only the library derives from it: <see cref="OpenAiImageOptions" /> and <see cref="StableDiffusionImageOptions" />. The model is the library's choice, so a caller never lists or names one. Set it as <see cref="IPromptSessionConfiguration.ImageGeneration" />; it is stored with the session, an API key included.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "service")]
[JsonDerivedType(typeof(OpenAiImageOptions), "openai")]
[JsonDerivedType(typeof(StableDiffusionImageOptions), "stable-diffusion")]
public abstract record PromptImageOptions
{
    private protected PromptImageOptions()
    {
    }
}
