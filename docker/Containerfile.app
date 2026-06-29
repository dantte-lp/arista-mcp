# syntax=docker/dockerfile:1.7
#
# Multi-stage Containerfile for the arista-mcp server image.
#
#   Stage 1 (build) — full SDK on Ubuntu, restores + publishes a
#                     self-contained single-file binary for $TARGETARCH.
#                     Self-contained because the runtime stage is
#                     `runtime-deps` (no managed runtime baked in).
#
#   Stage 2 (runtime) — `runtime-deps:10.0-noble-chiseled-extra`.
#                       Chiseled = no shell, no package manager, ~100 MB
#                       smaller, fewer CVEs; the `-extra` variant keeps
#                       ICU + tzdata which the corpus pipeline needs for
#                       Russian/UTF-8 string handling. Ships a non-root
#                       `app` user (UID 1654) by default.
#
# Multi-arch: docker buildx populates TARGETARCH (`amd64` / `arm64`);
# we map that to the .NET RID once and reuse it for `dotnet publish`.

ARG SDK_TAG=10.0-noble
ARG RUNTIME_TAG=10.0-noble-chiseled-extra

# ──────────────────────────────────────────────────────────────────────
# Stage 1 — build
# ──────────────────────────────────────────────────────────────────────
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:${SDK_TAG} AS build
ARG TARGETARCH
ARG VERSION=0.0.0
ARG COMMIT=unknown

ENV DOTNET_NOLOGO=1 \
    DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1

WORKDIR /src

# Copy only the dependency manifests first so the restore layer caches
# across source-only changes.
COPY global.json Directory.Build.props Directory.Packages.props arista-mcp.slnx ./
COPY src/ ./src/
COPY BannedSymbols.txt ./

# Map docker arch → .NET RID.
RUN case "${TARGETARCH}" in \
        amd64) echo "linux-x64"   > /tmp/rid ;; \
        arm64) echo "linux-arm64" > /tmp/rid ;; \
        *) echo "unsupported TARGETARCH=${TARGETARCH}" >&2; exit 1 ;; \
    esac

RUN --mount=type=cache,target=/root/.nuget/packages,sharing=locked \
    RID="$(cat /tmp/rid)" \
    && dotnet restore src/AristaMcp.Cli/AristaMcp.Cli.csproj \
        --runtime "$RID"

RUN --mount=type=cache,target=/root/.nuget/packages,sharing=locked \
    RID="$(cat /tmp/rid)" \
    && dotnet publish src/AristaMcp.Cli/AristaMcp.Cli.csproj \
        --configuration Release \
        --runtime "$RID" \
        --self-contained true \
        --no-restore \
        -p:PublishSingleFile=true \
        -p:IncludeNativeLibrariesForSelfExtract=true \
        -p:PublishTrimmed=false \
        -p:DebugType=embedded \
        -p:Version=${VERSION} \
        -p:InformationalVersion=${VERSION}+${COMMIT} \
        --output /app/publish

# ──────────────────────────────────────────────────────────────────────
# Stage 2 — runtime
# ──────────────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/runtime-deps:${RUNTIME_TAG} AS runtime

ARG VERSION=0.0.0
ARG COMMIT=unknown

LABEL org.opencontainers.image.title="arista-mcp" \
      org.opencontainers.image.description="Hybrid retrieval MCP server for Arista documentation" \
      org.opencontainers.image.source="https://github.com/dantte-lp/arista-mcp" \
      org.opencontainers.image.licenses="MIT" \
      org.opencontainers.image.version="${VERSION}" \
      org.opencontainers.image.revision="${COMMIT}"

WORKDIR /app
COPY --from=build --chown=app:app /app/publish/ /app/

# Models are mounted at runtime via the Quadlet volume — pre-create the
# mountpoint so the read-only rootfs doesn't reject a fresh bind.
USER app
ENV ARISTA_MCP__MODELSDIR=/var/lib/arista-mcp/models \
    DOTNET_NOLOGO=1 \
    DOTNET_CLI_TELEMETRY_OPTOUT=1

EXPOSE 8080

# `serve --transport http --bind 0.0.0.0 --port 8080` — bind 0.0.0.0
# inside the container so Podman's PublishPort can route host traffic
# in. The host side stays loopback-only via PublishPort=127.0.0.1:8080.
ENTRYPOINT ["/app/arista-mcp", "serve", "--transport", "http", "--bind", "0.0.0.0", "--port", "8080"]
