using System.Text.Json.Serialization;

namespace Coda.Sdk.Serve.Messages;

public sealed record QuestionResponse(
    [property: JsonPropertyName("answer")] string Answer);
