namespace Markwardt.LiveAi;

/// <summary>
/// Which service makes the images the AI asks for, and how it is reached. It has no members of its own: each service has a type that holds just what that service needs, and only the library derives from it: <see cref="OpenAiImageSetup" /> and <see cref="LocalImageSetup" />. The model is the library's choice, so a caller never lists or names one. Set it as <see cref="PromptEnvironment.ImageGeneration" />, where its API key lives too; a session is not given one, it only says whether the AI may use the environment's (<see cref="IPromptSessionConfiguration.IsImageGenerationAllowed" />). It is not stored with sessions, and its JSON carries a <c>service</c> discriminator so an environment can be read from a file.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "service")]
[JsonDerivedType(typeof(OpenAiImageSetup), "openai")]
[JsonDerivedType(typeof(LocalImageSetup), "local")]
public abstract record PromptImageSetup
{
    private protected PromptImageSetup()
    {
    }
}
