# Subtitle Retimer for Jellyfin

A Jellyfin plugin that gets subtitles for your shows and films and fixes their timing.

It adds one page to your Jellyfin server. The page lists your shows with their posters. A click on
a show lists its episodes, and from an episode you get a subtitle: the plugin searches the subtitle
sources, picks a file, checks its language, retimes it against the video and saves it next to the
video. Jellyfin lists the new subtitle at once, without a library scan.

It builds for **Jellyfin 12.4** (.NET 10) and installs through Jellyfin's own plugin installer.

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

Retimer API is a separate service: the hosted one, or your own
[retimer-api](https://github.com/friedrich-de/retimer-api) server. Both take the same calls. It is
**off until you switch it on**; without it a subtitle is saved as downloaded.

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
Without a login it shows a link to Jellyfin. The page is served with a Content-Security-Policy that
allows only its own script, styles and images, and nothing is written into it on the server.

The plugin's entry in the dashboard (Dashboard → Plugins → Subtitle Retimer) links to the page
and to its settings. To put the page in the side menu for everyone, an administrator can add it to
`menuLinks` in the web client's `config.json`.

## Who can do what

| | Without login | Signed-in user | May manage subtitles | Administrator |
| --- | --- | --- | --- | --- |
| The page and its files (no data in them) | yes | yes | yes | yes |
| See shows, episodes, states and jobs of what Jellyfin lets them see | no | yes | yes | yes |
| Get, choose, retime or remove a subtitle, set a show's language | no | no | yes | yes |
| See today's use and what a job cost | no | no | yes | yes |
| Settings, connection checks, clearing the activity list | no | no | no | yes |

"May manage subtitles" is the right of that name in a Jellyfin user's profile. The setting "Who may
fetch" can open fetching to every signed-in user. A job can be cancelled by whoever started it and
by administrators. Only a subtitle the plugin saved itself can be removed through it.

The keys are stored by Jellyfin in `plugins/configurations/Jellyfin.Plugin.Retimer.xml` on the
server, readable by whoever administers the machine. The plugin's endpoints never return a key: the
settings say only whether one is stored. Jellyfin's own `GET /Plugins/{id}/Configuration` returns
them to administrators, as for every plugin.

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
| `Jellyfin.Plugin.Retimer/` | The plugin: `Configuration/` (settings), `Sources/` and `Jimaku/` (subtitle sources), `Picking/` (model, rules, language check), `Retimer/` (Retimer API client), `Library/` (what the plugin reads from and writes to Jellyfin), `Jobs/` (the job, the queue, what is remembered), `Api/` (endpoints), `Web/` (the page) |
| `Jellyfin.Plugin.Retimer.Tests/` | Unit tests. Nothing in them leaves the process. |
| `Jellyfin.Plugin.Retimer.E2E/` | End-to-end tests against a real server |
| `dev/` | `package.sh`, `e2e.sh` |

## Licence

GPL-3.0, see [LICENSE](LICENSE). A Jellyfin plugin links Jellyfin's GPL-3.0 packages, so it has to
be.
