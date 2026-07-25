using System.Text.Json.Serialization;

namespace Coda.Sdk.Serve.Messages;

/// <summary>Result of a successful <c>session/scheduleDelete</c>. A not-found id returns a
/// JSON-RPC error rather than a success result.</summary>
/// <param name="Ok">Always <see langword="true"/> on the success path.</param>
/// <param name="Id">The id of the deleted definition.</param>
public sealed record ScheduleDeleteResult(
    [property: JsonPropertyName("ok")] bool Ok,
    [property: JsonPropertyName("id")] string Id);
