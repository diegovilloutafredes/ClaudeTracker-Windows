#!/bin/bash
# Signs and publishes a release that the Release workflow created as a draft. Run on the
# maintainer's Mac: the update-signing key lives only in its Keychain, and one key signs
# both apps (the Mac repo's scripts/update-signing.swift is the only tool that touches it).
#
#   scripts/publish-release.sh --check-key   # only confirm the Keychain key is the one the app embeds
#   scripts/publish-release.sh 1.2.0         # wait for CI, sign ClaudeTracker-Setup.exe, upload the .sig, publish
#
# Nothing is public until the last step, and the app passes drafts over, so a run that
# fails leaves users untouched: run it again.
#
# What this signs is what every installed copy will run without asking, so it signs only a
# file it can tie to the maintainer's own tag: the workflow must have run on the commit
# tagged in THIS clone, the draft's file must be the one that run built (by the SHA-256 the
# build printed in its log), and the setup must carry the version. What that leaves is the
# build itself: the packages and tools the workflow fetches are trusted as they come.
#
# The Mac repo is looked for beside this one (the workspace's layout); CLAUDETRACKER_MAC_REPO
# points somewhere else.
set -euo pipefail
cd "$(dirname "$0")/.."

mac="${CLAUDETRACKER_MAC_REPO:-../macos}"
[ -f "$mac/scripts/update-signing.swift" ] || { echo "The Mac repo was not found at $mac (set CLAUDETRACKER_MAC_REPO)." >&2; exit 1; }
sign_tool() { (cd "$mac" && xcrun --sdk macosx swift scripts/update-signing.swift "$@"); }

# The version a setup file carries, read out of the file: the "FileVersion" text Windows shows.
setup_version() {
  python3 - "$1" <<'EOF'
import sys
data = open(sys.argv[1], "rb").read()
at = data.find("FileVersion".encode("utf-16-le"))
if at < 0:
    sys.exit("no FileVersion in " + sys.argv[1])
# The name, its terminator, padding to four bytes, then the value up to its own terminator.
rest = data[at + len("FileVersion") * 2:].decode("utf-16-le", "ignore").lstrip("\x00")
print(rest.split("\x00", 1)[0].strip())
EOF
}

# Signing with a key the app does not embed would publish a release every install refuses.
key=$(sign_tool public-key)
if ! grep -q "\"$key\"" src/ClaudeTracker.Core/Updates.cs; then
  echo "The Keychain signing key does not match SigningPublicKey in Updates.cs." >&2
  exit 1
fi
[ "${1:-}" = "--check-key" ] && { echo "==> Signing key matches the app"; exit 0; }

version="${1:?usage: publish-release.sh <version> | --check-key}"
tag="v$version"
# Anything that fails from here on leaves a private draft; running this again resumes it.
trap 'echo "Publishing $tag failed. The draft stays private; rerun: scripts/publish-release.sh $version" >&2' ERR

echo "==> Waiting for the Release workflow for $tag..."
run=""
for _ in $(seq 1 60); do
  run=$(gh run list --workflow release.yml --branch "$tag" --limit 1 --json databaseId -q '.[0].databaseId')
  [ -n "$run" ] && break
  sleep 5
done
[ -n "$run" ] || { echo "No Release workflow run found for $tag." >&2; exit 1; }
if ! gh run watch "$run" --exit-status --interval 20 > /dev/null; then
  trap - ERR
  # Running this again cannot help: there is no setup to sign.
  echo "The Release workflow failed: $(gh run view "$run" --json url -q .url)" >&2
  echo "Fix the cause, delete the tag (git push origin :refs/tags/$tag; git tag -d $tag), and tag again." >&2
  exit 1
fi

# The run must have built the commit that is tagged here, in this clone: a tag moved on
# GitHub, or a run started from another commit, is not the release that was meant.
tagged=$(git rev-parse --verify --quiet "$tag^{commit}") || {
  echo "There is no tag $tag in this clone. Fetch it (git fetch origin tag $tag) and check that it is the commit you meant (git log -1 $tag)." >&2
  exit 1
}
built_from=$(gh run view "$run" --json headSha -q .headSha)
[ "$built_from" = "$tagged" ] || { echo "The Release workflow built $built_from, but $tag is $tagged in this clone." >&2; exit 1; }

tmp=$(mktemp -d)
trap 'rm -rf "$tmp"' EXIT
gh release download "$tag" --pattern ClaudeTracker-Setup.exe --dir "$tmp"

# The file on the draft must be the file that run built. The build prints the SHA-256 of
# the setup it made (scripts/build-installer.ps1), and a finished run's log cannot be
# changed: a file put on the draft afterwards does not match it.
# (|| true: with no hash in the log the search fails, and the check below should say so.)
built_hash=$(gh run view "$run" --log | grep -o 'sha256 [0-9a-f]\{64\}' | tail -1 | cut -d' ' -f2 || true)
draft_hash=$(shasum -a 256 "$tmp/ClaudeTracker-Setup.exe" | cut -d' ' -f1)
if [ -z "$built_hash" ] || [ "$built_hash" != "$draft_hash" ]; then
  echo "The setup on the draft ($draft_hash) is not the file the workflow built (${built_hash:-no hash found in the log of the run})." >&2
  exit 1
fi

# Sign only the build this version promises; the app checks the file's version again after verifying.
built=$(setup_version "$tmp/ClaudeTracker-Setup.exe")
[ "$built" = "$version" ] || { echo "The draft's setup is version $built, expected $version." >&2; exit 1; }

sign_tool sign "$tmp/ClaudeTracker-Setup.exe" > /dev/null
gh release upload "$tag" "$tmp/ClaudeTracker-Setup.exe.sig" --clobber
gh release edit "$tag" --draft=false --latest > /dev/null
echo "==> Published $tag (signed)"
