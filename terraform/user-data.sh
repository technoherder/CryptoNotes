#!/bin/bash
# =============================================================================
# CryptoNotes Server Bootstrap Script (Cloud-Init User Data)
#
# This script runs automatically when the Lightsail instance first boots.
# It installs Docker, configures the firewall, and sets up CryptoNotes.
# =============================================================================

set -euo pipefail
exec > >(tee /var/log/cryptonotes-setup.log) 2>&1

echo "=========================================="
echo "CryptoNotes Server Setup - $(date)"
echo "=========================================="

# Configuration
DOMAIN="${domain}"
HTTPS_PORT="${https_port}"
APP_DIR="/opt/cryptonotes"

# =============================================================================
# System Updates
# =============================================================================
echo "[1/6] Updating system packages..."
apt-get update -qq
DEBIAN_FRONTEND=noninteractive apt-get upgrade -y -qq

# =============================================================================
# Install Docker
# =============================================================================
echo "[2/6] Installing Docker..."
apt-get install -y -qq ca-certificates curl gnupg

install -m 0755 -d /etc/apt/keyrings
curl -fsSL https://download.docker.com/linux/debian/gpg | gpg --dearmor -o /etc/apt/keyrings/docker.gpg
chmod a+r /etc/apt/keyrings/docker.gpg

echo "deb [arch=$(dpkg --print-architecture) signed-by=/etc/apt/keyrings/docker.gpg] https://download.docker.com/linux/debian $(. /etc/os-release && echo "$VERSION_CODENAME") stable" | tee /etc/apt/sources.list.d/docker.list > /dev/null

apt-get update -qq
apt-get install -y -qq docker-ce docker-ce-cli containerd.io docker-buildx-plugin docker-compose-plugin

# Add admin user to docker group
usermod -aG docker admin || true

# =============================================================================
# Configure Firewall
# =============================================================================
echo "[3/6] Configuring firewall..."
apt-get install -y -qq ufw

ufw --force reset
ufw default deny incoming
ufw default allow outgoing
ufw allow ssh
ufw allow 80/tcp
ufw allow $HTTPS_PORT/tcp
ufw allow $HTTPS_PORT/udp
ufw --force enable

# =============================================================================
# Install Fail2ban
# =============================================================================
echo "[4/6] Installing fail2ban..."
apt-get install -y -qq fail2ban

cat > /etc/fail2ban/jail.local << 'JAILEOF'
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
JAILEOF

systemctl enable fail2ban
systemctl restart fail2ban

# =============================================================================
# Create Application Directory and Config
# =============================================================================
echo "[5/6] Creating application configuration..."
mkdir -p "$APP_DIR"
cd "$APP_DIR"

# Generate secure token key
TOKEN_KEY=$(openssl rand -base64 48)

# Create Caddyfile
cat > Caddyfile << 'CADDYEOF'
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
CADDYEOF

# Create docker-compose.yml
cat > docker-compose.yml << 'COMPOSEEOF'
version: "3.8"

services:
  caddy:
    image: caddy:2-alpine
    container_name: cryptonotes-caddy
    restart: unless-stopped
    ports:
      - "80:80"
      - "$${HTTPS_PORT}:$${HTTPS_PORT}"
      - "$${HTTPS_PORT}:$${HTTPS_PORT}/udp"
    volumes:
      - ./Caddyfile:/etc/caddy/Caddyfile:ro
      - caddy-data:/data
      - caddy-config:/config
    environment:
      - DOMAIN=$${DOMAIN}
      - HTTPS_PORT=$${HTTPS_PORT}
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
    image: ghcr.io/technoherder/cryptonotes:latest
    container_name: cryptonotes-server
    restart: unless-stopped
    expose:
      - "5000"
    volumes:
      - cryptonotes-data:/app/data
    environment:
      - ASPNETCORE_ENVIRONMENT=Production
      - ASPNETCORE_URLS=http://0.0.0.0:5000
      - Security__TokenSigningKey=$${TOKEN_SIGNING_KEY}
      - Security__TokenExpiryHours=24
      - Security__MaxLoginAttemptsPerMinute=5
      - Security__MaxMessageSizeBytes=65536
      - Security__MinPasswordLength=8
      - Security__MessageExpiryDays=30
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
COMPOSEEOF

# Create .env file
cat > .env << ENVEOF
DOMAIN=$DOMAIN
HTTPS_PORT=$HTTPS_PORT
TOKEN_SIGNING_KEY=$TOKEN_KEY
ENVEOF

chmod 600 .env
chown -R admin:admin "$APP_DIR"

# =============================================================================
# Note: Container won't start automatically
# =============================================================================
echo "[6/6] Setup complete!"
echo ""
echo "=========================================="
echo "CryptoNotes server setup complete!"
echo "=========================================="
echo ""
echo "To complete deployment:"
echo "  1. Upload the Docker image or build locally"
echo "  2. cd $APP_DIR && docker compose up -d"
echo ""
echo "Config: $APP_DIR/.env"
echo "Domain: $DOMAIN"
echo "HTTPS Port: $HTTPS_PORT"
echo ""
