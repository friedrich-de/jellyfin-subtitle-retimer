# Changelog

Each section is the changelog Jellyfin shows for that version: `dev/package.sh --publish`
copies it into the repository manifest.

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
