namespace Coda.Agent.Classifier;

/// <summary>
/// Inspects a single proposed tool action (in bypass/"yolo" mode) and decides
/// whether it is safe to auto-approve or must be confirmed by the user.
/// </summary>
public interface IToolActionClassifier
{
    Task<ToolActionVerdict> ClassifyAsync(string toolName, string inputJson, CancellationToken cancellationToken = default);
}
