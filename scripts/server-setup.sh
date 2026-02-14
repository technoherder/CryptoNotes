#!/usr/bin/env bash
# =============================================================================
# CryptoNotes Server Setup Script
# Hardens a fresh Ubuntu 22.04 server for hosting CryptoNotes.
#
# Usage:
#   sudo bash server-setup.sh
#
# This script:
#   1. Creates a dedicated service user
#   2. Hardens SSH (key-only, custom port, rate limiting)
#   3. Installs and configures Fail2Ban
#   4. Applies kernel hardening
#   5. Configures UFW firewall
#   6. Installs .NET Core runtime
#   7. Sets up automatic security updates
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

# ---------------------------------------------------------------------------
# Pre-flight checks
# ---------------------------------------------------------------------------
if [ "$EUID" -ne 0 ]; then
    err "This script must be run as root. Use: sudo bash server-setup.sh"
fi

if ! grep -qi 'ubuntu\|debian' /etc/os-release 2>/dev/null; then
    warn "This script is designed for Ubuntu/Debian. Other distros may need adjustments."
fi

echo ""
echo "============================================="
echo "  CryptoNotes Server Hardening Script"
echo "============================================="
echo ""
warn "This will make significant changes to your server."
warn "Make sure you have console access in case SSH locks you out."
echo ""

# ---------------------------------------------------------------------------
# Configuration prompts
# ---------------------------------------------------------------------------
SSH_PORT=2222
SERVICE_USER="cryptonotes"

ask "SSH port to use (default: 2222):"
read -r input_port
if [ -n "$input_port" ]; then
    SSH_PORT="$input_port"
fi

ask "Service user to create (default: cryptonotes):"
read -r input_user
if [ -n "$input_user" ]; then
    SERVICE_USER="$input_user"
fi

ask "Your SSH public key (paste it, or leave blank to skip):"
read -r SSH_PUBKEY

echo ""
log "Configuration:"
echo "  SSH Port:      $SSH_PORT"
echo "  Service User:  $SERVICE_USER"
echo ""

ask "Continue? (y/N)"
read -r confirm
if [ "$confirm" != "y" ] && [ "$confirm" != "Y" ]; then
    echo "Aborted."
    exit 0
fi

# ---------------------------------------------------------------------------
# Step 1: System update
# ---------------------------------------------------------------------------
log "Updating system packages..."
apt update -qq && apt upgrade -y -qq
apt install -y -qq \
    ufw fail2ban unattended-upgrades curl wget \
    apt-transport-https ca-certificates gnupg lsb-release \
    sqlite3 openssl

# ---------------------------------------------------------------------------
# Step 2: Create service user
# ---------------------------------------------------------------------------
if id "$SERVICE_USER" &>/dev/null; then
    log "User '$SERVICE_USER' already exists."
else
    log "Creating user '$SERVICE_USER'..."
    adduser --disabled-password --gecos "" "$SERVICE_USER"
    usermod -aG sudo "$SERVICE_USER"
fi

# Copy SSH key if provided
if [ -n "$SSH_PUBKEY" ]; then
    log "Installing SSH public key for '$SERVICE_USER'..."
    mkdir -p /home/$SERVICE_USER/.ssh
    echo "$SSH_PUBKEY" >> /home/$SERVICE_USER/.ssh/authorized_keys
    chmod 700 /home/$SERVICE_USER/.ssh
    chmod 600 /home/$SERVICE_USER/.ssh/authorized_keys
    chown -R $SERVICE_USER:$SERVICE_USER /home/$SERVICE_USER/.ssh
fi

# ---------------------------------------------------------------------------
# Step 3: SSH hardening
# ---------------------------------------------------------------------------
log "Hardening SSH configuration..."
cp /etc/ssh/sshd_config /etc/ssh/sshd_config.backup.$(date +%s)

cat > /etc/ssh/sshd_config.d/99-cryptonotes.conf << SSHEOF
Port $SSH_PORT
PermitRootLogin no
PasswordAuthentication no
PubkeyAuthentication yes
AllowUsers $SERVICE_USER
Protocol 2
MaxAuthTries 3
LoginGraceTime 30
AllowTcpForwarding no
X11Forwarding no
AllowAgentForwarding no
ClientAliveInterval 300
ClientAliveCountMax 2
PermitEmptyPasswords no
ChallengeResponseAuthentication no
UsePAM yes
SSHEOF

log "Testing SSH config..."
if sshd -t; then
    systemctl restart sshd
    log "SSH restarted on port $SSH_PORT."
else
    err "SSH config test failed. Check /etc/ssh/sshd_config.d/99-cryptonotes.conf"
fi

# ---------------------------------------------------------------------------
# Step 4: Fail2Ban
# ---------------------------------------------------------------------------
log "Configuring Fail2Ban..."
cat > /etc/fail2ban/jail.local << F2BEOF
[DEFAULT]
bantime  = 3600
findtime = 600
maxretry = 3
banaction = iptables-multiport

