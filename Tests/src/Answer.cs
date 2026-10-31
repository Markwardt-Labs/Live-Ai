namespace Markwardt.LiveAi.Tests;

public sealed record Answer(string Summary, int Score, Details? Details = null);
