#!/bin/bash
# =============================================================================
# CryptoNotes Server Setup Script for Debian/Ubuntu
#
# Usage:
#   sudo ./setup-server.sh                    # Uses default port 443
#   sudo ./setup-server.sh 1337               # Uses custom port 1337
#   HTTPS_PORT=1337 sudo ./setup-server.sh    # Alternative: via environment
# =============================================================================

set -euo pipefail

# Configuration
HTTPS_PORT="${1:-${HTTPS_PORT:-443}}"

# Colors for output
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
NC='\033[0m' # No Color

log() { echo -e "${GREEN}[+]${NC} $1"; }
warn() { echo -e "${YELLOW}[!]${NC} $1"; }
error() { echo -e "${RED}[ERROR]${NC} $1"; exit 1; }

# Check if running as root
if [[ $EUID -ne 0 ]]; then
   error "This script must be run as root (use sudo)"
fi

log "Starting CryptoNotes server setup..."
log "HTTPS Port: ${HTTPS_PORT}"

# =============================================================================
# 1. System Updates
# =============================================================================
log "Updating system packages..."
apt-get update -qq
apt-get upgrade -y -qq

# =============================================================================
# 2. Install Docker
# =============================================================================
if command -v docker &> /dev/null; then
    log "Docker already installed: $(docker --version)"
else
    log "Installing Docker..."
    apt-get install -y -qq ca-certificates curl gnupg

    # Add Docker's official GPG key
    install -m 0755 -d /etc/apt/keyrings
    curl -fsSL https://download.docker.com/linux/debian/gpg | gpg --dearmor -o /etc/apt/keyrings/docker.gpg
    chmod a+r /etc/apt/keyrings/docker.gpg

    # Add the repository
    echo \
      "deb [arch=$(dpkg --print-architecture) signed-by=/etc/apt/keyrings/docker.gpg] https://download.docker.com/linux/debian \
      $(. /etc/os-release && echo "$VERSION_CODENAME") stable" | \
      tee /etc/apt/sources.list.d/docker.list > /dev/null

    apt-get update -qq
    apt-get install -y -qq docker-ce docker-ce-cli containerd.io docker-buildx-plugin docker-compose-plugin

    log "Docker installed: $(docker --version)"
fi

# =============================================================================
# 3. Configure Firewall (UFW)
# =============================================================================
log "Configuring firewall..."
apt-get install -y -qq ufw

ufw --force reset
ufw default deny incoming
ufw default allow outgoing
ufw allow ssh
ufw allow 80/tcp           # HTTP (for Let's Encrypt challenge)
ufw allow ${HTTPS_PORT}/tcp   # HTTPS
ufw allow ${HTTPS_PORT}/udp   # HTTP/3 (QUIC)
ufw --force enable

log "Firewall configured: SSH, HTTP, HTTPS allowed"

# =============================================================================
# 4. Install Fail2ban
# =============================================================================
log "Installing fail2ban..."
apt-get install -y -qq fail2ban

cat > /etc/fail2ban/jail.local << 'EOF'
[DEFAULT]
bantime = 1h
findtime = 10m
maxretry = 5

[sshd]
enabled = true
port = ssh
filter = sshd
logpath = /var/log/auth.log
maxretry = 3
EOF

systemctl enable fail2ban
systemctl restart fail2ban
log "Fail2ban configured"

# =============================================================================
# 5. Create app directory
# =============================================================================
APP_DIR="/opt/cryptonotes"
log "Creating application directory at ${APP_DIR}..."
mkdir -p "${APP_DIR}"
cd "${APP_DIR}"

# =============================================================================
# 6. Generate secure token key
# =============================================================================
TOKEN_KEY=$(openssl rand -base64 48)
log "Generated secure token signing key"

# =============================================================================
# 7. Create configuration files
# =============================================================================
log "Creating configuration files..."

# Caddyfile
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
        output file /data/access.log {
            roll_size 10mb
            roll_keep 5
        }
    }

    encode gzip zstd
}
EOF

# docker-compose.yml
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
      - DOMAIN=${DOMAIN}
      - HTTPS_PORT=${HTTPS_PORT}
    networks:
      - frontend
    depends_on:
      - cryptonotes
    cap_drop:
      - ALL
    cap_add:
      - NET_BIND_SERVICE
    security_opt:
      - no-new-privileges:true

  cryptonotes:
    image: ghcr.io/cryptonotes/server:latest
    build:
      context: .
      dockerfile: Dockerfile
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
      - Security__TokenExpiryHours=${TOKEN_EXPIRY_HOURS:-24}
      - Security__MaxLoginAttemptsPerMinute=${MAX_LOGIN_ATTEMPTS:-5}
      - Security__MaxMessageSizeBytes=${MAX_MESSAGE_SIZE:-65536}
      - Security__MinPasswordLength=${MIN_PASSWORD_LENGTH:-8}
      - Security__MessageExpiryDays=${MESSAGE_EXPIRY_DAYS:-30}
    deploy:
      resources:
        limits:
          cpus: "0.80"
          memory: 256M
    cap_drop:
      - ALL
    security_opt:
      - no-new-privileges:true
    read_only: true
    tmpfs:
      - /tmp:size=10M
    networks:
      - frontend
    healthcheck:
      test: ["CMD", "wget", "--no-verbose", "--tries=1", "--spider", "http://localhost:5000/health"]
      interval: 30s
      timeout: 5s
      retries: 3
      start_period: 10s

  backup:
    image: alpine:3.18
    container_name: cryptonotes-backup
    restart: unless-stopped
    volumes:
      - cryptonotes-data:/data:ro
      - cryptonotes-backups:/backups
    entrypoint: /bin/sh
    command: >
      -c 'while true; do
        TIMESTAMP=$$(date +%Y%m%d-%H%M%S);
        if [ -f /data/cryptonotes.db ]; then
          cp /data/cryptonotes.db "/backups/cryptonotes-$$TIMESTAMP.db";
          echo "[$$TIMESTAMP] Backup created";
          ls -t /backups/cryptonotes-*.db 2>/dev/null | tail -n +8 | xargs rm -f 2>/dev/null;
        fi;
        sleep 86400;
      done'
    cap_drop:
      - ALL
    security_opt:
      - no-new-privileges:true
    networks: []

volumes:
  cryptonotes-data:
  cryptonotes-backups:
  caddy-data:
  caddy-config:

networks:
  frontend:
    driver: bridge
EOF

# .env file
cat > .env << EOF
DOMAIN=talk.technoherder.com
HTTPS_PORT=${HTTPS_PORT}
TOKEN_SIGNING_KEY=${TOKEN_KEY}
TOKEN_EXPIRY_HOURS=24
MAX_LOGIN_ATTEMPTS=5
MAX_MESSAGE_SIZE=65536
MIN_PASSWORD_LENGTH=8
MESSAGE_EXPIRY_DAYS=30
EOF

chmod 600 .env
log "Configuration files created"

# =============================================================================
# Done
# =============================================================================
echo ""
echo "============================================================================="
echo -e "${GREEN}Server setup complete!${NC}"
echo "============================================================================="
echo ""
echo "Next steps:"
echo "  1. Ensure DNS A record points to this server's IP"
echo "  2. Copy the CryptoNotes source code to ${APP_DIR}"
echo "  3. Build and start: cd ${APP_DIR} && docker compose up -d --build"
echo ""
echo "Configuration: ${APP_DIR}/.env"
echo "Logs: docker compose logs -f"
echo ""
