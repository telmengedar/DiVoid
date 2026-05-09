# =============================================================================
# DiVoid Backend — multi-stage Dockerfile
# =============================================================================
#
# PRIVATE FEED STRATEGY (local filesystem, no remote credentials)
# ----------------------------------------------------------------
# The Pooshit packages are distributed as .nupkg files in a local-filesystem
# NuGet feed rather than via a remote authenticated server.  Before running
# `docker build`, populate the `packages/` directory at the repo root with
# the required .nupkg files.  Helper scripts are provided:
#
#   # Windows PowerShell:
#   .\scripts\copy-packages.ps1
#
#   # Linux / macOS / WSL:
#   ./scripts/copy-packages.sh
#
# Both scripts copy from the global NuGet package cache by default.
# See nuget.config for feed configuration details.
#
# BUILD COMMAND
# -------------
#   # 1. Populate local feed (first time or after package updates):
#   .\scripts\copy-packages.ps1          # Windows
#   ./scripts/copy-packages.sh           # Linux / WSL
#
#   # 2. Build the image (tests run automatically; build fails if any test fails):
#   docker build -t divoid:local .
#
# RUNTIME
# -------
# The service listens on port 80 (HTTP/1+2).  HTTP/3 (QUIC) is configured in
# Program.cs but is a host/reverse-proxy concern at deploy time.
#
# Database defaults to SQLite at /data/DiVoid.db3 — mount a volume there.
# Override Database__Type at runtime for PostgreSQL:
#   -e Database__Type=PostgreSQL
#   -e Database__Host=<host>
#   -e Database__Port=5432
#   -e Database__Instance=<db>
#   -e Database__User=<user>
#   -e Database__Password=<password>
# =============================================================================

# -----------------------------------------------------------------------------
# Stage 1: build — restore and compile the entire solution
# -----------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build

WORKDIR /src

# Copy solution and project files first so the restore layer is cached
# independently of source changes.
COPY DiVoid.sln ./
COPY Backend/Backend.csproj Backend/
COPY Backend.tests/Backend.tests.csproj Backend.tests/

# Copy nuget.config and the pre-populated local feed directory.
# packages/ is NOT excluded from .dockerignore so the private
# Pooshit .nupkg files are accessible during restore.
COPY nuget.config ./
COPY packages/ packages/

# Restore all dependencies (nuget.config points at packages/ + nuget.org)
RUN dotnet restore DiVoid.sln

# Copy full source after restore to maximise layer cache hits
COPY Backend/ Backend/
COPY Backend.tests/ Backend.tests/

# Compile Release
RUN dotnet build DiVoid.sln -c Release --no-restore

# -----------------------------------------------------------------------------
# Stage 2: test — runs against the Release build; any test failure aborts the
#           image build before the final publish step is reached.
#           Runtime inherits from this stage so Docker cannot skip it.
# -----------------------------------------------------------------------------
FROM build AS test

RUN dotnet test Backend.tests/Backend.tests.csproj \
        -c Release \
        --no-build \
        --logger "console;verbosity=normal" \
        --blame-hang \
        --blame-hang-timeout 2min

# Publish here so the runtime stage can COPY from a stage it depends on
RUN dotnet publish Backend/Backend.csproj -c Release --no-build -o /app/publish

# -----------------------------------------------------------------------------
# Stage 3: runtime — lean aspnet image; no SDK, no test dependencies
# -----------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS runtime

WORKDIR /app

# Copy published output from the test stage (which inherited from build).
# Because runtime depends on test, Docker must execute the test stage —
# tests are never skipped.
COPY --from=test /app/publish .

# The service binds to port 80 (see Program.cs / Kestrel config)
EXPOSE 80

# Default to SQLite with a volume-mounted data directory.
# Override Database__Type at runtime for PostgreSQL.
ENV ASPNETCORE_ENVIRONMENT=Production
ENV Database__Type=Sqlite
ENV Database__Source=/data/DiVoid.db3

# Create the data directory; in production mount a volume at /data
RUN mkdir -p /data

ENTRYPOINT ["dotnet", "Backend.dll"]
