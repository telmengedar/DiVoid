# copy-packages.ps1 — Populate the packages/ local NuGet feed for Docker builds.
#
# Copies the required Pooshit .nupkg files from the global NuGet package cache
# into packages/ at the repo root so that `docker build` can restore them
# without access to the developer's machine-level NuGet sources.
#
# Usage:
#   From the repo root: .\scripts\copy-packages.ps1
#   Optional: -SourceDir <path>  override the source directory
#             -Clean             clear packages/ before copying
#
# Requires the packages to already be present in the global NuGet cache
# (i.e. the solution has been restored at least once on this machine).

param (
    [string]$SourceDir = "$env:USERPROFILE\.nuget\packages",
    [switch]$Clean
)

$ErrorActionPreference = "Stop"

$RepoRoot = Split-Path $PSScriptRoot -Parent
$PackagesDir = Join-Path $RepoRoot "packages"

if ($Clean -and (Test-Path $PackagesDir)) {
    Write-Host "Cleaning $PackagesDir ..."
    Remove-Item "$PackagesDir\*" -Recurse -Force
}

if (-not (Test-Path $PackagesDir)) {
    New-Item -ItemType Directory -Path $PackagesDir | Out-Null
}

# Packages required by the solution (name, version as stored in global cache)
$RequiredPackages = @(
    @{ Name = "pooshit.aspnetcore.services"; Version = "0.6.16-preview" },
    @{ Name = "pooshit.ocelot";              Version = "0.20.0-preview"  },
    @{ Name = "pooshit.json";                Version = "0.3.40-preview"  },
    @{ Name = "pooshit.http";                Version = "0.7.8-preview"   }
)

$Missing = @()

foreach ($pkg in $RequiredPackages) {
    $nupkgName = "$($pkg.Name).$($pkg.Version).nupkg"

    # Global cache layout: <cache>/<name>/<version>/<name>.<version>.nupkg
    $CachePath = Join-Path $SourceDir "$($pkg.Name)\$($pkg.Version)\$nupkgName"

    # Also try a flat directory layout (e.g. C:\dev\nuget)
    $FlatPath = Join-Path $SourceDir $nupkgName

    $Source = if (Test-Path $CachePath) { $CachePath }
              elseif (Test-Path $FlatPath) { $FlatPath }
              else { $null }

    if ($null -eq $Source) {
        $Missing += $nupkgName
        Write-Warning "NOT FOUND: $nupkgName (looked in $CachePath and $FlatPath)"
    } else {
        $Dest = Join-Path $PackagesDir $nupkgName
        if (-not (Test-Path $Dest)) {
            Copy-Item $Source $Dest
            Write-Host "Copied: $nupkgName"
        } else {
            Write-Host "Already present: $nupkgName"
        }
    }
}

if ($Missing.Count -gt 0) {
    Write-Error "Missing packages: $($Missing -join ', ')`nRestore the solution locally first: dotnet restore DiVoid.sln"
    exit 1
}

Write-Host ""
Write-Host "packages/ is ready. Run: docker build -t divoid:local ."
