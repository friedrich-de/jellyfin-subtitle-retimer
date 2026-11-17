using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Mime;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Retimer.Configuration;
using Jellyfin.Plugin.Retimer.Jimaku;
using Jellyfin.Plugin.Retimer.Retimer;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.Retimer.Api;

/// <summary>
/// The plugin's own HTTP endpoints, under <c>/Retimer</c> at the Jellyfin address.
/// Every signed-in user may search. Only administrators reach the connection tests, and no endpoint returns a key.
/// </summary>
[ApiController]
[Route("Retimer")]
[Authorize]
public class RetimerController : ControllerBase
{
    /// <summary>
    /// What the page for all users may do: run its own inline script and styles, and call this server.
    /// </summary>
    internal const string UserPageContentSecurityPolicy =
        "default-src 'none'; script-src 'unsafe-inline'; style-src 'unsafe-inline'; connect-src 'self'; "
        + "frame-ancestors 'none'; base-uri 'none'; form-action 'none'";

    private readonly IPluginSettings _settings;
    private readonly IJimakuClient _jimaku;
    private readonly IRetimerClient _retimer;

    /// <summary>
    /// Initializes a new instance of the <see cref="RetimerController"/> class.
    /// </summary>
    /// <param name="settings">The plugin's settings.</param>
    /// <param name="jimaku">The Jimaku client.</param>
    /// <param name="retimer">The retimer client.</param>
    public RetimerController(IPluginSettings settings, IJimakuClient jimaku, IRetimerClient retimer)
    {
        _settings = settings;
        _jimaku = jimaku;
        _retimer = retimer;
    }