[sshd]
enabled  = true
port     = $SSH_PORT
logpath  = /var/log/auth.log
maxretry = 3
bantime  = 86400
F2BEOF

systemctl enable fail2ban
systemctl restart fail2ban
log "Fail2Ban configured (24h ban after 3 failed SSH attempts)."

# ---------------------------------------------------------------------------
# Step 5: Kernel hardening
# ---------------------------------------------------------------------------
log "Applying kernel hardening..."
cat > /etc/sysctl.d/99-cryptonotes-hardening.conf << SYSEOF
net.ipv4.conf.all.rp_filter = 1
net.ipv4.conf.default.rp_filter = 1
net.ipv4.conf.all.accept_source_route = 0
net.ipv6.conf.all.accept_source_route = 0
net.ipv4.conf.all.accept_redirects = 0
net.ipv6.conf.all.accept_redirects = 0
net.ipv4.conf.all.send_redirects = 0
net.ipv4.tcp_syncookies = 1
net.ipv4.tcp_max_syn_backlog = 2048
net.ipv4.tcp_synack_retries = 2
net.ipv4.conf.all.log_martians = 1
net.ipv4.conf.default.log_martians = 1
net.ipv4.icmp_echo_ignore_broadcasts = 1
net.ipv6.conf.all.disable_ipv6 = 1
net.ipv6.conf.default.disable_ipv6 = 1
SYSEOF
sysctl --system > /dev/null 2>&1
log "Kernel hardening applied."

# ---------------------------------------------------------------------------
# Step 6: UFW Firewall
# ---------------------------------------------------------------------------
log "Configuring UFW firewall..."
ufw default deny incoming
ufw default allow outgoing
ufw allow "$SSH_PORT/tcp" comment 'SSH'
ufw allow 80/tcp comment 'HTTP-redirect'
ufw allow 443/tcp comment 'HTTPS'
echo "y" | ufw enable
log "Firewall enabled (ports: $SSH_PORT, 80, 443)."

# ---------------------------------------------------------------------------
# Step 7: Install .NET Core Runtime
# ---------------------------------------------------------------------------
log "Installing .NET Core 3.1 runtime..."
wget -q https://dot.net/v1/dotnet-install.sh -O /tmp/dotnet-install.sh
chmod +x /tmp/dotnet-install.sh
sudo -u "$SERVICE_USER" bash /tmp/dotnet-install.sh --channel 3.1 --runtime aspnetcore --install-dir /home/$SERVICE_USER/.dotnet
rm /tmp/dotnet-install.sh

# Add to PATH permanently
sudo -u "$SERVICE_USER" bash -c 'echo "export DOTNET_ROOT=\$HOME/.dotnet" >> ~/.bashrc'
sudo -u "$SERVICE_USER" bash -c 'echo "export PATH=\$PATH:\$DOTNET_ROOT" >> ~/.bashrc'
log ".NET Core runtime installed."

# ---------------------------------------------------------------------------
# Step 8: Automatic security updates
# ---------------------------------------------------------------------------
log "Enabling automatic security updates..."
cat > /etc/apt/apt.conf.d/20auto-upgrades << AUTOEOF
APT::Periodic::Update-Package-Lists "1";
APT::Periodic::Unattended-Upgrade "1";
APT::Periodic::AutocleanInterval "7";
AUTOEOF

# ---------------------------------------------------------------------------
# Step 9: Create application directories
# ---------------------------------------------------------------------------
log "Creating application directories..."
sudo -u "$SERVICE_USER" mkdir -p /home/$SERVICE_USER/cryptonotes-server
sudo -u "$SERVICE_USER" mkdir -p /home/$SERVICE_USER/cryptonotes-data
sudo -u "$SERVICE_USER" mkdir -p /home/$SERVICE_USER/cryptonotes-backups
chmod 700 /home/$SERVICE_USER/cryptonotes-server
chmod 700 /home/$SERVICE_USER/cryptonotes-data
chmod 700 /home/$SERVICE_USER/cryptonotes-backups

# ---------------------------------------------------------------------------
# Done
# ---------------------------------------------------------------------------
echo ""
echo "============================================="
echo -e "  ${GREEN}Server hardening complete!${NC}"
echo "============================================="
echo ""
echo "  SSH port: $SSH_PORT"
echo "  User:     $SERVICE_USER"
echo ""
echo "  Next steps:"
echo "    1. Test SSH in a NEW terminal:"
echo "       ssh -p $SSH_PORT $SERVICE_USER@$(hostname -I | awk '{print $1}')"
echo ""
echo "    2. Run the deploy script:"
echo "       sudo -u $SERVICE_USER bash scripts/deploy.sh"
echo ""
echo "    3. Run the SSL setup script:"
echo "       sudo bash scripts/ssl-setup.sh"
echo ""
warn "DO NOT close this terminal until you verify SSH works!"
echo ""
