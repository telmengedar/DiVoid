#!/usr/bin/env bash
# copy-packages.sh — Populate packages/ local NuGet feed for Docker builds.
#
# Copies the required Pooshit .nupkg files from the global NuGet package cache
# into packages/ at the repo root so that `docker build` can restore them
# without access to the developer's machine-level NuGet sources.
#
# Usage:
#   From the repo root: ./scripts/copy-packages.sh
#   Optional env vars:
#     SOURCE_DIR=<path>  override source (default: ~/.nuget/packages)
#     CLEAN=1            clear packages/ before copying
#
# Requires the packages to already be present in the global NuGet cache
# (i.e. the solution has been restored at least once on this machine).

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(dirname "$SCRIPT_DIR")"
PACKAGES_DIR="$REPO_ROOT/packages"
SOURCE_DIR="${SOURCE_DIR:-$HOME/.nuget/packages}"

if [[ "${CLEAN:-0}" == "1" && -d "$PACKAGES_DIR" ]]; then
    echo "Cleaning $PACKAGES_DIR ..."
    rm -rf "${PACKAGES_DIR:?}"/*
fi

mkdir -p "$PACKAGES_DIR"

# Packages required by the solution (lowercase name, version)
declare -A REQUIRED_PACKAGES=(
    ["pooshit.aspnetcore.services"]="0.6.16-preview"
    ["pooshit.ocelot"]="0.20.0-preview"
    ["pooshit.json"]="0.3.40-preview"
    ["pooshit.http"]="0.7.8-preview"
)

MISSING=()

for name in "${!REQUIRED_PACKAGES[@]}"; do
    version="${REQUIRED_PACKAGES[$name]}"
    nupkg_name="${name}.${version}.nupkg"

    # Global cache layout: <cache>/<name>/<version>/<name>.<version>.nupkg
    cache_path="$SOURCE_DIR/$name/$version/$nupkg_name"
    # Flat directory layout (e.g. /c/dev/nuget)
    flat_path="$SOURCE_DIR/$nupkg_name"

    if [[ -f "$cache_path" ]]; then
        source="$cache_path"
    elif [[ -f "$flat_path" ]]; then
        source="$flat_path"
    else
        MISSING+=("$nupkg_name")
        echo "WARNING: NOT FOUND: $nupkg_name" >&2
        continue
    fi

    dest="$PACKAGES_DIR/$nupkg_name"
    if [[ ! -f "$dest" ]]; then
        cp "$source" "$dest"
        echo "Copied: $nupkg_name"
    else
        echo "Already present: $nupkg_name"
    fi
done

if [[ ${#MISSING[@]} -gt 0 ]]; then
    echo ""
    echo "ERROR: Missing packages: ${MISSING[*]}" >&2
    echo "Restore the solution locally first: dotnet restore DiVoid.sln" >&2
    exit 1
fi

echo ""
echo "packages/ is ready. Run: docker build -t divoid:local ."
