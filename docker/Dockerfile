# =============================================================================
# CryptoNotes Server - Multi-stage Docker Build
# Builds and runs the CryptoNotes E2E encrypted messaging server.
#
# Build:  docker build -t cryptonotes-server ./docker -f docker/Dockerfile
# Run:    docker run -d -p 127.0.0.1:5000:5000 -v cryptonotes-data:/app/data cryptonotes-server
# =============================================================================

# --- Stage 1: Build ---
FROM mcr.microsoft.com/dotnet/core/sdk:3.1-alpine AS build
WORKDIR /src

# Copy project file first for layer caching
COPY CryptoNotes.Server/CryptoNotes.Server.csproj CryptoNotes.Server/
RUN dotnet restore CryptoNotes.Server/CryptoNotes.Server.csproj

# Copy source and build
COPY CryptoNotes.Server/ CryptoNotes.Server/
RUN dotnet publish CryptoNotes.Server/CryptoNotes.Server.csproj \
    -c Release \
    -o /app/publish \
    --no-restore

# --- Stage 2: Runtime ---
FROM mcr.microsoft.com/dotnet/core/aspnet:3.1-alpine AS runtime

# Security: run as non-root user
RUN addgroup -S cryptonotes && adduser -S cryptonotes -G cryptonotes

# Security: remove unnecessary packages and shells
RUN apk --no-cache add sqlite-libs \
    && rm -rf /var/cache/apk/*

WORKDIR /app
COPY --from=build /app/publish .

# Create data directory
RUN mkdir -p /app/data && chown -R cryptonotes:cryptonotes /app /app/data

# Security: read-only filesystem for app binaries
# Data directory is mounted as a volume
VOLUME ["/app/data"]

# Environment configuration
ENV ASPNETCORE_ENVIRONMENT=Production \
    ASPNETCORE_URLS=http://0.0.0.0:5000 \
    DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    DOTNET_RUNNING_IN_CONTAINER=true

# Expose only HTTP - TLS is handled by reverse proxy
EXPOSE 5000

# Switch to non-root user
USER cryptonotes

# Health check
HEALTHCHECK --interval=30s --timeout=5s --start-period=10s --retries=3 \
    CMD wget --no-verbose --tries=1 --spider http://localhost:5000/health || exit 1

ENTRYPOINT ["dotnet", "CryptoNotes.Server.dll"]
