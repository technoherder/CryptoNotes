#!/bin/bash
# =============================================================================
# CryptoNotes Quick Deploy Commands
# Run these on your Lightsail server: ssh bitnami@52.86.223.254
#
# Usage:
#   ./deploy-commands.sh                    # Uses default port 443
#   ./deploy-commands.sh 1337               # Uses custom port 1337
#   HTTPS_PORT=1337 ./deploy-commands.sh    # Alternative: via environment
# =============================================================================

# Exit on error
set -e

# Configuration - can be overridden via argument or environment
HTTPS_PORT="${1:-${HTTPS_PORT:-443}}"

echo "=== CryptoNotes Deployment ==="
echo "HTTPS Port: $HTTPS_PORT"

# 1. Install Docker if not present
if ! command -v docker &> /dev/null; then
    echo "[1/6] Installing Docker..."
    curl -fsSL https://get.docker.com | sudo sh
    sudo usermod -aG docker bitnami
    echo "Docker installed. You may need to log out and back in for group changes."
else
    echo "[1/6] Docker already installed"
fi

# 2. Install docker-compose plugin if needed
if ! docker compose version &> /dev/null; then
    echo "[2/6] Installing Docker Compose plugin..."
    sudo apt-get update && sudo apt-get install -y docker-compose-plugin
else
    echo "[2/6] Docker Compose already installed"
fi

# 3. Create app directory
echo "[3/6] Setting up application directory..."
sudo mkdir -p /opt/cryptonotes
sudo chown bitnami:bitnami /opt/cryptonotes
cd /opt/cryptonotes

# 4. Generate secure signing key
echo "[4/6] Generating secure token key..."
TOKEN_KEY=$(openssl rand -base64 48)

# 5. Create configuration files
echo "[5/6] Creating configuration files..."

cat > .env << EOF
DOMAIN=talk.technoherder.com
HTTPS_PORT=$HTTPS_PORT
TOKEN_SIGNING_KEY=$TOKEN_KEY
EOF
chmod 600 .env

cat > Caddyfile << 'EOF'
{$DOMAIN}:{$HTTPS_PORT} {
    reverse_proxy cryptonotes:5000
    header {
        Strict-Transport-Security "max-age=31536000; includeSubDomains; preload"
        X-Frame-Options "DENY"
        X-Content-Type-Options "nosniff"
        X-XSS-Protection "1; mode=block"
        Referrer-Policy "strict-origin-when-cross-origin"
        -Server
    }
    log {
        output file /data/access.log
    }
    encode gzip
}
EOF

cat > Dockerfile << 'EOF'
# Multi-stage build for CryptoNotes Server
FROM mcr.microsoft.com/dotnet/core/sdk:3.1-alpine AS build
WORKDIR /src
COPY CryptoNotes.Server/CryptoNotes.Server.csproj CryptoNotes.Server/
RUN dotnet restore CryptoNotes.Server/CryptoNotes.Server.csproj
COPY CryptoNotes.Server/ CryptoNotes.Server/
RUN dotnet publish CryptoNotes.Server/CryptoNotes.Server.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/core/aspnet:3.1-alpine AS runtime
RUN addgroup -S cryptonotes && adduser -S cryptonotes -G cryptonotes
RUN apk --no-cache add sqlite-libs
WORKDIR /app
COPY --from=build /app/publish .
RUN mkdir -p /app/data && chown -R cryptonotes:cryptonotes /app /app/data
VOLUME ["/app/data"]
ENV ASPNETCORE_ENVIRONMENT=Production ASPNETCORE_URLS=http://0.0.0.0:5000
EXPOSE 5000
USER cryptonotes
HEALTHCHECK --interval=30s --timeout=5s --start-period=10s --retries=3 \
    CMD wget --no-verbose --tries=1 --spider http://localhost:5000/health || exit 1
ENTRYPOINT ["dotnet", "CryptoNotes.Server.dll"]
EOF

cat > docker-compose.yml << 'EOF'
version: "3.8"

services:
  caddy:
    image: caddy:2-alpine
    container_name: cryptonotes-caddy
    restart: unless-stopped
    ports:
      - "80:80"
      - "\${HTTPS_PORT:-443}:\${HTTPS_PORT:-443}"
      - "\${HTTPS_PORT:-443}:\${HTTPS_PORT:-443}/udp"
    volumes:
      - ./Caddyfile:/etc/caddy/Caddyfile:ro
      - caddy-data:/data
      - caddy-config:/config
    environment:
      - DOMAIN=\${DOMAIN}
      - HTTPS_PORT=\${HTTPS_PORT}
    networks:
      - frontend
    depends_on:
      - cryptonotes

  cryptonotes:
    build: .
    container_name: cryptonotes-server
    restart: unless-stopped
    expose:
      - "5000"
    volumes:
      - cryptonotes-data:/app/data
    environment:
      - ASPNETCORE_ENVIRONMENT=Production
      - ASPNETCORE_URLS=http://0.0.0.0:5000
      - Security__TokenSigningKey=${TOKEN_SIGNING_KEY}
    networks:
      - frontend
    healthcheck:
      test: ["CMD", "wget", "--no-verbose", "--tries=1", "--spider", "http://localhost:5000/health"]
      interval: 30s
      timeout: 5s
      retries: 3
      start_period: 10s

volumes:
  cryptonotes-data:
  caddy-data:
  caddy-config:

networks:
  frontend:
    driver: bridge
EOF

echo "[6/6] Configuration complete!"
echo ""
echo "=== Next Steps ==="
echo "1. Clone the repo to get source code:"
echo "   git clone https://github.com/technoherder/CryptoNotes.git /tmp/cn"
echo "   cp -r /tmp/cn/CryptoNotes.Server /opt/cryptonotes/"
echo ""
echo "2. Build and start:"
echo "   cd /opt/cryptonotes"
echo "   docker compose up -d --build"
echo ""
echo "3. Check logs:"
echo "   docker compose logs -f"
echo ""
echo "4. Test:"
if [ "$HTTPS_PORT" = "443" ]; then
    echo "   curl https://talk.technoherder.com/health"
else
    echo "   curl https://talk.technoherder.com:$HTTPS_PORT/health"
fi
