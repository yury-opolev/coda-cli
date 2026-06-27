using Coda.Agent;

namespace Coda.Agent.Classifier;

/// <summary>
/// An <see cref="IPermissionPrompt"/> for bypass/"yolo" mode: every mutating tool
/// action is classified first. Safe actions run automatically; risky ones are
/// escalated to the <see cref="inner"/> interactive prompt, or denied when there is
/// no inner prompt (headless). This is what makes yolo mode "allow everything safe,
/// but stop for the dangerous stuff."
/// </summary>
public sealed class ClassifierPermissionPrompt : IPermissionPrompt
{
    private readonly IToolActionClassifier classifier;
    private readonly IPermissionPrompt? inner;

    public ClassifierPermissionPrompt(IToolActionClassifier classifier, IPermissionPrompt? inner)
    {
        this.classifier = classifier ?? throw new ArgumentNullException(nameof(classifier));
        this.inner = inner;
    }

    public async Task<bool> RequestAsync(ITool tool, string inputPreview, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tool);

        var verdict = await this.classifier.ClassifyAsync(tool.Name, inputPreview, cancellationToken).ConfigureAwait(false);

        return verdict.Decision switch
        {
            PermissionDecision.Allow => true,
            PermissionDecision.Deny => false,
            PermissionDecision.Ask when this.inner is not null =>
                await this.inner.RequestAsync(tool, inputPreview, cancellationToken).ConfigureAwait(false),
            _ => false,
        };
    }
}
