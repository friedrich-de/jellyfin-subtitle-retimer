#!/usr/bin/env bash
# Runs the end-to-end tests: a throwaway Jellyfin server, the plugin packed by dev/package.sh
# and installed through Jellyfin's own plugin installer, then every endpoint called over HTTP.
#
#   dev/e2e.sh
#
# By default it downloads the Jellyfin server once into .cache/ (about 110 MB, x86-64 Linux).
# To use a server that is already installed, set
#   RETIMER_E2E_JELLYFIN      the server binary, for example /usr/bin/jellyfin
#   RETIMER_E2E_JELLYFIN_WEB  its web client folder, for example /usr/share/jellyfin/web
#   RETIMER_E2E_FFMPEG        ffmpeg, when it is not on the PATH, for example /usr/lib/jellyfin-ffmpeg/ffmpeg
# Set JIMAKU_API_KEY to include the one test that searches the real jimaku.cc.
set -euo pipefail

cd "$(dirname "$0")/.."

# Jellyfin refuses to start without ffmpeg, and the server download does not include it.
if [ -z "${RETIMER_E2E_FFMPEG:-}" ] && ! command -v ffmpeg >/dev/null; then
  echo "Jellyfin does not start without ffmpeg. Install it, or set RETIMER_E2E_FFMPEG to its path." >&2
  exit 1
fi

# The Jellyfin release the plugin targets, and the checksum of its x86-64 Linux archive.
jellyfin_series="12.4"
jellyfin_sha256="e835e224571ac9186eed1d2239eeb0e972ab31eb61ade44594289afc94c15875"

if [ -z "${RETIMER_E2E_JELLYFIN:-}" ]; then
  [ "$(uname -s)-$(uname -m)" = "Linux-x86_64" ] || {
    echo "No download for $(uname -s) $(uname -m): set RETIMER_E2E_JELLYFIN to an installed Jellyfin ${jellyfin_series}." >&2
    exit 1
  }
  cache=".cache/jellyfin-${jellyfin_series}"
  if [ ! -x "${cache}/jellyfin/jellyfin" ]; then
    archive="jellyfin_${jellyfin_series}-amd64.tar.gz"
    echo "Downloading Jellyfin ${jellyfin_series}"
    rm -rf "${cache}"
    mkdir -p "${cache}"
    curl -fsSL --retry 3 -o "${cache}/${archive}" \
      "https://repo.jellyfin.org/files/server/linux/stable/v${jellyfin_series}/amd64/${archive}"
    echo "${jellyfin_sha256}  ${cache}/${archive}" | sha256sum -c - >/dev/null
    tar -xzf "${cache}/${archive}" -C "${cache}"
    rm "${cache}/${archive}"
  fi
  export RETIMER_E2E_JELLYFIN="${PWD}/${cache}/jellyfin/jellyfin"
fi

# Build before the tests, and without the Jimaku key. The compiler's and the build's helper
# processes can outlive this script, and they keep the environment they were started with.
# So they are switched off here, and no build step sees the key.
export UseSharedCompilation=false
export MSBUILDDISABLENODEREUSE=1

# The tests serve dist/ themselves and put their own address into the manifest.
env -u JIMAKU_API_KEY dev/package.sh >/dev/null
env -u JIMAKU_API_KEY dotnet build Jellyfin.Plugin.Retimer.E2E -c Release --nologo -v quiet
export RETIMER_E2E_PACKAGE_DIR="${PWD}/dist"
export RETIMER_E2E_REQUIRED=1

dotnet test --project Jellyfin.Plugin.Retimer.E2E -c Release --no-build
