#!/usr/bin/env bash
# Create a signed release tag `releases/vX.Y.Z`. The publish and release
# GitHub workflows fire off the pushed tag and stamp the package version from it.
set -euo pipefail

usage() {
	echo "usage: scripts/release.sh <vX.Y.Z> [--dry-run]" >&2
	exit 2
}

version=""
dry_run=0
for arg in "$@"; do
	case "$arg" in
		--dry-run) dry_run=1 ;;
		v*|[0-9]*) version="${arg#v}" ;;
		*) usage ;;
	esac
done

[[ -n "$version" ]] || usage

if ! printf '%s' "$version" | grep -Eq '^[0-9]+\.[0-9]+\.[0-9]+([-.+][0-9A-Za-z.-]+)?$'; then
	echo "release: '$version' is not a valid semantic version" >&2
	exit 1
fi

tag="releases/v${version}"

branch="$(git rev-parse --abbrev-ref HEAD)"
if [[ "$branch" != "main" ]]; then
	echo "release: must tag from 'main' (on '$branch')" >&2
	exit 1
fi

if [[ -n "$(git status --porcelain)" ]]; then
	echo "release: working tree is not clean" >&2
	exit 1
fi

if git rev-parse -q --verify "refs/tags/${tag}" >/dev/null; then
	echo "release: tag '${tag}' already exists" >&2
	exit 1
fi

if [[ "$dry_run" -eq 1 ]]; then
	echo "release: would create and push signed tag '${tag}'"
	exit 0
fi

git tag -s "$tag" -m "Release v${version}"
git push origin "$tag"
echo "release: pushed '${tag}'"
