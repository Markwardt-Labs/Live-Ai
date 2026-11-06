namespace Markwardt.LiveAi;

/// <summary>
/// Writes a <see cref="Guid" /> without hyphens, the same text a session reports as its identifier.
/// </summary>
internal sealed class CompactGuidConverter : JsonConverter<Guid>
{
    /// <inheritdoc />
    public override Guid Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => Guid.Parse(reader.GetString()!);

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, Guid value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.ToString("N"));
}
