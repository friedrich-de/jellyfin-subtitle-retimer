namespace Jellyfin.Plugin.Retimer.Configuration;

/// <summary>
/// One model the administrator added to the list in the settings.
/// </summary>
public class ModelEntry
{
    /// <summary>
    /// Gets or sets the model's id on OpenRouter, such as <c>anthropic/claude-haiku-5.5</c>.
    /// </summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the name the page shows for the model.
    /// </summary>
    public string Name { get; set; } = string.Empty;
}
