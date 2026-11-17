# Subtitle Retimer for Jellyfin

A Jellyfin plugin that finds Japanese subtitles on [Jimaku](https://jimaku.cc) and, in later
versions, retimes them to the audio of your files through a retimer service.

This is the skeleton. It builds for **Jellyfin 12.4** (.NET 10), installs through Jellyfin's own
plugin installer, and has a settings page, a Jimaku search and a page for all signed-in users.
It does not download, save or retime subtitles yet.

## What is in it

| Part | What it does |
| --- | --- |
| Settings page | Dashboard → Plugins → Subtitle Retimer. Holds the Jimaku API key and the retimer's address and key, and can check both connections. Administrators only. |
| Jimaku search | Finds entries by AniList id, by TMDB id or by title, in that order, and lists an entry's files, optionally for one episode. Uses the key from the settings. |
| Page for all users | `http://<your Jellyfin>/Retimer`. Any signed-in user can search Jimaku there. |
| Retimer check | Confirms that the configured retimer answers and accepts the key. It submits no job. |

The retimer can be the hosted service or your own
[retimer-api](https://github.com/friedrich-de/retimer-api) server. Both take the same calls, so the
plugin has one address and one key for either.

## How the pages work

Jellyfin's web client shows a plugin's own pages only to administrators. In 12.4 the route for
plugin pages sits in the administrator part of the client, and the server refuses to list them to
anyone else. So the plugin has two kinds of page:

- **The settings page** is a normal Jellyfin plugin page, with an entry in the dashboard menu.
- **The page for all users** is served by the plugin itself at `/Retimer`. It has no login of its
  own. It reuses the login that Jellyfin's web client keeps in the browser for the same address,
  and calls the plugin's endpoints with it. Without a login it shows a link to Jellyfin.

Jellyfin's web client has no place where a plugin could add a link for normal users. To put one in
the side menu, an administrator can add the page to `menuLinks` in the web client's `config.json`.

## Who can do what

| Endpoint | Without login | Signed-in user | Administrator |
| --- | --- | --- | --- |
| `GET /Retimer` (the page, no data in it) | yes | yes | yes |
| `GET /Retimer/Status` (what is set up, never a key) | no | yes | yes |
| `GET /Retimer/Jimaku/Entries?anilistId=&tmdbId=&query=&anime=` | no | yes | yes |
| `GET /Retimer/Jimaku/Entries/{id}/Files?episode=` | no | yes | yes |
| `POST /Retimer/Test/Jimaku`, `POST /Retimer/Test/Retimer` | no | no | yes |
| Jellyfin's own `GET/POST /Plugins/{id}/Configuration` (the keys) | no | no | yes |

A TMDB id is written the way Jimaku writes it: `tv:123` or `movie:123`. Jimaku keeps anime and
live action apart: a search finds anime entries unless it says `anime=false`, and that holds for
a lookup by id too.

The keys are stored by Jellyfin in `plugins/configurations/Jellyfin.Plugin.Retimer.xml` on the
server, readable by whoever administers the machine. No endpoint of the plugin returns them.

Jimaku limits requests per address. The plugin waits out a limit of up to five seconds once and
otherwise passes it on with the time to wait.

## Build and test

You need the .NET 10 SDK. The end-to-end tests also need `zip` and ffmpeg, because Jellyfin does
not start without ffmpeg.

```sh
dotnet build -c Release        # warnings are errors
dotnet test -c Release         # unit tests; the end-to-end tests are skipped
dev/e2e.sh                     # end-to-end tests in a real Jellyfin 12.4 server
```

`dev/e2e.sh` downloads the Jellyfin 12.4 server once into `.cache/` (about 110 MB, x86-64 Linux),
packs the plugin, starts a throwaway server on a free local port, installs the plugin from a
plugin repository through Jellyfin's installer, restarts the server and then calls every endpoint
as nobody, as a normal user and as an administrator. To use a Jellyfin that is already installed:

```sh
RETIMER_E2E_JELLYFIN=/usr/bin/jellyfin RETIMER_E2E_JELLYFIN_WEB=/usr/share/jellyfin/web \
  RETIMER_E2E_FFMPEG=/usr/lib/jellyfin-ffmpeg/ffmpeg dev/e2e.sh
```

The throwaway server does not call jimaku.cc. The tests start a stand-in on the same local port
that answers in the shapes of Jimaku's published API description, and point the plugin at it with
the environment variable `RETIMER_JIMAKU_API_URL`. That variable exists for tests only: the plugin
reads it once at start and logs a warning when it is set. Leave it unset on a real server.

One more end-to-end test searches the real jimaku.cc. It runs only when `JIMAKU_API_KEY` is set.
The script builds everything before the tests and keeps the key away from the build, because the
compiler's helper process can outlive a build together with its environment.

## Package and install

```sh
dev/package.sh [BASE_URL]
```

writes `dist/subtitle-retimer_<version>.zip` and `dist/manifest.json`, a Jellyfin plugin
repository with that one plugin. Serve `dist/` at `BASE_URL` (default `http://127.0.0.1:8765`),
add `BASE_URL/manifest.json` under Dashboard → Plugins → Repositories, install "Subtitle Retimer"
from the catalog and restart Jellyfin. There is no public repository address yet.

The version lives in `Directory.Build.props`, together with the Jellyfin version the plugin is
built against. A plugin built for 12.4 does not load on Jellyfin 10.11, which runs on .NET 9.

## Layout

| Path | Content |
| --- | --- |
| `Jellyfin.Plugin.Retimer/` | The plugin: `Plugin.cs`, `Configuration/` (settings and their page), `Jimaku/` (client), `Retimer/` (connection check), `Api/` (endpoints), `Web/` (page for all users) |
| `Jellyfin.Plugin.Retimer.Tests/` | Unit tests. Nothing in them leaves the process. |
| `Jellyfin.Plugin.Retimer.E2E/` | End-to-end tests against a real server |
| `dev/` | `package.sh`, `e2e.sh` |

## Licence

GPL-3.0, see [LICENSE](LICENSE). A Jellyfin plugin links Jellyfin's GPL-3.0 packages, so it has to
be.
