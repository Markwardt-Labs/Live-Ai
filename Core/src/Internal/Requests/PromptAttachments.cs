namespace Markwardt.LiveAi;

/// <summary>
/// Sorts the attachments of a prompt into what goes into the prompt as text and what the engine takes as it is.
/// </summary>
internal static class PromptAttachments
{
    private static readonly int maxCount = 20;

    private static readonly int maxBytes = 20 * 1024 * 1024;

    private static readonly int maxTotalBytes = 30 * 1024 * 1024;

    private static readonly int maxTextBytes = 256 * 1024;

    private static readonly UTF8Encoding strict = new(false, true);

    extension(IReadOnlyList<PromptAttachment> attachments)
    {
        /// <summary>
        /// Checks the attachments against what the engine can take and puts the text files into the prompt.
        /// </summary>
        /// <param name="prompt">The prompt text.</param>
        /// <param name="engine">The engine the prompt goes to.</param>
        /// <returns>The prompt with the text files in it, and the attachments the engine is given as they are.</returns>
        /// <exception cref="ArgumentException">There are too many attachments, one is too large, or a text file is not valid UTF-8 text.</exception>
        /// <exception cref="NotSupportedException">An attachment is of a type that cannot be given to the engine or put into the prompt.</exception>
        public (string Prompt, IReadOnlyList<PromptAttachment> Binary) Prepare(string prompt, IPromptEngine engine)
        {
            if (attachments.Count > maxCount)
            {
                throw new ArgumentException($"A prompt can have at most {maxCount} attachments.", nameof(attachments));
            }

            if (attachments.Sum(attachment => (long)attachment.Content.Length) > maxTotalBytes)
            {
                throw new ArgumentException($"The attachments of a prompt can be at most {maxTotalBytes / 1024 / 1024} MB together.", nameof(attachments));
            }

            StringBuilder text = new(prompt);
            List<PromptAttachment> binary = [];

            foreach (PromptAttachment attachment in attachments)
            {
                if (attachment.Content.Length > maxBytes)
                {
                    throw new ArgumentException($"The attachment '{attachment.Name}' is larger than {maxBytes / 1024 / 1024} MB.", nameof(attachments));
                }

                if (engine.Accepts(attachment.MediaType))
                {
                    binary.Add(attachment);
                }
                else if (attachment.MediaType.IsText())
                {
                    text.Append(attachment.ToPromptText());
                }
                else
                {
                    throw new NotSupportedException($"{engine.Identifier} cannot be given the attachment '{attachment.Name}' of type '{attachment.MediaType}'. Images (PNG, JPEG, GIF, WebP) are shown to the AI{(engine.Accepts("application/pdf") ? ", and so are PDF documents" : string.Empty)}, and text files are put into the prompt; put anything else in the session's cache so that the AI can read it with the file tools.");
                }
            }

            return (text.ToString(), binary);
        }
    }

    extension(PromptAttachment attachment)
    {
        /// <summary>
        /// Writes a text file as it goes into the prompt: its name, then its content in a code fence that no line of the content can end early.
        /// </summary>
        /// <returns>The text to append to the prompt.</returns>
        /// <exception cref="ArgumentException">The file is too large for the prompt or is not valid UTF-8.</exception>
        public string ToPromptText()
        {
            if (attachment.Content.Length > maxTextBytes)
            {
                throw new ArgumentException($"The text file '{attachment.Name}' is larger than {maxTextBytes / 1024} KB, which is the most that is put into a prompt. Put it in the session's cache instead so that the AI can read it in parts.", nameof(attachment));
            }

            string content;

            try
            {
                content = strict.GetString(attachment.Content.Span);
            }
            catch (DecoderFallbackException)
            {
                throw new ArgumentException($"The attachment '{attachment.Name}' is of a text type but is not valid UTF-8.", nameof(attachment));
            }

            int longest = 0;
            int run = 0;

            foreach (char character in content)
            {
                run = character == '`' ? run + 1 : 0;
                longest = Math.Max(longest, run);
            }

            string fence = new('`', Math.Max(3, longest + 1));
            return $"\n\nAttached file \"{attachment.Name.Replace('"', '\'')}\":\n{fence}\n{content.TrimEnd('\r', '\n')}\n{fence}";
        }
    }
}
