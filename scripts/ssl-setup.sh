#!/usr/bin/env bash
# =============================================================================
# CryptoNotes SSL & Apache Setup Script
# Sets up Let's Encrypt HTTPS + Apache reverse proxy.
#
# Usage:
#   sudo bash scripts/ssl-setup.sh
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

if [ "$EUID" -ne 0 ]; then
    err "This script must be run as root. Use: sudo bash scripts/ssl-setup.sh"
fi

echo ""
echo "============================================="
echo "  CryptoNotes SSL & Apache Setup"
echo "============================================="
echo ""

# ---------------------------------------------------------------------------
# Prompts
# ---------------------------------------------------------------------------
ask "Your domain name (e.g., msg.example.com):"
read -r DOMAIN
if [ -z "$DOMAIN" ]; then
    err "Domain name is required."
fi

ask "Email for Let's Encrypt notifications:"
read -r EMAIL
if [ -z "$EMAIL" ]; then
    err "Email is required for Let's Encrypt."
fi

echo ""
log "Domain:  $DOMAIN"
log "Email:   $EMAIL"
ask "Continue? (y/N)"
read -r confirm
if [ "$confirm" != "y" ] && [ "$confirm" != "Y" ]; then
    echo "Aborted."
    exit 0
fi

# ---------------------------------------------------------------------------
# Step 1: Install Apache and Certbot
# ---------------------------------------------------------------------------
log "Installing Apache and Certbot..."
apt install -y -qq apache2 certbot python3-certbot-apache

# Enable required Apache modules
a2enmod ssl proxy proxy_http headers rewrite
log "Apache modules enabled."

# ---------------------------------------------------------------------------
# Step 2: Disable default site
# ---------------------------------------------------------------------------
a2dissite 000-default 2>/dev/null || true
a2dissite default-ssl 2>/dev/null || true

# ---------------------------------------------------------------------------
# Step 3: Obtain SSL certificate
# ---------------------------------------------------------------------------
log "Obtaining Let's Encrypt certificate for $DOMAIN..."
log "This may take a moment..."

# Stop Apache temporarily so certbot can use port 80
systemctl stop apache2 2>/dev/null || true

certbot certonly --standalone \
    -d "$DOMAIN" \
    --email "$EMAIL" \
    --agree-tos \
    --no-eff-email \
    --rsa-key-size 4096 \
    --non-interactive

if [ ! -f "/etc/letsencrypt/live/$DOMAIN/fullchain.pem" ]; then
    err "Certificate generation failed. Check DNS and try again."
fi
log "SSL certificate obtained!"

# ---------------------------------------------------------------------------
# Step 4: Generate DH parameters
# ---------------------------------------------------------------------------
if [ ! -f /etc/ssl/certs/dhparam.pem ]; then
    log "Generating 4096-bit DH parameters (this takes several minutes)..."
    openssl dhparam -out /etc/ssl/certs/dhparam.pem 4096
    log "DH parameters generated."
else
    log "DH parameters already exist, skipping."
fi

# ---------------------------------------------------------------------------
# Step 5: Configure Apache virtual host
# ---------------------------------------------------------------------------
log "Configuring Apache reverse proxy..."

cat > /etc/apache2/sites-available/cryptonotes.conf << APACHEEOF
# HTTP -> HTTPS redirect
<VirtualHost *:80>
    ServerName $DOMAIN
    RewriteEngine On
    RewriteCond %{HTTPS} off
    RewriteRule ^(.*)$ https://%{HTTP_HOST}%{REQUEST_URI} [L,R=301]
</VirtualHost>

