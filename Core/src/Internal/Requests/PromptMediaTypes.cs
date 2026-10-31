namespace Markwardt.LiveAi;

/// <summary>
/// Media types of the files that can go along with a prompt.
/// </summary>
internal static class PromptMediaTypes
{
    private static readonly Dictionary<string, string> types = new(StringComparer.OrdinalIgnoreCase)
    {
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".gif"] = "image/gif",
        [".webp"] = "image/webp",
        [".pdf"] = "application/pdf",
        [".txt"] = "text/plain",
        [".md"] = "text/markdown",
        [".csv"] = "text/csv",
        [".tsv"] = "text/tab-separated-values",
        [".html"] = "text/html",
        [".htm"] = "text/html",
        [".css"] = "text/css",
        [".log"] = "text/plain",
        [".ini"] = "text/plain",
        [".cs"] = "text/plain",
        [".py"] = "text/plain",
        [".js"] = "text/javascript",
        [".ts"] = "text/plain",
        [".java"] = "text/plain",
        [".go"] = "text/plain",
        [".rs"] = "text/plain",
        [".sh"] = "text/plain",
        [".sql"] = "text/plain",
        [".json"] = "application/json",
        [".xml"] = "application/xml",
        [".yaml"] = "application/yaml",
        [".yml"] = "application/yaml"
    };

    extension(string name)
    {
        /// <summary>
        /// Gets the media type for a file name, from its extension.
        /// </summary>
        /// <returns>The media type, or <c>application/octet-stream</c> for an extension that is not known.</returns>
        public string ToMediaType()
            => types.TryGetValue(Path.GetExtension(name), out string? type) ? type : "application/octet-stream";
    }

    extension(string mediaType)
    {
        /// <summary>
        /// Tells whether a media type is an image the AI tools can be shown.
        /// </summary>
        /// <returns>Whether it is a PNG, JPEG, GIF or WebP image.</returns>
        public bool IsImage()
            => mediaType is "image/png" or "image/jpeg" or "image/gif" or "image/webp";

        /// <summary>
        /// Tells whether a media type is one of text, which can be put into a prompt.
        /// </summary>
        /// <returns>Whether it is <c>text/*</c>, JSON, XML or YAML.</returns>
        public bool IsText()
            => mediaType.StartsWith("text/", StringComparison.OrdinalIgnoreCase) || mediaType is "application/json" or "application/xml" or "application/yaml" or "application/x-yaml" || mediaType.EndsWith("+json", StringComparison.OrdinalIgnoreCase) || mediaType.EndsWith("+xml", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Gets the file extension to give a file of an image type, which the AI tool uses to tell its type.
        /// </summary>
        /// <returns>The extension with its dot.</returns>
        public string ToExtension()
            => mediaType switch
            {
                "image/png" => ".png",
                "image/jpeg" => ".jpg",
                "image/gif" => ".gif",
                "image/webp" => ".webp",
                _ => ".bin"
            };
    }
}
