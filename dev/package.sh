#!/usr/bin/env bash
# Builds the plugin and packs it the way Jellyfin installs plugins: one zip, plus a
# repository manifest that points at it.
#
#   dev/package.sh [BASE_URL]
#
# BASE_URL is where dist/ will be served from (default http://127.0.0.1:8765). Jellyfin
# downloads the zip from BASE_URL/<zip name> and checks it against the manifest's checksum.
# Output: dist/subtitle-retimer_<version>.zip and dist/manifest.json.
set -euo pipefail

cd "$(dirname "$0")/.."
base_url="${1:-http://127.0.0.1:8765}"
base_url="${base_url%/}"

version="$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' Directory.Build.props)"
jellyfin_version="$(sed -n 's:.*<JellyfinVersion>\(.*\)</JellyfinVersion>.*:\1:p' Directory.Build.props)"
guid="$(sed -n 's:.*PluginId = Guid.Parse("\(.*\)").*:\1:p' Jellyfin.Plugin.Retimer/Plugin.cs)"
[ -n "$version" ] && [ -n "$jellyfin_version" ] && [ -n "$guid" ] || { echo "could not read the version, the Jellyfin version or the plugin id" >&2; exit 1; }

dotnet build Jellyfin.Plugin.Retimer -c Release --nologo -v quiet

zip_name="subtitle-retimer_${version}.zip"
rm -rf dist
mkdir -p dist
# The server ships every other assembly the plugin uses, so the zip holds the one DLL.
(cd Jellyfin.Plugin.Retimer/bin/Release/net10.0 && zip -q -X "../../../../dist/${zip_name}" Jellyfin.Plugin.Retimer.dll)

checksum="$(md5sum "dist/${zip_name}" | cut -d' ' -f1)"
timestamp="$(date -u +%Y-%m-%dT%H:%M:%SZ)"

cat > dist/manifest.json <<JSON
[
  {
    "guid": "${guid}",
    "name": "Subtitle Retimer",
    "overview": "Finds subtitles for your shows and films and fixes their timing.",
    "description": "A page that lists your shows and gets a subtitle for an episode from Jimaku or OpenSubtitles. A model can pick the file, and Retimer API can retime it against the video's own subtitle tracks or its audio. The subtitle is saved next to the video and Jellyfin lists it at once.",
    "owner": "friedrich-de",
    "category": "Subtitles",
    "versions": [
      {
        "version": "${version}",
        "changelog": "The subtitle page: your shows, episodes, a file picker, activity and settings. Fetches, retimes and saves subtitles.",
        "targetAbi": "${jellyfin_version}.0",
        "sourceUrl": "${base_url}/${zip_name}",
        "checksum": "${checksum}",
        "timestamp": "${timestamp}"
      }
    ]
  }
]
JSON

echo "dist/${zip_name} (md5 ${checksum})"
echo "dist/manifest.json -> ${base_url}/manifest.json"
