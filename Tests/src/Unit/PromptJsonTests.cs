namespace Markwardt.LiveAi.Tests.Unit;

public sealed class PromptJsonTests
{
    [Fact]
    public void Schema_DescribesTheTypeAsAnObjectWithItsProperties()
    {
        JsonElement schema = PromptJson.Schema(typeof(Answer));

        Assert.Equal("object", schema.GetProperty("type").GetString());
        JsonElement properties = schema.GetProperty("properties");
        Assert.Equal("string", properties.GetProperty("summary").GetProperty("type").GetString());
        Assert.Equal("integer", properties.GetProperty("score").GetProperty("type").GetString());
        Assert.Contains("summary", schema.GetProperty("required").EnumerateArray().Select(name => name.GetString()));
        Assert.Contains("score", schema.GetProperty("required").EnumerateArray().Select(name => name.GetString()));
        Assert.DoesNotContain("details", schema.GetProperty("required").EnumerateArray().Select(name => name.GetString()));
    }

    [Fact]
    public void Schema_WritesEnumsAsTheirNames()
    {
        JsonElement schema = PromptJson.Schema(typeof(Question));

        Assert.Equal(["Low", "High"], schema.GetProperty("properties").GetProperty("level").GetProperty("enum").EnumerateArray().Select(name => name.GetString()));
        Assert.Equal("array", schema.GetProperty("properties").GetProperty("items").GetProperty("type").GetString());
    }

    [Theory]
    [InlineData(typeof(string), "string")]
    [InlineData(typeof(int), "integer")]
    [InlineData(typeof(string[]), "array")]
    [InlineData(typeof(List<string>), "array")]
    public void Output_OfATypeThatIsNotAnObject_IsAnObjectWithOneRequiredValueProperty(Type type, string kind)
    {
        (JsonElement schema, bool wrapped) = PromptJson.Output(type);

        Assert.True(wrapped);
        Assert.Equal("object", schema.GetProperty("type").GetString());
        Assert.Equal(kind, schema.GetProperty("properties").GetProperty("value").GetProperty("type").GetString());
        Assert.Equal(["value"], schema.GetProperty("required").EnumerateArray().Select(name => name.GetString()));
        Assert.False(schema.GetProperty("additionalProperties").GetBoolean());
    }

    [Fact]
    public void Output_OfAnObjectType_IsItsOwnSchemaAndNotWrapped()
    {
        (JsonElement schema, bool wrapped) = PromptJson.Output(typeof(Answer));

        Assert.False(wrapped);
        Assert.Equal(PromptJson.Schema(typeof(Answer)).GetRawText(), schema.GetRawText());
    }

    [Fact]
    public void Deserialize_OfAWrappedAnswer_ReadsTheValueProperty()
    {
        Assert.Equal(["a", "b"], PromptJson.Deserialize<string[]>(["thinking", """{"value":["a","b"]}"""], true));
        Assert.Equal(7, PromptJson.Deserialize<int>(["""{"value":7}"""], true));
        Assert.Equal(Level.High, PromptJson.Deserialize<Level>(["""{"value":"High"}"""], true));
    }

    [Theory]
    [InlineData("""{"other":1}""")]
    [InlineData("""[1]""")]
    [InlineData("""{"value":"not a number"}""")]
    [InlineData("""{"value":null}""")]
    [InlineData("not json")]
    public void Deserialize_OfAWrappedAnswerThatIsWrong_FailsWithTheOutputError(string text)
    {
        PromptSessionOutputException exception = Assert.Throws<PromptSessionOutputException>(() => PromptJson.Deserialize<int>([text], true));

        Assert.Equal(text, exception.Output);
    }

    [Fact]
    public void Deserialize_OfAWrappedAnswerWithNoMessages_FailsWithTheNoOutputError()
        => Assert.Contains("wrote no structured output", Assert.Throws<PromptSessionOutputException>(() => PromptJson.Deserialize<int>([], true)).Message);

    [Theory]
    [InlineData(typeof(string))]
    [InlineData(typeof(int))]
    [InlineData(typeof(List<string>))]
    [InlineData(typeof(string[]))]
    public void Schema_RejectsATypeThatIsNotAnObject(Type type)
        => Assert.Throws<ArgumentException>(() => PromptJson.Schema(type));

    [Fact]
    public void Serialize_WritesTheRuntimeTypeInCamelCaseWithEnumNames()
    {
        object input = new Question("sort", ["b", "a"], Level.High);

        Assert.Equal("""{"task":"sort","items":["b","a"],"level":"High"}""", PromptJson.Serialize(input));
    }

    [Fact]
    public void Deserialize_ReadsTheLastMessage()
    {
        Answer answer = PromptJson.Deserialize<Answer>(["working on it", """{"summary":"done","score":7,"details":{"note":"n"}}"""]);

        Assert.Equal(new Answer("done", 7, new Details("n")), answer);
    }

    [Fact]
    public void Deserialize_WithoutMessages_Throws()
    {
        PromptSessionOutputException exception = Assert.Throws<PromptSessionOutputException>(() => PromptJson.Deserialize<Answer>([]));

        Assert.Null(exception.Output);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{"summary":"missing the score"}""")]
    [InlineData("""{"summary":"x","score":"seven"}""")]
    [InlineData("null")]
    public void Deserialize_WhenTheAnswerIsNotTheType_ThrowsWithTheAnswer(string output)
    {
        PromptSessionOutputException exception = Assert.Throws<PromptSessionOutputException>(() => PromptJson.Deserialize<Answer>([output]));

        Assert.Equal(output, exception.Output);
    }
}
