# Subtitle Retimer for Jellyfin

A Jellyfin plugin that gets subtitles for your shows and films and fixes their timing.

It adds one page to your Jellyfin server. The page lists your shows with their posters. A click on
a show lists its episodes, and from an episode you get a subtitle: the plugin searches the subtitle
sources, picks a file, checks its language, retimes it against the video and saves it next to the
video. Jellyfin lists the new subtitle at once, without a library scan.

It builds for **Jellyfin 12.4** (.NET 10) and installs through Jellyfin's own plugin installer.

## Install

1. In Jellyfin, open Dashboard → Plugins → Manage Repositories and add a new repository: the name
   `Subtitle Retimer` and this URL.

   ```
   https://raw.githubusercontent.com/friedrich-de/jellyfin-plugin-retimer/main/repository/manifest.json
   ```

2. Install **Subtitle Retimer** from the list of plugins (category Subtitles) and restart Jellyfin.
3. Open `http://<your Jellyfin>/Retimer`, then Settings, and put in the keys below.

### What you need

| | What for | |
| --- | --- | --- |
| [OpenRouter](https://openrouter.ai) key | The model that picks the file for an episode and checks its language. Well under a cent per subtitle with Haiku 5.5. | Required |
| [Jimaku](https://jimaku.cc) key | Japanese subtitles for anime and dramas. Free, from your Jimaku account page. | One source at least |
| [OpenSubtitles](https://www.opensubtitles.com) key | Subtitles in many languages. | One source at least |
| A [retimer-api](https://github.com/friedrich-de/retimer-api) server | Fixes the timing of a downloaded subtitle. You run it yourself, with Docker next to Jellyfin; its README has the steps. | Optional |

Without Retimer API the plugin still finds, checks and saves subtitles; it just does not retime them.

## What it does

| Part | What it does |
| --- | --- |
| Your shows | `http://<your Jellyfin>/Retimer`. The libraries, shows and films your Jellyfin user may see, each with how many videos have a subtitle. |
| A show | Its episodes with their state: no subtitle, a subtitle saved by the plugin (retimed or not), a subtitle that was already there. "Get subtitle" on one episode, "Get all missing" for the show. |
| Choose a file | Every file the sources list for the show, the ones for this episode first. For when the plugin's own choice is wrong, or to search a source under another title. |
| Activity | The jobs, one after the other, with their steps, their result and what they cost. |
| Settings | Sources and their keys, Retimer API, the AI model, which libraries appear, who may fetch, and limits for a day. Administrators only. |

### How a subtitle is fetched

1. **Search.** Every source that is switched on is asked for the show: by AniList, TMDB or IMDb id
   where the source takes one, else by title. Sources: [Jimaku](https://jimaku.cc) (Japanese, for
   anime and dramas) and [OpenSubtitles](https://www.opensubtitles.com) (many languages).
2. **Pick.** A model, called through [OpenRouter](https://openrouter.ai) with your key, chooses the
   source's entry for the show and matches the listed files to the season's episodes. It only ever
   chooses among files the sources list. The model is required: the plugin picks no file without
   one. With no key or no model in the settings, "Get subtitle" and "Get all missing" are off and
   the page says what is missing. When the model gives no answer, the job fails and can be tried
   again. Choosing a file yourself always works.
   What a name plainly says stands over the model's order: a plain file goes before one with
   furigana or for the hearing impaired, and those before one that names a second language.
3. **Check the language.** A sample of the downloaded file is checked by its script, and by the
   model for languages that share a script. A file in another language is skipped and the next one
   is tried, three files at most. The plugin remembers the release for the show, so its files for
   the other episodes are tried last and the picker says why. A file the plugin picked is not
   saved when the model cannot read it. A file you chose is.
4. **Retime**, if Retimer API is switched on. The plugin cuts the text subtitle tracks out of the
   video with Jellyfin's ffmpeg and sends them with the downloaded subtitle, six at most in one
   job. The tracks in the subtitle's own language and the English ones go first, and a plain track
   before captions for the hearing impaired, which often follow a dubbed audio. The video's other
   tracks are sent only when none of those fits. Retimer API chooses the track that fits and moves
   the subtitle's lines to it. Every line stays: lines the track does not have, such as the songs
   of the opening and the ending, move along with the rest, and only the clean-up switches in the
   settings take lines out. The video's audio is sent never, only when no track fits, or always,
   as you set it. Picture subtitles (PGS), in the video or from a source, can be read into text by
   Retimer API first.
5. **Save.** The subtitle goes through Jellyfin's own subtitle upload, as `<video>.<language>.srt`
   or `.ass` next to the video, and the plugin refreshes that one item.

Retimer API is a separate service that you run yourself:
[retimer-api](https://github.com/friedrich-de/retimer-api), on the Jellyfin machine or another one.
It is **off until you switch it on**; without it a subtitle is saved as downloaded.

The subtitle language is one setting for the server, and each show can use another one, set on the
show's page.

### What leaves your server

| To | What | When |
| --- | --- | --- |
| The subtitle sources | The show's ids and title, and the downloads | On every search |
| OpenRouter | The show's title, year and season, the episodes with the names of their video files (no folders), the sources' entry and file names, and up to 45 lines of a downloaded subtitle | When a model is set |
| Retimer API | The downloaded subtitle, the video's subtitle tracks with their language and title, and the audio if you allow it | When Retimer API is on |

No video leaves the server. Only the audio does, and only if you allow it.

### Cost and limits

Each job records what OpenRouter and Retimer API report as its cost, and the page shows it. Two
limits for a day, both in the settings, stop new jobs once they are reached: the number of jobs
(200 by default) and the cost (2 US dollars by default). A job counts when it starts to work, so
one that is cancelled while it waits does not. The day ends at midnight UTC. "Get all missing"
asks first and says what it will do.

## How the pages work

Jellyfin's web client shows a plugin's own pages only to administrators. So the plugin serves its
page itself at `/Retimer`. The page has no login of its own: it reuses the login that Jellyfin's
web client keeps in the browser for the same address, and calls the plugin's endpoints with it.
Without a login it shows a link to Jellyfin, and goes on by itself once you sign in there. The page
is served with a Content-Security-Policy that allows only its own script, styles, images, app
manifest and service worker, and nothing is written into it on the server.

### Install the page as an app

Chrome, Edge and other browsers built on Chromium can install the page as an app, on the computer
and on Android: open `https://<your Jellyfin>/Retimer` and choose "Install" in the address bar or
in the browser's menu. Browsers install only pages opened over HTTPS or at `localhost`, so a
Jellyfin reached as `http://<address on your network>` is not offered. Firefox on the computer
does not install pages. Safari on iPhone and iPad can add the page to the home screen, which has
not been tried yet.

The app opens in its own window with the plugin's icon. It shares the browser's login: sign in to
Jellyfin in the browser and the app is signed in too, without being opened again. "Back to
Jellyfin" opens Jellyfin in the browser.

The page brings a web app manifest (`/Retimer/manifest.webmanifest`), its icons
(`/Retimer/icons/`) and a service worker (`/Retimer/sw.js`). Like the page itself, these answer
without a login and hold no data. The service worker keeps a copy of the page and its files only,
so the app still opens, and says that the server cannot be reached, while it is offline; every
other request goes to the server and is never stored. The icons are drawn from the SVG files in
`Jellyfin.Plugin.Retimer/Web/icons/`; after changing one, run `node dev/render-icons.js`.

The plugin's entry in the dashboard (Dashboard → Plugins → Subtitle Retimer) links to the page
and to its settings. To put the page in the side menu for everyone, an administrator can add it to
`menuLinks` in the web client's `config.json`.

## Who can do what

| | Without login | Signed-in user | May manage subtitles | Administrator |
| --- | --- | --- | --- | --- |
| The page and its files, app manifest, icons and service worker (no data in them) | yes | yes | yes | yes |
| See shows, episodes, states and jobs of what Jellyfin lets them see | no | yes | yes | yes |
| Get, choose, retime or remove a subtitle, set a show's language | no | no | yes | yes |
| See today's use and what a job cost | no | no | yes | yes |
| Settings, connection checks, clearing the activity list | no | no | no | yes |

"May manage subtitles" is the right of that name in a Jellyfin user's profile. The setting "Who may
fetch" can open fetching to every signed-in user. A job can be cancelled by whoever started it and
by administrators. Only a subtitle the plugin saved itself can be removed through it.

The keys and the OpenSubtitles password are stored by Jellyfin in
`plugins/configurations/Jellyfin.Plugin.Retimer.xml`, encrypted (values that start with `enc:v1:`).
They are read back only for the call that sends them. The secret that encrypts them is
`data/subtitle-retimer/settings.key` in Jellyfin's data folder, readable only by the user Jellyfin
runs as. So a search of the server's files, a copy of its settings folder or Jellyfin's own
`GET /Plugins/{id}/Configuration` (administrators only) shows no key. Whoever can read both files
can still recover the keys, as can whoever administers the machine. Copying the settings file to
another server without that secret loses the keys: enter them again there. The plugin's endpoints
never return a key: the settings say only whether one is stored.

## Not in this version

- A watch mode that fetches subtitles for new episodes by itself.
- More sources. SubDL was looked at and left out, because its API could not be verified.
- OpenSubtitles is built from its published API description and from open-source clients. It has
  unit tests, and has not run against the live service yet.

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
plugin repository through Jellyfin's installer and restarts the server. It makes three small
episodes with ffmpeg, two of them with two English subtitle tracks, and gives them to the server as
a library. Then it calls the endpoints as nobody, as a normal user and as an administrator, and
fetches, retimes, saves and removes a subtitle. To use a Jellyfin that is already installed:

```sh
RETIMER_E2E_JELLYFIN=/usr/bin/jellyfin RETIMER_E2E_JELLYFIN_WEB=/usr/share/jellyfin/web \
  RETIMER_E2E_FFMPEG=/usr/lib/jellyfin-ffmpeg/ffmpeg dev/e2e.sh
```

The throwaway server calls no outside service. The tests play Jimaku, OpenRouter and Retimer API
themselves on a local port, in the shapes of those services' published API descriptions, and point
the plugin at them with the environment variables `RETIMER_JIMAKU_API_URL` and
`RETIMER_OPENROUTER_API_URL` and with the address of Retimer API in the settings.
`RETIMER_OPENSUBTITLES_API_URL` does the same for OpenSubtitles. These variables exist for tests
only: the plugin reads them once at start. Leave them unset on a real server.

The page itself was checked by hand in Chromium against the same kind of server; no test opens a
browser.

One more end-to-end test searches the real jimaku.cc. It runs only when `JIMAKU_API_KEY` is set.
The script builds everything before the tests and keeps the key away from the build, because the
compiler's helper process can outlive a build together with its environment.

## Package and publish

```sh
dev/package.sh [BASE_URL]      # a test repository in dist/
dev/package.sh --publish       # this version added to the public repository in repository/
```

`dev/package.sh` writes `dist/subtitle-retimer_<version>.zip` and `dist/manifest.json`, a Jellyfin
plugin repository with that one build. Serve `dist/` at `BASE_URL` (default
`http://127.0.0.1:8765`), add `BASE_URL/manifest.json` as a repository in Jellyfin, install
"Subtitle Retimer" from the catalog and restart Jellyfin. `dev/e2e.sh` does the same with a
throwaway server.

`dev/package.sh --publish` releases a version. It builds the zip into `repository/`, and adds the
version to `repository/manifest.json` with its section of [CHANGELOG.md](CHANGELOG.md) as the
changelog, keeping the versions before it. Jellyfin reads that manifest from the main branch on
GitHub, so the version is out once the commit that adds it is merged. A version that is in the
manifest already is refused: raise `<Version>` first. To release:

1. Raise `<Version>` in `Directory.Build.props` and add its section to `CHANGELOG.md`.
2. Run `dotnet build`, `dotnet test` and `dev/e2e.sh`.
3. Run `dev/package.sh --publish`, commit `repository/` and merge it.

The version lives in `Directory.Build.props`, together with the Jellyfin version the plugin is
built against. A plugin built for 12.4 does not load on Jellyfin 10.11, which runs on .NET 9.

## Layout

| Path | Content |
| --- | --- |
| `Jellyfin.Plugin.Retimer/` | The plugin: `Configuration/` (settings), `Sources/` and `Jimaku/` (subtitle sources), `Picking/` (model, rules, language check), `Retimer/` (Retimer API client), `Library/` (what the plugin reads from and writes to Jellyfin), `Jobs/` (the job, the queue, what is remembered), `Api/` (endpoints), `Web/` (the page) |
| `Jellyfin.Plugin.Retimer.Tests/` | Unit tests. Nothing in them leaves the process. |
| `Jellyfin.Plugin.Retimer.E2E/` | End-to-end tests against a real server |
| `dev/` | `package.sh`, `e2e.sh`, `render-icons.js` (the page's PNG icons from their SVG files) |
| `repository/` | The public plugin repository: `manifest.json` and the zip of each released version |

## Licence

GPL-3.0, see [LICENSE](LICENSE). A Jellyfin plugin links Jellyfin's GPL-3.0 packages, so it has to
be.
