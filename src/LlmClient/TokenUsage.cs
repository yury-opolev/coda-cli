namespace LlmClient;

/// <summary>Token consumption for one model turn or an accumulated session.</summary>
public sealed record TokenUsage(int InputTokens, int OutputTokens)
{
    public static TokenUsage Zero { get; } = new(0, 0);

    public TokenUsage Add(TokenUsage other) =>
        new(this.InputTokens + other.InputTokens, this.OutputTokens + other.OutputTokens);

    public int Total => this.InputTokens + this.OutputTokens;
}
