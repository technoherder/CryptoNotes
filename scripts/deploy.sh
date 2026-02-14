#!/usr/bin/env bash
# =============================================================================
# CryptoNotes Deploy Script
# Builds and deploys the CryptoNotes server application.
#
# Usage (from the project root):
#   bash scripts/deploy.sh [--first-run]
#
# Options:
#   --first-run   Initial deployment (generates config, installs systemd service)
#   (no flags)    Update deployment (rebuilds and restarts)
# =============================================================================

set -euo pipefail

RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
CYAN='\033[0;36m'
NC='\033[0m'

log()  { echo -e "${GREEN}[+]${NC} $1"; }
warn() { echo -e "${YELLOW}[!]${NC} $1"; }
err()  { echo -e "${RED}[ERROR]${NC} $1"; exit 1; }
ask()  { echo -e "${CYAN}[?]${NC} $1"; }

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
PROJECT_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
SERVER_SRC="$PROJECT_ROOT/CryptoNotes.Server"
DEPLOY_DIR="$HOME/cryptonotes-server"
DATA_DIR="$HOME/cryptonotes-data"
FIRST_RUN=false

for arg in "$@"; do
    case "$arg" in
        --first-run) FIRST_RUN=true ;;
    esac
done

echo ""
echo "============================================="
echo "  CryptoNotes Deployment"
echo "============================================="
echo ""

# ---------------------------------------------------------------------------
# Check prerequisites
# ---------------------------------------------------------------------------
if [ ! -d "$SERVER_SRC" ]; then
    err "Server source not found at $SERVER_SRC"
fi

DOTNET="$HOME/.dotnet/dotnet"
if [ ! -f "$DOTNET" ]; then
    # Try system dotnet
    DOTNET="$(which dotnet 2>/dev/null || true)"
    if [ -z "$DOTNET" ]; then
        err ".NET SDK/runtime not found. Run server-setup.sh first or install .NET."
    fi
fi

# Check if we have the SDK for building
if ! "$DOTNET" --list-sdks 2>/dev/null | grep -q "3.1\|5.\|6.\|7.\|8."; then
    log "Installing .NET SDK for building..."
    wget -q https://dot.net/v1/dotnet-install.sh -O /tmp/dotnet-install.sh
    chmod +x /tmp/dotnet-install.sh
    bash /tmp/dotnet-install.sh --channel 3.1
    rm /tmp/dotnet-install.sh
    DOTNET="$HOME/.dotnet/dotnet"
fi

# ---------------------------------------------------------------------------
# Build
# ---------------------------------------------------------------------------
log "Building CryptoNotes.Server (Release)..."
cd "$SERVER_SRC"
"$DOTNET" restore --verbosity quiet
"$DOTNET" publish -c Release -o "$DEPLOY_DIR" --verbosity quiet
log "Build complete -> $DEPLOY_DIR"

# ---------------------------------------------------------------------------
# First-run configuration
# ---------------------------------------------------------------------------
if [ "$FIRST_RUN" = true ]; then
    log "First-run setup..."

    # Generate token signing key
    TOKEN_KEY=$(openssl rand -base64 48)
    log "Generated secure token signing key."

    # Create production config
    cat > "$DEPLOY_DIR/appsettings.Production.json" << CONFIGEOF
{
  "Logging": {
    "LogLevel": {
      "Default": "Warning",
      "Microsoft": "Warning"
    }
  },
  "Security": {
    "TokenSigningKey": "$TOKEN_KEY",
    "TokenExpiryHours": 24,
    "MaxLoginAttemptsPerMinute": 5,
    "MaxMessageSizeBytes": 65536,
    "MinPasswordLength": 8
  }
}
CONFIGEOF

    chmod 600 "$DEPLOY_DIR/appsettings.Production.json"
    log "Production config created with secure random key."

    # Create data directory
    mkdir -p "$DATA_DIR"
    chmod 700 "$DATA_DIR"

    # Install systemd service
    if [ "$EUID" -eq 0 ] || sudo -n true 2>/dev/null; then
        log "Installing systemd service..."

        SERVICE_USER="$(whoami)"
        DOTNET_PATH="$(dirname "$DOTNET")"

        sudo tee /etc/systemd/system/cryptonotes.service > /dev/null << SERVICEEOF
[Unit]
Description=CryptoNotes E2E Encrypted Messaging Server
After=network.target

[Service]
User=$SERVICE_USER
Group=$SERVICE_USER
WorkingDirectory=$DEPLOY_DIR
ExecStart=$DOTNET $DEPLOY_DIR/CryptoNotes.Server.dll
Environment=ASPNETCORE_ENVIRONMENT=Production
Environment=ASPNETCORE_URLS=http://127.0.0.1:5000
Environment=DOTNET_CLI_TELEMETRY_OPTOUT=1
Restart=always
RestartSec=10
NoNewPrivileges=true
ProtectSystem=strict
ProtectHome=read-only
ReadWritePaths=$DEPLOY_DIR
ReadWritePaths=$DATA_DIR
PrivateTmp=true
ProtectKernelTunables=true
ProtectKernelModules=true
ProtectControlGroups=true
RestrictSUIDSGID=true
RestrictNamespaces=true
LockPersonality=true
MemoryMax=256M
CPUQuota=80%
StandardOutput=journal
StandardError=journal
SyslogIdentifier=cryptonotes

[Install]
WantedBy=multi-user.target
SERVICEEOF

        sudo systemctl daemon-reload
        sudo systemctl enable cryptonotes
        log "Systemd service installed and enabled."
    else
        warn "No sudo access - skipping systemd service install."
        warn "You'll need to manually create the systemd service."
    fi

    echo ""
    echo "============================================="
    echo -e "  ${GREEN}First-run deployment complete!${NC}"
    echo "============================================="
    echo ""
    echo "  Deploy dir:  $DEPLOY_DIR"
    echo ""
    echo "  Start the server:"
    echo "    sudo systemctl start cryptonotes"
    echo ""
    echo "  Or run manually:"
    echo "    ASPNETCORE_ENVIRONMENT=Production \\"
    echo "    ASPNETCORE_URLS=http://127.0.0.1:5000 \\"
    echo "    $DOTNET $DEPLOY_DIR/CryptoNotes.Server.dll"
    echo ""
    echo "  Next: run scripts/ssl-setup.sh to configure HTTPS"
    echo ""

else
    # ---------------------------------------------------------------------------
    # Update deployment
    # ---------------------------------------------------------------------------
    log "Updating deployment..."

    # Stop the service
    if systemctl is-active --quiet cryptonotes 2>/dev/null; then
        log "Stopping CryptoNotes service..."
        sudo systemctl stop cryptonotes
    fi

    # Backup database
    if [ -f "$DEPLOY_DIR/cryptonotes.db" ]; then
        BACKUP_FILE="$HOME/cryptonotes-backups/pre-update-$(date +%Y%m%d-%H%M%S).db"
        cp "$DEPLOY_DIR/cryptonotes.db" "$BACKUP_FILE"
        log "Database backed up to $BACKUP_FILE"
    fi

    # Restart
    if systemctl is-enabled --quiet cryptonotes 2>/dev/null; then
        sudo systemctl start cryptonotes
        sleep 2
        if systemctl is-active --quiet cryptonotes; then
            log "CryptoNotes service restarted successfully."
        else
            err "Service failed to start. Check: sudo journalctl -u cryptonotes -n 30"
        fi
    fi

    echo ""
    echo -e "${GREEN}Update deployment complete!${NC}"
    echo ""
fi
