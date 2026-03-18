# ════════════════════════════════════════════════════════════════════
#  Dockerfile  —  C# / .NET 8 Worker  (multi-stage build)
#
#  What changed from the original and WHY:
#
#  ORIGINAL:
#    FROM microsoft/dotnet:1.1.1-sdk
#    — Single stage: SDK image stays in the final image (~700 MB)
#    — microsoft/dotnet is deprecated and deleted from Docker Hub
#    — .NET 1.1.1 is EOL since 2019, has known CVEs
#    — No layer caching: COPY . then restore = restore runs every build
#    — CMD dotnet src/Worker/Worker.dll  (fragile relative path)
#    — Runs as root inside the container (security risk)
#
#  NEW:
#    — Multi-stage: Stage 1 (SDK) compiles; Stage 2 (runtime) runs
#    — Final image is ~200 MB instead of ~700 MB (no compiler tools)
#    — mcr.microsoft.com/dotnet is the official, maintained registry
#    — .NET 8 LTS — supported until November 2026
#    — Layer caching: .csproj copied first → dotnet restore cached
#      separately from source code changes
#    — Runs as non-root user 'app' (built into the .NET runtime image)
# ════════════════════════════════════════════════════════════════════


# ── Stage 1: BUILD ────────────────────────────────────────────────────────────
# Full SDK needed to compile. This stage is NOT included in the final image.
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build

WORKDIR /code

# --- Layer cache trick ---
# Copy the project file FIRST and restore dependencies.
# Docker caches this layer. If only your .cs files change (not .csproj),
# `dotnet restore` is NOT re-run on the next build → saves 10-30 seconds.
# Original copied everything at once so restore re-ran on every single change.
COPY src/Worker/worker.csproj ./src/Worker/
RUN dotnet restore ./src/Worker/worker.csproj --verbosity minimal

# Now copy the actual source code and publish a Release build
COPY src/Worker/ ./src/Worker/
RUN dotnet publish ./src/Worker/worker.csproj \
      --configuration Release \
      --output /app/publish \
      --no-restore              # skip restore: already done above


# ── Stage 2: RUNTIME ─────────────────────────────────────────────────────────
# Smaller image: only the .NET runtime, no SDK, no compiler, no NuGet cache.
# Original used a single SDK stage so the final image included all build tools.
FROM mcr.microsoft.com/dotnet/runtime:8.0 AS runtime

WORKDIR /app

# Copy only the compiled output from the build stage — nothing else
COPY --from=build /app/publish .

# Run as the non-root 'app' user that the .NET runtime image ships with.
# Original ran as root — if the container is compromised, root = game over.
USER app

# Absolute path via dotnet — no fragile relative path like the original
ENTRYPOINT ["dotnet", "Worker.dll"]