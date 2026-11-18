using System.Net.Mime;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Retimer.Api.Models;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.Retimer.Api;

/// <summary>
/// The plugin's settings and the tests of the services they point to. Only administrators reach these,
/// and no answer holds a key or a password: it says only whether one is stored.
/// </summary>
[ApiController]
[Route("Retimer")]
[Authorize(Policy = Policies.RequiresElevation)]
public class RetimerSettingsController : ControllerBase
{
    private readonly PluginServices _services;

    /// <summary>
    /// Initializes a new instance of the <see cref="RetimerSettingsController"/> class.
    /// </summary>
    /// <param name="services">The plugin's services.</param>
    public RetimerSettingsController(PluginServices services)
    {
        _services = services;
    }

    /// <summary>
    /// Reads the settings.
    /// </summary>
    /// <returns>The settings, without keys.</returns>
    [HttpGet("Settings")]
    [Produces(MediaTypeNames.Application.Json)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult GetSettings()
    {
        Response.Headers.CacheControl = "no-store";
        return Ok(_services.Settings.Get());
    }

    /// <summary>
    /// Changes settings. What the request leaves out stays as it is.
    /// </summary>
    /// <param name="update">The new values.</param>
    /// <returns>The settings as they are now, without keys.</returns>
    [HttpPut("Settings")]
    [Produces(MediaTypeNames.Application.Json)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult SaveSettings([FromBody] SettingsUpdate update)
    {
        Response.Headers.CacheControl = "no-store";
        var (view, error) = _services.Settings.Save(update);
        return error is null ? Ok(view) : Problem(error.Message, statusCode: error.Status);
    }

    /// <summary>
    /// Tests a service with the stored settings: <c>jimaku</c>, <c>opensubtitles</c>, <c>retimer</c> or <c>model</c>.
    /// </summary>
    /// <param name="target">The service.</param>
    /// <param name="request">For <c>model</c>: which model to ask. Without it, the model in use.</param>
    /// <param name="cancellationToken">Cancels the test.</param>
    /// <returns>The outcome in one sentence.</returns>
    [HttpPost("Test/{target}")]
    [Produces(MediaTypeNames.Application.Json)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> Test([FromRoute] string target, [FromBody] ModelTestRequest? request, CancellationToken cancellationToken)
    {
        var result = await _services.Settings.TestAsync(target, request?.ModelId, cancellationToken).ConfigureAwait(false);
        return result is null ? Problem("The plugin has no test of that name.", statusCode: StatusCodes.Status404NotFound) : Ok(result);
    }
}
