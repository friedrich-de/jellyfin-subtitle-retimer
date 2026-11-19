using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Net.Mime;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Retimer.Api.Models;
using Jellyfin.Plugin.Retimer.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.Retimer.Api;

/// <summary>
/// The plugin's page and what it calls, under <c>/Retimer</c> at the Jellyfin address.
/// Every signed-in user sees the shows of the libraries they may see in Jellyfin. Getting, replacing and
/// removing subtitles needs the right the settings name. No endpoint returns a key.
/// </summary>
[ApiController]
[Route("Retimer")]
[Authorize]
public class RetimerController : ControllerBase
{
    /// <summary>
    /// What the page may do: run its own script and styles, show pictures from this server, call this server,
    /// and read its own app manifest and service worker so the browser can install it as an app.
    /// </summary>
    internal const string UserPageContentSecurityPolicy =
        "default-src 'none'; script-src 'self'; style-src 'self'; img-src 'self'; connect-src 'self'; "
        + "manifest-src 'self'; worker-src 'self'; "
        + "frame-ancestors 'none'; base-uri 'none'; form-action 'none'";

    /// <summary>
    /// The page's icons by file name, with their type. Nothing else is served under <c>icons/</c>.
    /// </summary>
    internal static readonly FrozenDictionary<string, string> Icons = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["favicon.svg"] = "image/svg+xml",
        ["favicon-32.png"] = "image/png",
        ["apple-touch-icon.png"] = "image/png",
        ["icon-192.png"] = "image/png",
        ["icon-512.png"] = "image/png",
        ["icon-maskable-192.png"] = "image/png",
        ["icon-maskable-512.png"] = "image/png",
    }.ToFrozenDictionary(StringComparer.Ordinal);

    private readonly PluginServices _services;

    /// <summary>
    /// Initializes a new instance of the <see cref="RetimerController"/> class.
    /// </summary>
    /// <param name="services">The plugin's services.</param>
    public RetimerController(PluginServices services)
    {
        _services = services;
    }

    /// <summary>
    /// Serves the page. The page itself holds no data; it signs in with the Jellyfin login the
    /// browser already has and then calls the endpoints below.
    /// </summary>
    /// <returns>The page.</returns>
    [HttpGet("")]
    [AllowAnonymous]
    [Produces(MediaTypeNames.Text.Html)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status302Found)]
    public ActionResult GetUserPage()
    {
        // The page links its script and styles relative to its own address, which only works
        // without a slash at the end. The browser keeps the part after # across the redirect.
        var request = HttpContext?.Request;
        if (request?.Path.Value?.EndsWith('/') == true)
        {
            return Redirect("../Retimer" + request.QueryString);
        }

        return Resource(Plugin.UserPageResource, "text/html; charset=utf-8");
    }

    /// <summary>
    /// Serves the page's script.
    /// </summary>
    /// <returns>The script.</returns>
    [HttpGet("app.js")]
    [AllowAnonymous]
    [Produces("text/javascript")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult GetScript() => Resource(Plugin.UserScriptResource, "text/javascript; charset=utf-8");

    /// <summary>
    /// Serves the page's styles.
    /// </summary>
    /// <returns>The style sheet.</returns>
    [HttpGet("app.css")]
    [AllowAnonymous]
    [Produces("text/css")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult GetStyles() => Resource(Plugin.UserStylesResource, "text/css; charset=utf-8");

    /// <summary>
    /// Serves the page's web app manifest: its name, colours and icons, so a browser can install it as an app.
    /// </summary>
    /// <returns>The manifest.</returns>
    [HttpGet("manifest.webmanifest")]
    [AllowAnonymous]
    [Produces("application/manifest+json")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult GetManifest() => Resource(Plugin.UserManifestResource, "application/manifest+json; charset=utf-8");

    /// <summary>
    /// Serves the page's service worker. It keeps a copy of the page and its files, which are the same for
    /// everyone, and leaves every other request alone.
    /// </summary>
    /// <returns>The service worker's script.</returns>
    [HttpGet("sw.js")]
    [AllowAnonymous]
    [Produces("text/javascript")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult GetWorker()
    {
        // The script sits at <base>/Retimer/sw.js but looks after the page at <base>/Retimer, one level up.
        // The browser allows that only when the script says so. The address is relative to the script,
        // so it holds behind a reverse proxy with a base path too.
        Response.Headers["Service-Worker-Allowed"] = "../Retimer";
        return Resource(Plugin.UserWorkerResource, "text/javascript; charset=utf-8");
    }

    /// <summary>
    /// Serves one of the page's icons: the browser tab's icon, and the app's icons once it is installed.
    /// </summary>
    /// <param name="name">The icon's file name.</param>
    /// <returns>The icon, or 404 for a name that is not one of them.</returns>
    [HttpGet("icons/{name}")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult GetIcon([FromRoute] string name)
        => Icons.TryGetValue(name, out var type) ? Resource(Plugin.IconResourcePrefix + name, type) : NotFound();

    /// <summary>
    /// Says who is signed in, what they may do and what is set up, without any key.
    /// </summary>
    /// <returns>The status.</returns>
    [HttpGet("Status")]
    [Produces(MediaTypeNames.Application.Json)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult> GetStatus()
    {
        var caller = await CallerAsync().ConfigureAwait(false);
        return caller is null ? NoUser() : Ok(_services.Page.Status(caller));
    }

    /// <summary>
    /// Lists the libraries of shows and films the user may see.
    /// </summary>
    /// <returns>The libraries.</returns>
    [HttpGet("Libraries")]
    [Produces(MediaTypeNames.Application.Json)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult> GetLibraries()
    {
        var caller = await CallerAsync().ConfigureAwait(false);
        return caller is null ? NoUser() : Ok(_services.Page.Libraries(caller));
    }

    /// <summary>
    /// Lists a library's shows and films, with how many of their videos have a subtitle in their language.
    /// </summary>
    /// <param name="libraryId">The library.</param>
    /// <returns>The shows and films.</returns>
    [HttpGet("Libraries/{libraryId:guid}/Shows")]
    [Produces(MediaTypeNames.Application.Json)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> GetShows([FromRoute] Guid libraryId)
    {
        var caller = await CallerAsync().ConfigureAwait(false);
        if (caller is null)
        {
            return NoUser();
        }

        var shows = _services.Page.Shows(caller, libraryId);
        return shows is null ? Problem("That library is not one you can see here.", statusCode: StatusCodes.Status404NotFound) : Ok(shows);
    }

    /// <summary>
    /// Describes one show or film: its episodes and what is known about each one's subtitle.
    /// </summary>
    /// <param name="showId">The show or film.</param>
    /// <returns>The show.</returns>
    [HttpGet("Shows/{showId:guid}")]
    [Produces(MediaTypeNames.Application.Json)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> GetShow([FromRoute] Guid showId)
    {
        var caller = await CallerAsync().ConfigureAwait(false);
        if (caller is null)
        {
            return NoUser();
        }

        var show = _services.Page.Show(caller, showId);
        return show is null ? Problem("That show is not in a library you can see.", statusCode: StatusCodes.Status404NotFound) : Ok(show);
    }

    /// <summary>
    /// Sets the subtitle language of one show or film.
    /// </summary>
    /// <param name="showId">The show or film.</param>
    /// <param name="request">The language.</param>
    /// <returns>Nothing.</returns>
    [HttpPut("Shows/{showId:guid}/Language")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> SetLanguage([FromRoute] Guid showId, [FromBody] SetLanguageRequest request)
    {
        var caller = await CallerAsync().ConfigureAwait(false);
        if (caller is null)
        {
            return NoUser();
        }

        var error = _services.Page.SetLanguage(caller, showId, request.Language);
        return error is null ? NoContent() : Refuse(error);
    }

    /// <summary>
    /// Lists the jobs that changed since the page last asked.
    /// </summary>
    /// <param name="since">The counter of the last answer, or 0 for every job.</param>
    /// <returns>The jobs and the new counter.</returns>
    [HttpGet("Jobs")]
    [Produces(MediaTypeNames.Application.Json)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult> GetJobs([FromQuery] long since = 0)
    {
        var caller = await CallerAsync().ConfigureAwait(false);
        return caller is null ? NoUser() : Ok(_services.Page.Jobs(caller, since));
    }

    /// <summary>
    /// Puts jobs in the line: one for each named video.
    /// </summary>
    /// <param name="request">The videos and what to do for them.</param>
    /// <returns>How many jobs were started.</returns>
    [HttpPost("Jobs")]
    [Produces(MediaTypeNames.Application.Json)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult> StartJobs([FromBody] StartJobsRequest request)
    {
        var caller = await CallerAsync().ConfigureAwait(false);
        if (caller is null)
        {
            return NoUser();
        }

        var (view, error) = _services.Page.Start(caller, request);
        return error is null ? Ok(view) : Refuse(error);
    }

    /// <summary>
    /// Cancels a job that waits or runs.
    /// </summary>
    /// <param name="jobId">The job.</param>
    /// <returns>Nothing.</returns>
    [HttpDelete("Jobs/{jobId}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult> CancelJob([FromRoute] string jobId)
    {
        var caller = await CallerAsync().ConfigureAwait(false);
        if (caller is null)
        {
            return NoUser();
        }

        var error = _services.Page.Cancel(caller, jobId);
        return error is null ? NoContent() : Refuse(error);
    }

    /// <summary>
    /// Starts a finished job again.
    /// </summary>
    /// <param name="jobId">The job.</param>
    /// <returns>How many jobs were started.</returns>
    [HttpPost("Jobs/{jobId}/Retry")]
    [Produces(MediaTypeNames.Application.Json)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> RetryJob([FromRoute] string jobId)
    {
        var caller = await CallerAsync().ConfigureAwait(false);
        if (caller is null)
        {
            return NoUser();
        }

        var (view, error) = _services.Page.Retry(caller, jobId);
        return error is null ? Ok(view) : Refuse(error);
    }

    /// <summary>
    /// Empties the list of finished jobs. Only administrators may.
    /// </summary>
    /// <returns>Nothing.</returns>
    [HttpDelete("Jobs")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult> ClearJobs()
    {
        var caller = await CallerAsync().ConfigureAwait(false);
        if (caller is null)
        {
            return NoUser();
        }

        var error = _services.Page.ClearFinished(caller);
        return error is null ? NoContent() : Refuse(error);
    }

    /// <summary>
    /// Lists the files the sources have for a video, best first, for choosing one by hand.
    /// </summary>
    /// <param name="itemId">The episode or film.</param>
    /// <param name="cancellationToken">Cancels the search.</param>
    /// <returns>The files.</returns>
    [HttpGet("Items/{itemId:guid}/Files")]
    [Produces(MediaTypeNames.Application.Json)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult> GetFiles([FromRoute] Guid itemId, CancellationToken cancellationToken)
    {
        var caller = await CallerAsync().ConfigureAwait(false);
        if (caller is null)
        {
            return NoUser();
        }

        try
        {
            var (view, error) = await _services.Page.FilesAsync(caller, itemId, cancellationToken).ConfigureAwait(false);
            return error is null ? Ok(view) : Refuse(error);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return ClientLeft();
        }
    }

    /// <summary>
    /// Searches the sources for a show by a title the user typed, when the plugin matched the wrong one.
    /// </summary>
    /// <param name="itemId">The episode or film the search is for.</param>
    /// <param name="query">The title.</param>
    /// <param name="cancellationToken">Cancels the search.</param>
    /// <returns>The entries the sources have.</returns>
    [HttpGet("Items/{itemId:guid}/Entries")]
    [Produces(MediaTypeNames.Application.Json)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> SearchEntries([FromRoute] Guid itemId, [FromQuery] string? query, CancellationToken cancellationToken)
    {
        var caller = await CallerAsync().ConfigureAwait(false);
        if (caller is null)
        {
            return NoUser();
        }

        try
        {
            var (entries, error) = await _services.Page.EntriesAsync(caller, itemId, query, cancellationToken).ConfigureAwait(false);
            return error is null ? Ok(entries) : Refuse(error);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return ClientLeft();
        }
    }

    /// <summary>
    /// Says which entry of a source belongs to the video's show and season.
    /// </summary>
    /// <param name="itemId">An episode of the season, or the film.</param>
    /// <param name="request">The entry, as a search returned it.</param>
    /// <returns>Nothing.</returns>
    [HttpPut("Items/{itemId:guid}/Entry")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> PinEntry([FromRoute] Guid itemId, [FromBody] PinEntryRequest request)
    {
        var caller = await CallerAsync().ConfigureAwait(false);
        if (caller is null)
        {
            return NoUser();
        }

        var error = _services.Page.PinEntry(caller, itemId, request);
        return error is null ? NoContent() : Refuse(error);
    }

    /// <summary>
    /// Removes the subtitle the plugin saved for a video.
    /// </summary>
    /// <param name="itemId">The episode or film.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>Nothing.</returns>
    [HttpDelete("Items/{itemId:guid}/Subtitle")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult> RemoveSubtitle([FromRoute] Guid itemId, CancellationToken cancellationToken)
    {
        var caller = await CallerAsync().ConfigureAwait(false);
        if (caller is null)
        {
            return NoUser();
        }

        var error = await _services.Page.RemoveSubtitleAsync(caller, itemId, cancellationToken).ConfigureAwait(false);
        return error is null ? NoContent() : Refuse(error);
    }

    private Task<Caller?> CallerAsync() => _services.Callers.ResolveAsync(HttpContext);

    private ObjectResult NoUser() => Problem("Sign in to Jellyfin as a user to use this page.", statusCode: StatusCodes.Status403Forbidden);

    private ObjectResult Refuse(ServiceError error) => Problem(error.Message, statusCode: error.Status);

    /// <summary>
    /// The answer to a request whose browser left before it was done. Nobody reads it, and it is no error of the server.
    /// </summary>
    private StatusCodeResult ClientLeft() => StatusCode(499);

    private ActionResult Resource(string name, string contentType)
    {
        var stream = typeof(Plugin).Assembly.GetManifestResourceStream(name);
        if (stream is null)
        {
            return NotFound();
        }

        // The files change with the plugin version, so the browser should always ask again.
        Response.Headers.CacheControl = "no-cache";
        Response.Headers.XContentTypeOptions = "nosniff";

        // The page reads the Jellyfin login from the browser, so it must not load anything from
        // elsewhere, sit inside another site's frame, or tell another site where it was opened from.
        Response.Headers.ContentSecurityPolicy = UserPageContentSecurityPolicy;
        Response.Headers["Referrer-Policy"] = "no-referrer";
        return File(stream, contentType);
    }
}
