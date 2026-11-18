namespace Jellyfin.Plugin.Retimer.Api.Models;

/// <summary>
/// Sets the subtitle language of one show or film.
/// </summary>
public sealed class SetLanguageRequest
{
    /// <summary>
    /// Gets or sets the language code, such as <c>ja</c>. Empty means the server's language.
    /// </summary>
    public string? Language { get; set; }
}
