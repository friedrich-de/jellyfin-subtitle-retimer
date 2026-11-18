namespace Jellyfin.Plugin.Retimer.Api.Models;

/// <summary>
/// Names the model to ask a test question.
/// </summary>
public sealed class ModelTestRequest
{
    /// <summary>
    /// Gets or sets the model's id at OpenRouter, such as <c>anthropic/claude-haiku-5.5</c>.
    /// </summary>
    public string? ModelId { get; set; }
}
