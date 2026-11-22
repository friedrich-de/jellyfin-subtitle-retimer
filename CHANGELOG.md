# Changelog

Each section is the changelog Jellyfin shows for that version: `dev/package.sh --publish`
copies it into the repository manifest.

## 0.5.0.1

The project's public home.

- The plugin lives at github.com/friedrich-de/jellyfin-subtitle-retimer and Retimer API at github.com/friedrich-de/subtitle-retimer. The settings page links there.
- Requests to Jimaku and OpenSubtitles name the plugin `jellyfin-subtitle-retimer`.

## 0.5.0.0

The page can be installed as an app.

- Chrome, Edge and other browsers built on Chromium offer to install the page at /Retimer as an app. It opens in its own window with its own icon. Installing needs the page to be opened over HTTPS, or at localhost.
- The page has an icon of its own in the browser tab: a stopwatch over two subtitle lines.
- The installed app opens Jellyfin in the browser, and goes on by itself once you sign in to Jellyfin there.
- A service worker keeps a copy of the page's own files, so the app still opens and says so when the server cannot be reached. It never keeps an answer of the server.

## 0.4.1.0

Keys are stored encrypted.

- The API keys and the OpenSubtitles password are encrypted in the plugin's settings file and read back only for the call that sends them. The secret that encrypts them is a file of its own in Jellyfin's data folder (`data/subtitle-retimer/settings.key`), readable only by the user Jellyfin runs as. Keys stored in plain by 0.4.0.0 are encrypted at the first start.
- Settings copied from another server lose their keys, because only the server that encrypted them can read them: enter them again on the settings screen. The same goes for a step back to 0.4.0.0.
- The page keeps every call on its own server, whatever address it was opened under, and OpenSubtitles may only move the login to one of its own hosts.

## 0.4.0.0

The first public release, for Jellyfin 12.4.

- A page at /Retimer for every signed-in user: your shows with their posters, a show's episodes with their subtitle state, a file picker, the activity of jobs, and settings for administrators.
- Gets subtitles from Jimaku and OpenSubtitles. A model through OpenRouter (Haiku 5.5 by default, with your key) picks the entry, matches files to episodes and checks the language.
- Retimes the subtitle with your own Retimer API server against the video's subtitle tracks or its audio, and can read picture subtitles (PGS) into text.
- Saves the subtitle next to the video, and Jellyfin lists it at once.
- Daily limits on jobs and cost.