    /// <summary>
    /// Serves the page for all users. The page itself holds no data; it signs in with the
    /// Jellyfin login the browser already has and then calls the endpoints below.
    /// </summary>
    /// <returns>The page.</returns>
    [HttpGet("")]
    [AllowAnonymous]
    [Produces(MediaTypeNames.Text.Html)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult GetUserPage()
    {
        var stream = typeof(Plugin).Assembly.GetManifestResourceStream(Plugin.UserPageResource);
        if (stream is null)
        {
            return NotFound();
        }

        // The page changes with the plugin version, so the browser should always ask again.
        Response.Headers.CacheControl = "no-cache";
        Response.Headers.XContentTypeOptions = "nosniff";

        // The page reads the Jellyfin login from the browser, so it must not load anything from
        // elsewhere, sit inside another site's frame, or tell Jimaku where it was opened from.
        Response.Headers.ContentSecurityPolicy = UserPageContentSecurityPolicy;
        Response.Headers["Referrer-Policy"] = "no-referrer";
        return File(stream, "text/html; charset=utf-8");
    }

    /// <summary>
    /// Says what is set up, without any key.
    /// </summary>
    /// <returns>The status.</returns>
    [HttpGet("Status")]
    [Produces(MediaTypeNames.Application.Json)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<StatusResponse> GetStatus()
    {
        var configuration = _settings.Current;
        return new StatusResponse
        {
            Version = typeof(Plugin).Assembly.GetName().Version?.ToString() ?? "0",
            JimakuConfigured = !string.IsNullOrWhiteSpace(configuration.JimakuApiKey),
            RetimerConfigured = !string.IsNullOrWhiteSpace(configuration.RetimerUrl) && !string.IsNullOrWhiteSpace(configuration.RetimerApiKey),
            IsAdministrator = User.IsInRole("Administrator")
        };
    }

    /// <summary>
    /// Finds Jimaku entries: by AniList id first, then by TMDB id, then by title.
    /// </summary>
    /// <param name="anilistId">The AniList id.</param>
    /// <param name="tmdbId">The TMDB id as <c>tv:123</c> or <c>movie:123</c>.</param>
    /// <param name="query">The title to search for.</param>
    /// <param name="anime">Search anime (<c>true</c>) or live action (<c>false</c>). Jimaku's default is anime.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The matching entries.</returns>
    [HttpGet("Jimaku/Entries")]
    [Produces(MediaTypeNames.Application.Json)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<IReadOnlyList<JimakuEntry>>> FindEntries(
        [FromQuery] int? anilistId,
        [FromQuery] string? tmdbId,
        [FromQuery] string? query,
        [FromQuery] bool? anime,
        CancellationToken cancellationToken)
    {
        if (anilistId is null && string.IsNullOrWhiteSpace(tmdbId) && string.IsNullOrWhiteSpace(query))
        {
            return Problem("Give an AniList id, a TMDB id or a title.", statusCode: StatusCodes.Status400BadRequest);
        }

        if (anilistId is < 0)
        {
            return Problem("An AniList id cannot be negative.", statusCode: StatusCodes.Status400BadRequest);
        }

        tmdbId = string.IsNullOrWhiteSpace(tmdbId) ? null : tmdbId.Trim();
        if (tmdbId is not null && !JimakuTmdbId.IsValid(tmdbId))
        {
            return Problem("A TMDB id must look like tv:123 or movie:123.", statusCode: StatusCodes.Status400BadRequest);
        }

        try
        {
            var lookup = new JimakuEntryQuery { AnilistId = anilistId, TmdbId = tmdbId, Title = query, Anime = anime };
            return Ok(await _jimaku.FindEntriesAsync(lookup, cancellationToken).ConfigureAwait(false));
        }
        catch (JimakuException ex)
        {
            return JimakuProblem(ex);
        }
    }

    /// <summary>
    /// Lists the files of a Jimaku entry.
    /// </summary>
    /// <param name="entryId">The entry's id on Jimaku.</param>
    /// <param name="episode">Keep only files for this episode.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The files.</returns>
    [HttpGet("Jimaku/Entries/{entryId:long:min(0)}/Files")]
    [Produces(MediaTypeNames.Application.Json)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<IReadOnlyList<JimakuFile>>> GetFiles(
        [FromRoute] long entryId,
        [FromQuery] int? episode,
        CancellationToken cancellationToken)
    {
        if (episode is < 0)
        {
            return Problem("An episode number cannot be negative.", statusCode: StatusCodes.Status400BadRequest);
        }

        try
        {
            return Ok(await _jimaku.GetFilesAsync(entryId, episode, cancellationToken).ConfigureAwait(false));
        }
        catch (JimakuException ex)
        {
            return JimakuProblem(ex);
        }
    }

    /// <summary>
    /// Checks the saved Jimaku key with one small search. Administrators only.
    /// </summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The outcome.</returns>
    [HttpPost("Test/Jimaku")]
    [Authorize(Policy = Policies.RequiresElevation)]
    [Produces(MediaTypeNames.Application.Json)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<ConnectionTestResponse>> TestJimaku(CancellationToken cancellationToken)
    {
        try
        {
            // AniList id 1 exists on AniList, so the call is valid whether or not Jimaku has files for it.
            await _jimaku.SearchEntriesAsync(new JimakuEntryQuery { AnilistId = 1 }, cancellationToken).ConfigureAwait(false);
            return new ConnectionTestResponse { Ok = true, Status = "Ok", Message = "Jimaku accepted the key." };
        }
        catch (JimakuException ex)
        {
            return new ConnectionTestResponse { Ok = false, Status = ex.Error.ToString(), Message = ex.Message };
        }
    }

    /// <summary>
    /// Checks the saved retimer address and key. It submits no job. Administrators only.
    /// </summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The outcome.</returns>
    [HttpPost("Test/Retimer")]
    [Authorize(Policy = Policies.RequiresElevation)]
    [Produces(MediaTypeNames.Application.Json)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<ConnectionTestResponse>> TestRetimer(CancellationToken cancellationToken)
    {
        var status = await _retimer.CheckConnectionAsync(cancellationToken).ConfigureAwait(false);
        var message = status switch
        {
            RetimerConnectionStatus.Ok => "The retimer answered and accepted the key.",
            RetimerConnectionStatus.NotConfigured => "Set the retimer's address and key first.",
            RetimerConnectionStatus.InvalidAddress => "The address must start with http:// or https://.",
            RetimerConnectionStatus.Unauthorized => "The retimer answered and rejected the key.",
            _ => "No retimer answered at that address."
        };

        return new ConnectionTestResponse { Ok = status == RetimerConnectionStatus.Ok, Status = status.ToString(), Message = message };
    }

    private ObjectResult JimakuProblem(JimakuException exception)
    {
        var statusCode = exception.Error switch
        {
            JimakuError.NotConfigured => StatusCodes.Status503ServiceUnavailable,
            JimakuError.NotFound => StatusCodes.Status404NotFound,
            JimakuError.BadRequest => StatusCodes.Status400BadRequest,
            JimakuError.RateLimited => StatusCodes.Status429TooManyRequests,

            // The key is the administrator's to fix; to the caller, the source is simply not usable.
            _ => StatusCodes.Status502BadGateway
        };

        if (exception.Error == JimakuError.RateLimited && exception.RetryAfter is { } wait)
        {
            Response.Headers.RetryAfter = Math.Ceiling(wait.TotalSeconds).ToString(CultureInfo.InvariantCulture);
        }

        return Problem(exception.Message, statusCode: statusCode, title: exception.Error.ToString());
    }
}
