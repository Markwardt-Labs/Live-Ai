namespace Markwardt.LiveAi;

/// <summary>
/// Turns .NET objects into prompts and the AI's structured answers back into .NET objects, always with the same serializer options: web defaults (camel case) with enums as strings and numbers only as JSON numbers, and with required constructor parameters and non-nullable annotations enforced, so an answer that leaves something out is an error instead of a default.
/// </summary>
internal static class PromptJson
{
    private static readonly string wrapperProperty = "value";

    private static readonly JsonSchemaExporterOptions exporter = new() { TreatNullObliviousAsNonNullable = true };

    /// <summary>
    /// Gets the serializer options everything is written and read with.
    /// </summary>
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() }, NumberHandling = JsonNumberHandling.Strict, RespectRequiredConstructorParameters = true, RespectNullableAnnotations = true };

    /// <summary>
    /// Builds the JSON Schema of a type.
    /// </summary>
    /// <param name="type">The type of the structured output.</param>
    /// <returns>The schema, whose root is an object.</returns>
    /// <exception cref="ArgumentException"><paramref name="type" /> is not written as a JSON object.</exception>
    public static JsonElement Schema(Type type)
    {
        JsonNode root = Root(type);

        if (!IsObject(root))
        {
            throw new ArgumentException($"{type} is not written as a JSON object, which the AI tools require of structured output. Wrap it in a type that has a property for it.", nameof(type));
        }

        return JsonSerializer.SerializeToElement(root);
    }

    /// <summary>
    /// Builds the JSON Schema the AI answers under for a type. A type that is not written as an object (a string, a number, an array, an enumeration) is answered in an object with one property, <c>value</c>, because the AI tools require an object at the root.
    /// </summary>
    /// <param name="type">The type of the structured output.</param>
    /// <returns>The schema, whose root is an object, and whether the answer is wrapped in an object with one property.</returns>
    public static (JsonElement Schema, bool Wrapped) Output(Type type)
    {
        JsonNode root = Root(type);

        if (IsObject(root))
        {
            return (JsonSerializer.SerializeToElement(root), false);
        }

        JsonObject wrapper = new()
        {
            ["type"] = "object",
            ["properties"] = new JsonObject { [wrapperProperty] = root },
            ["required"] = new JsonArray(wrapperProperty),
            ["additionalProperties"] = false
        };

        return (JsonSerializer.SerializeToElement(wrapper), true);
    }

    private static JsonNode Root(Type type)
    {
        JsonNode schema = JsonSchemaExporter.GetJsonSchemaAsNode(new JsonSerializerOptions(Options) { TypeInfoResolver = new DefaultJsonTypeInfoResolver() }, type, exporter);

        if (schema is JsonObject root && root["type"] is JsonArray types && types.Select(entry => entry?.GetValue<string>()).Where(name => name != "null").ToList() is [string only])
        {
            root["type"] = only;
        }

        return Written(schema);
    }

    private static JsonNode Written(JsonNode schema)
    {
        if (schema is JsonValue { } value && value.TryGetValue(out bool allowed))
        {
            return allowed ? new JsonObject() : new JsonObject { ["not"] = new JsonObject() };
        }

        if (schema is not JsonObject root)
        {
            return schema;
        }

        foreach (string keyword in (string[])["properties", "patternProperties", "$defs", "definitions", "dependentSchemas"])
        {
            if (root[keyword] is JsonObject map)
            {
                foreach ((string name, JsonNode? member) in map.ToArray())
                {
                    if (member is not null && Written(member) is { } replaced && !ReferenceEquals(replaced, member))
                    {
                        map[name] = replaced;
                    }
                }
            }
        }

        foreach (string keyword in (string[])["items", "not", "contains", "propertyNames", "if", "then", "else"])
        {
            if (root[keyword] is JsonNode single && Written(single) is { } replaced && !ReferenceEquals(replaced, single))
            {
                root[keyword] = replaced;
            }
        }

        foreach (string keyword in (string[])["allOf", "anyOf", "oneOf", "prefixItems"])
        {
            if (root[keyword] is JsonArray list)
            {
                for (int index = 0; index < list.Count; index++)
                {
                    if (list[index] is { } member && Written(member) is { } replaced && !ReferenceEquals(replaced, member))
                    {
                        list[index] = replaced;
                    }
                }
            }
        }

        return root;
    }

    private static bool IsObject(JsonNode schema)
        => schema is JsonObject { } root && root["type"] is JsonValue kind && kind.TryGetValue(out string? name) && name == "object";

    /// <summary>
    /// Writes an input object as the text of a prompt.
    /// </summary>
    /// <param name="input">The input object, written with its own runtime type.</param>
    /// <returns>The JSON text.</returns>
    public static string Serialize(object input)
        => JsonSerializer.Serialize(input, input.GetType(), Options);

    /// <summary>
    /// Reads the last message as the structured output, unwrapping it first when it was asked for in an object with one property.
    /// </summary>
    /// <typeparam name="TOutput">The type of the structured output.</typeparam>
    /// <param name="messages">The messages the AI wrote; the last one is the answer.</param>
    /// <param name="wrapped">Whether the answer is in an object with one property (see <see cref="Output" />).</param>
    /// <returns>The answer.</returns>
    /// <exception cref="PromptSessionOutputException">There is no answer, or it is not JSON of the requested type.</exception>
    public static TOutput Deserialize<TOutput>(IReadOnlyList<string> messages, bool wrapped)
    {
        if (!wrapped)
        {
            return Deserialize<TOutput>(messages);
        }

        if (messages.Count == 0)
        {
            throw new PromptSessionOutputException("The AI wrote no structured output.", null);
        }

        string output = messages[^1];

        try
        {
            using JsonDocument document = JsonDocument.Parse(output);

            if (document.RootElement.ValueKind != JsonValueKind.Object || !document.RootElement.TryGetProperty(wrapperProperty, out JsonElement value))
            {
                throw new PromptSessionOutputException($"The AI's structured output has no '{wrapperProperty}' property.", output);
            }

            return value.Deserialize<TOutput>(Options) ?? throw new PromptSessionOutputException("The AI's structured output is null.", output);
        }
        catch (JsonException exception)
        {
            throw new PromptSessionOutputException($"The AI's structured output is not a valid {typeof(TOutput)}: {exception.Message}", output, exception);
        }
    }

    /// <summary>
    /// Reads the structured answer from what the AI wrote.
    /// </summary>
    /// <typeparam name="TOutput">The type of the structured output.</typeparam>
    /// <param name="messages">The messages the AI wrote, in order; the last one is the answer.</param>
    /// <returns>The answer.</returns>
    /// <exception cref="PromptSessionOutputException">There is no answer, or it is not JSON of the requested type.</exception>
    public static TOutput Deserialize<TOutput>(IReadOnlyList<string> messages)
    {
        if (messages.Count == 0)
        {
            throw new PromptSessionOutputException("The AI wrote no structured output.", null);
        }

        string output = messages[^1];

        try
        {
            return JsonSerializer.Deserialize<TOutput>(output, Options) ?? throw new PromptSessionOutputException("The AI's structured output is null.", output);
        }
        catch (JsonException exception)
        {
            throw new PromptSessionOutputException($"The AI's structured output is not a valid {typeof(TOutput)}: {exception.Message}", output, exception);
        }
    }
}
