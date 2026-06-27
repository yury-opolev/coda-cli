using System.Text.Json.Serialization;

namespace Coda.Sdk.Serve.Messages;

public sealed record QuestionRequest(
    [property: JsonPropertyName("question")] string Question,
    [property: JsonPropertyName("options")] IReadOnlyList<string> Options,
    [property: JsonPropertyName("multiSelect")] bool MultiSelect);
