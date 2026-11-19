# Changelog

Each section is the changelog Jellyfin shows for that version: `dev/package.sh --publish`
copies it into the repository manifest.

## 0.4.0.0

The first public release, for Jellyfin 12.4.

- A page at /Retimer for every signed-in user: your shows with their posters, a show's episodes with their subtitle state, a file picker, the activity of jobs, and settings for administrators.
- Gets subtitles from Jimaku and OpenSubtitles. A model through OpenRouter (Haiku 5.5 by default, with your key) picks the entry, matches files to episodes and checks the language.
- Retimes the subtitle with your own Retimer API server against the video's subtitle tracks or its audio, and can read picture subtitles (PGS) into text.
- Saves the subtitle next to the video, and Jellyfin lists it at once.
- Daily limits on jobs and cost.