# HTTPS
<VirtualHost *:443>
    ServerName $DOMAIN

    # TLS
    SSLEngine on
    SSLCertificateFile      /etc/letsencrypt/live/$DOMAIN/fullchain.pem
    SSLCertificateKeyFile   /etc/letsencrypt/live/$DOMAIN/privkey.pem
    SSLProtocol             all -SSLv2 -SSLv3 -TLSv1 -TLSv1.1
    SSLCipherSuite          ECDHE-ECDSA-AES128-GCM-SHA256:ECDHE-RSA-AES128-GCM-SHA256:ECDHE-ECDSA-AES256-GCM-SHA384:ECDHE-RSA-AES256-GCM-SHA384:ECDHE-ECDSA-CHACHA20-POLY1305:ECDHE-RSA-CHACHA20-POLY1305:DHE-RSA-AES128-GCM-SHA256:DHE-RSA-AES256-GCM-SHA384
    SSLHonorCipherOrder     off
    SSLSessionTickets       off
    SSLOpenSSLConfCmd DHParameters "/etc/ssl/certs/dhparam.pem"
    SSLUseStapling          on
    SSLStaplingResponderTimeout 5
    SSLStaplingReturnResponderErrors off

    # Security headers
    Header always set Strict-Transport-Security "max-age=63072000; includeSubDomains; preload"
    Header always set X-Content-Type-Options "nosniff"
    Header always set X-Frame-Options "DENY"
    Header always set X-XSS-Protection "1; mode=block"
    Header always set Referrer-Policy "no-referrer"
    Header always set Content-Security-Policy "default-src 'none'; frame-ancestors 'none'"
    Header always set Permissions-Policy "camera=(), microphone=(), geolocation=()"
    Header always set Cache-Control "no-store, no-cache, must-revalidate"
    Header always unset "X-Powered-By"

    ServerSignature Off

    # Reverse proxy to Kestrel
    ProxyPreserveHost On
    ProxyPass         /api http://127.0.0.1:5000/api
    ProxyPassReverse  /api http://127.0.0.1:5000/api

    # Only allow /api and /health routes
    <Location "/">
        Require all denied
    </Location>
    <Location "/api">
        Require all granted
    </Location>
    <Location "/health">
        Require all granted
    </Location>

    # Request limits
    LimitRequestBody 65536
    LimitRequestFieldSize 8190
    LimitRequestFields 50
    TimeOut 30

    # Logging (no request bodies - they contain encrypted data)
    ErrorLog \${APACHE_LOG_DIR}/cryptonotes-error.log
    CustomLog \${APACHE_LOG_DIR}/cryptonotes-access.log combined
</VirtualHost>

SSLStaplingCache shmcb:/var/run/ocsp(128000)
APACHEEOF

# ---------------------------------------------------------------------------
# Step 6: Harden global Apache config
# ---------------------------------------------------------------------------
log "Hardening Apache global config..."

# Ensure security.conf has proper settings
cat > /etc/apache2/conf-available/cryptonotes-security.conf << SECEOF
ServerTokens Prod
ServerSignature Off
TraceEnable Off

<Directory />
    Options -Indexes -FollowSymLinks
    AllowOverride None
    Require all denied
</Directory>
SECEOF

a2enconf cryptonotes-security 2>/dev/null || true

# ---------------------------------------------------------------------------
# Step 7: Enable and test
# ---------------------------------------------------------------------------
log "Enabling site and testing config..."
a2ensite cryptonotes

if apache2ctl configtest 2>&1 | grep -q "Syntax OK"; then
    log "Apache config test passed."
else
    err "Apache config test failed. Check /etc/apache2/sites-available/cryptonotes.conf"
fi

systemctl restart apache2
log "Apache started."

# ---------------------------------------------------------------------------
# Step 8: Auto-renewal cron
# ---------------------------------------------------------------------------
log "Setting up certificate auto-renewal..."
(crontab -l 2>/dev/null | grep -v certbot; echo "0 3 * * * certbot renew --quiet --deploy-hook 'systemctl reload apache2'") | crontab -
log "Auto-renewal cron installed (runs daily at 3 AM)."

# ---------------------------------------------------------------------------
# Step 9: Verify
# ---------------------------------------------------------------------------
echo ""
log "Testing HTTPS connection..."
sleep 2
HTTP_CODE=$(curl -s -o /dev/null -w "%{http_code}" "https://$DOMAIN/api/auth/login" \
    -H "Content-Type: application/json" \
    -d '{"username":"test","password":"test"}' 2>/dev/null || echo "000")

if [ "$HTTP_CODE" = "400" ] || [ "$HTTP_CODE" = "401" ] || [ "$HTTP_CODE" = "415" ]; then
    log "HTTPS is working! (Got HTTP $HTTP_CODE from API)"
elif [ "$HTTP_CODE" = "000" ]; then
    warn "Could not connect. Make sure the CryptoNotes service is running:"
    warn "  sudo systemctl start cryptonotes"
else
    warn "Got unexpected HTTP $HTTP_CODE. Check Apache and Kestrel logs."
fi

echo ""
echo "============================================="
echo -e "  ${GREEN}SSL & Apache setup complete!${NC}"
echo "============================================="
echo ""
echo "  Your server URL: https://$DOMAIN"
echo ""
echo "  Test your SSL rating:"
echo "    https://www.ssllabs.com/ssltest/analyze.html?d=$DOMAIN"
echo ""
echo "  Test security headers:"
echo "    https://securityheaders.com/?q=$DOMAIN"
echo ""
echo "  Certificate auto-renews every 90 days."
echo ""
