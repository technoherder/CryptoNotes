#!/bin/bash
# =============================================================================
# CryptoNotes Server Setup Script for Debian/Ubuntu
#
# Usage:
#   sudo DOMAIN=msg.example.com ./setup-server.sh
#   sudo DOMAIN=msg.example.com ./setup-server.sh 1337               # Custom port
#   sudo DOMAIN=msg.example.com HTTPS_PORT=1337 ./setup-server.sh    # Alt syntax
# =============================================================================

set -euo pipefail

# Configuration
HTTPS_PORT="${1:-${HTTPS_PORT:-443}}"
DOMAIN="${DOMAIN:?Set DOMAIN environment variable, e.g.: sudo DOMAIN=msg.example.com ./setup-server.sh}"

# Colors for output
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
CYAN='\033[0;36m'
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
echo ""
warn "UFW firewall setup will reset all existing rules and only allow SSH (22), HTTP (80), and HTTPS (${HTTPS_PORT})."
warn "If your provider (e.g. AWS Lightsail) has its own firewall/security groups, enabling UFW may lock you out."
echo -e "${CYAN}[?]${NC} Configure UFW firewall rules? (y/N)"
read -r ufw_confirm
if [[ "${ufw_confirm}" =~ ^[Yy]$ ]]; then
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

    log "Firewall configured: SSH, HTTP, HTTPS (${HTTPS_PORT}) allowed"
else
    warn "Skipping UFW setup. Make sure your provider's firewall allows ports 22, 80, and ${HTTPS_PORT}."
fi

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
APP_DIR="${APP_DIR:-/opt/cryptonotes}"
log "Creating application directory at ${APP_DIR}..."
mkdir -p "${APP_DIR}"
cd "${APP_DIR}"

# =============================================================================
# 6. Generate secure token key
# =============================================================================
TOKEN_KEY=$(openssl rand -base64 48)
log "Generated secure token signing key"

# =============================================================================
# 7. Generate .env (secrets — not stored in repo)
# =============================================================================
log "Generating .env..."

cat > "${APP_DIR}/deploy/.env" << EOF
DOMAIN=${DOMAIN}
HTTPS_PORT=${HTTPS_PORT}
TOKEN_SIGNING_KEY=${TOKEN_KEY}
TOKEN_EXPIRY_HOURS=24
MAX_LOGIN_ATTEMPTS=5
MAX_MESSAGE_SIZE=65536
MIN_PASSWORD_LENGTH=8
MESSAGE_EXPIRY_DAYS=30
EOF

chmod 600 "${APP_DIR}/deploy/.env"
log ".env created"

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
echo "  2. Build and start: cd ${APP_DIR}/deploy && docker compose -f docker-compose.prod.yml up -d --build"
echo ""
echo "Configuration: ${APP_DIR}/deploy/.env"
echo "Logs: docker compose -f docker-compose.prod.yml logs -f"
echo ""
