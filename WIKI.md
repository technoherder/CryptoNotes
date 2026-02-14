# CryptoNotes Secure Deployment Wiki

Complete guide for securely deploying the CryptoNotes end-to-end encrypted messaging platform.

---

## Table of Contents

1. [Architecture Overview](#1-architecture-overview)
2. [Prerequisites](#2-prerequisites)
3. [Part 1: Linux Server Setup and Hardening](#3-part-1-linux-server-setup-and-hardening)
4. [Part 2: Domain and DNS Setup](#4-part-2-domain-and-dns-setup)
5. [Part 3: Compile and Deploy the Server](#5-part-3-compile-and-deploy-the-server)
6. [Part 4: HTTPS with Let's Encrypt](#6-part-4-https-with-lets-encrypt)
7. [Part 5: Apache Reverse Proxy Configuration](#7-part-5-apache-reverse-proxy-configuration)
8. [Part 6: Systemd Service for Auto-Start](#8-part-6-systemd-service-for-auto-start)
9. [Part 7: Firewall Configuration](#9-part-7-firewall-configuration)
10. [Part 8: Server Security Configuration](#10-part-8-server-security-configuration)
11. [Part 9: Building the Mobile Clients](#11-part-9-building-the-mobile-clients)
12. [Part 10: Client Setup Guide for Users](#12-part-10-client-setup-guide-for-users)
13. [Part 11: Ongoing Maintenance](#13-part-11-ongoing-maintenance)
14. [Part 12: Security Architecture Reference](#14-part-12-security-architecture-reference)
15. [Troubleshooting](#15-troubleshooting)
16. [Automation Scripts](#16-automation-scripts)
17. [Docker Deployment](#17-docker-deployment)
18. [Security Improvements (v2)](#18-security-improvements-v2)

---

## 1. Architecture Overview

```
+------------------+         HTTPS (TLS 1.2+)        +-------------------+
|  Mobile Client   | <-----------------------------> |   Linux Server    |
|  (Android/iOS)   |    PGP-encrypted messages only   |                   |
|                  |                                   |  Apache (443)     |
|  - PGP keygen    |                                   |    |              |
|  - AES-256 local |                                   |    v              |
|    encryption    |                                   |  Kestrel (5000)   |
|  - App password  |                                   |    |              |
|  - Auto-wipe     |                                   |    v              |
+------------------+                                   |  SQLite DB        |
                                                       |  (encrypted msgs) |
                                                       +-------------------+
```

**Key principle**: The server is a *relay only*. It never sees plaintext messages. All encryption/decryption happens on the client device. Even if the server is fully compromised, message contents remain protected by PGP encryption.

**What the server stores**:
- Usernames and bcrypt-hashed passwords
- PGP public keys (public by design)
- PGP-encrypted message blobs (ciphertext only)

**What the server never has access to**:
- Private PGP keys
- Plaintext messages
- App lock passwords
- AES data encryption keys

---

## 2. Prerequisites

**Server requirements**:
- A Linux VPS or dedicated server (Ubuntu 22.04 LTS recommended)
- Minimum 1 CPU, 512MB RAM, 10GB disk
- Root or sudo access
- A registered domain name

**Development machine requirements** (for compiling):
- .NET Core SDK 3.1 or later (for the server)
- Visual Studio 2019+ with Xamarin workload (for the mobile clients)
- macOS required for iOS builds (Xcode + Apple Developer account)

**Client requirements**:
- Android 9.0+ or iOS 13+
- Internet connection to reach the server

---

## 3. Part 1: Linux Server Setup and Hardening

### 3.1 Initial Server Setup

After provisioning a fresh Ubuntu 22.04 server, connect via SSH:

```bash
ssh root@your-server-ip
```

#### Update the system immediately

```bash
apt update && apt upgrade -y
apt install -y unattended-upgrades
dpkg-reconfigure -plow unattended-upgrades
```

#### Create a non-root user

Never run the application as root.

```bash
adduser cryptonotes
usermod -aG sudo cryptonotes
```

#### Set a strong password

```bash
passwd cryptonotes
# Use a 20+ character password with mixed characters
```

### 3.2 SSH Hardening

#### Generate an SSH key pair on your LOCAL machine (not the server)

```bash
# On your local machine:
ssh-keygen -t ed25519 -C "cryptonotes-admin"
ssh-copy-id -i ~/.ssh/id_ed25519.pub cryptonotes@your-server-ip
```

#### Lock down SSH configuration

Edit the SSH daemon config on the server:

```bash
sudo nano /etc/ssh/sshd_config
```

Apply these settings:

```
# Disable root login
PermitRootLogin no

# Disable password authentication (key-only)
PasswordAuthentication no
PubkeyAuthentication yes

# Limit SSH to your admin user
AllowUsers cryptonotes

# Use only SSH protocol 2
Protocol 2

# Change default port (choose a random high port)
Port 2222

# Limit authentication attempts
MaxAuthTries 3
LoginGraceTime 30

# Disable forwarding (not needed)
AllowTcpForwarding no
X11Forwarding no
AllowAgentForwarding no

# Idle timeout (10 minutes)
ClientAliveInterval 300
ClientAliveCountMax 2
```

Restart SSH:

```bash
sudo systemctl restart sshd
```

**Important**: Test that you can log in with your key on port 2222 in a NEW terminal before closing your current session:

```bash
ssh -p 2222 cryptonotes@your-server-ip
```

### 3.3 Install Fail2Ban

Fail2Ban automatically bans IPs that show malicious behavior:

```bash
sudo apt install -y fail2ban
```

Create the local config:

```bash
sudo nano /etc/fail2ban/jail.local
```

```ini
[DEFAULT]
bantime  = 3600
findtime = 600
maxretry = 3
banaction = iptables-multiport

[sshd]
enabled = true
port    = 2222
logpath = /var/log/auth.log
maxretry = 3
bantime = 86400
```

```bash
sudo systemctl enable fail2ban
sudo systemctl start fail2ban
```

### 3.4 Kernel Hardening

```bash
sudo nano /etc/sysctl.d/99-cryptonotes-hardening.conf
```

```ini
# Prevent IP spoofing
net.ipv4.conf.all.rp_filter = 1
net.ipv4.conf.default.rp_filter = 1

# Disable source routing
net.ipv4.conf.all.accept_source_route = 0
net.ipv6.conf.all.accept_source_route = 0

# Disable ICMP redirects
net.ipv4.conf.all.accept_redirects = 0
net.ipv6.conf.all.accept_redirects = 0
net.ipv4.conf.all.send_redirects = 0

# Enable SYN flood protection
net.ipv4.tcp_syncookies = 1
net.ipv4.tcp_max_syn_backlog = 2048
net.ipv4.tcp_synack_retries = 2

# Log suspicious packets
net.ipv4.conf.all.log_martians = 1
net.ipv4.conf.default.log_martians = 1

# Ignore ICMP broadcasts
net.ipv4.icmp_echo_ignore_broadcasts = 1

# Disable IPv6 if not needed
net.ipv6.conf.all.disable_ipv6 = 1
net.ipv6.conf.default.disable_ipv6 = 1
```

Apply:

```bash
sudo sysctl --system
```

### 3.5 Disable Unnecessary Services

```bash
# List running services
sudo systemctl list-units --type=service --state=running

# Disable anything you don't need, for example:
sudo systemctl disable --now cups
sudo systemctl disable --now avahi-daemon
sudo systemctl disable --now bluetooth
```

### 3.6 Set Up Automatic Security Updates

```bash
sudo nano /etc/apt/apt.conf.d/50unattended-upgrades
```

Ensure these lines are uncommented:

```
Unattended-Upgrade::Allowed-Origins {
    "${distro_id}:${distro_codename}-security";
};
Unattended-Upgrade::AutoFixInterruptedDpkg "true";
Unattended-Upgrade::Remove-Unused-Dependencies "true";
```

---

## 4. Part 2: Domain and DNS Setup

### 4.1 Register a Domain

Purchase a domain from a privacy-respecting registrar:
- **Njalla** (anonymous registration)
- **Namecheap** (supports WhoisGuard privacy)
- **Cloudflare Registrar** (at-cost pricing)

Enable WHOIS privacy protection if available.

### 4.2 Configure DNS Records

In your registrar's DNS panel, create:

| Type | Name | Value | TTL |
|------|------|-------|-----|
| A | `msg` (or `@`) | `YOUR_SERVER_IP` | 300 |
| AAAA | `msg` (or `@`) | `YOUR_IPV6` (if applicable) | 300 |
| CAA | `@` | `0 issue "letsencrypt.org"` | 3600 |

The CAA record restricts which certificate authorities can issue certificates for your domain, preventing unauthorized certificate issuance.

Example: If your domain is `example.com`, you would create `msg.example.com` pointing to your server IP. Your server URL would be `https://msg.example.com`.

### 4.3 Verify DNS Propagation

```bash
# From your local machine:
dig msg.example.com +short
# Should return your server IP

# Or use:
nslookup msg.example.com
```

Wait for DNS propagation (can take up to 48 hours, usually minutes).

---

## 5. Part 3: Compile and Deploy the Server

### 5.1 Install .NET Core SDK on the Server

```bash
# Switch to your cryptonotes user
su - cryptonotes

# Install .NET SDK
wget https://dot.net/v1/dotnet-install.sh
chmod +x dotnet-install.sh
./dotnet-install.sh --channel 3.1

# Add to PATH
echo 'export DOTNET_ROOT=$HOME/.dotnet' >> ~/.bashrc
echo 'export PATH=$PATH:$DOTNET_ROOT:$DOTNET_ROOT/tools' >> ~/.bashrc
source ~/.bashrc

# Verify
dotnet --version
```

### 5.2 Transfer and Compile the Server Code

**Option A: Clone from your repository**

```bash
cd ~
git clone https://github.com/YOUR_USERNAME/CryptoNotes.git
cd CryptoNotes/CryptoNotes.Server
```

**Option B: SCP from your local machine**

```bash
# On your local machine:
scp -P 2222 -r CryptoNotes.Server/ cryptonotes@your-server-ip:~/
```

#### Compile for production

```bash
cd ~/CryptoNotes/CryptoNotes.Server

# Restore dependencies
dotnet restore

# Build release version
dotnet publish -c Release -o ~/cryptonotes-server
```

This creates the compiled server in `~/cryptonotes-server/`.

### 5.3 Generate a Strong Token Signing Key

This is critical. The default key in `appsettings.json` is a placeholder.

```bash
# Generate a cryptographically random 64-character key
openssl rand -base64 48
```

This outputs something like:
```
k3Jf8x9qR2vB7nM1pL4wS6yT0uI3oA5dH8gF2jK9lZ7xC1vN4mQ6wE0rY3tU5i
```

### 5.4 Configure Production Settings

Create a production settings file that overrides the defaults:

```bash
nano ~/cryptonotes-server/appsettings.Production.json
```

```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Warning",
      "Microsoft": "Warning"
    }
  },
  "Security": {
    "TokenSigningKey": "PASTE-YOUR-64-CHAR-RANDOM-KEY-FROM-STEP-5.3-HERE",
    "TokenExpiryHours": 24,
    "MaxLoginAttemptsPerMinute": 5,
    "MaxMessageSizeBytes": 65536,
    "MinPasswordLength": 8
  }
}
```

#### Protect the config file

```bash
chmod 600 ~/cryptonotes-server/appsettings.Production.json
```

### 5.5 Set Restrictive File Permissions

```bash
# Only the cryptonotes user can access the server files
chmod 700 ~/cryptonotes-server
chmod 600 ~/cryptonotes-server/appsettings*.json

# Create a dedicated directory for the database
mkdir -p ~/cryptonotes-data
chmod 700 ~/cryptonotes-data
```

### 5.6 Test the Server

```bash
cd ~/cryptonotes-server
ASPNETCORE_ENVIRONMENT=Production \
ASPNETCORE_URLS="http://127.0.0.1:5000" \
dotnet CryptoNotes.Server.dll
```

In another terminal:

```bash
curl http://127.0.0.1:5000/api/auth/login \
  -H "Content-Type: application/json" \
  -d '{"username":"test","password":"test"}'
```

You should get a 401 Unauthorized response (which is correct - it means the server is running). Press `Ctrl+C` to stop the test.

---

## 6. Part 4: HTTPS with Let's Encrypt

### 6.1 Install Certbot

```bash
sudo apt install -y certbot
```

### 6.2 Obtain a Certificate

Stop any service on port 80 temporarily, then:

```bash
sudo certbot certonly --standalone \
  -d msg.example.com \
  --email your-email@example.com \
  --agree-tos \
  --no-eff-email \
  --rsa-key-size 4096
```

This places certificates at:
- Certificate: `/etc/letsencrypt/live/msg.example.com/fullchain.pem`
- Private key: `/etc/letsencrypt/live/msg.example.com/privkey.pem`

### 6.3 Set Up Auto-Renewal

Let's Encrypt certificates expire every 90 days. Set up automatic renewal:

```bash
sudo crontab -e
```

Add:

```
0 3 * * * certbot renew --quiet --deploy-hook "systemctl reload apache2"
```

Test the renewal process:

```bash
sudo certbot renew --dry-run
```

### 6.4 Strengthen TLS Configuration

Create a strong Diffie-Hellman parameter file (this takes a few minutes):

```bash
sudo openssl dhparam -out /etc/ssl/certs/dhparam.pem 4096
```

---

## 7. Part 5: Apache Reverse Proxy Configuration

Apache sits in front of Kestrel, handling TLS termination, request filtering, and serving as a security layer.

### 7.1 Install Apache

```bash
sudo apt install -y apache2
sudo a2enmod ssl proxy proxy_http headers rewrite
```

### 7.2 Disable the Default Site

```bash
sudo a2dissite 000-default
```

### 7.3 Create the CryptoNotes Virtual Host

```bash
sudo nano /etc/apache2/sites-available/cryptonotes.conf
```

```apache
# HTTP -> HTTPS redirect
<VirtualHost *:80>
    ServerName msg.example.com

    # Redirect ALL HTTP traffic to HTTPS
    RewriteEngine On
    RewriteCond %{HTTPS} off
    RewriteRule ^(.*)$ https://%{HTTP_HOST}%{REQUEST_URI} [L,R=301]
</VirtualHost>

# HTTPS virtual host
<VirtualHost *:443>
    ServerName msg.example.com

    # =========================================
    # TLS Configuration (A+ rating on SSL Labs)
    # =========================================
    SSLEngine on
    SSLCertificateFile      /etc/letsencrypt/live/msg.example.com/fullchain.pem
    SSLCertificateKeyFile   /etc/letsencrypt/live/msg.example.com/privkey.pem

    # Strong protocol and cipher configuration
    SSLProtocol             all -SSLv2 -SSLv3 -TLSv1 -TLSv1.1
    SSLCipherSuite          ECDHE-ECDSA-AES128-GCM-SHA256:ECDHE-RSA-AES128-GCM-SHA256:ECDHE-ECDSA-AES256-GCM-SHA384:ECDHE-RSA-AES256-GCM-SHA384:ECDHE-ECDSA-CHACHA20-POLY1305:ECDHE-RSA-CHACHA20-POLY1305:DHE-RSA-AES128-GCM-SHA256:DHE-RSA-AES256-GCM-SHA384
    SSLHonorCipherOrder     off
    SSLSessionTickets       off

    # DH parameters
    SSLOpenSSLConfCmd DHParameters "/etc/ssl/certs/dhparam.pem"

    # OCSP Stapling
    SSLUseStapling          on
    SSLStaplingResponderTimeout 5
    SSLStaplingReturnResponderErrors off

    # =========================================
    # Security Headers
    # =========================================
    Header always set Strict-Transport-Security "max-age=63072000; includeSubDomains; preload"
    Header always set X-Content-Type-Options "nosniff"
    Header always set X-Frame-Options "DENY"
    Header always set X-XSS-Protection "1; mode=block"
    Header always set Referrer-Policy "no-referrer"
    Header always set Content-Security-Policy "default-src 'none'; frame-ancestors 'none'"
    Header always set Permissions-Policy "camera=(), microphone=(), geolocation=()"
    Header always set Cache-Control "no-store, no-cache, must-revalidate"

    # Remove server version info
    ServerSignature Off
    Header always unset "X-Powered-By"

    # =========================================
    # Reverse Proxy to Kestrel
    # =========================================
    ProxyPreserveHost On
    ProxyPass         /api http://127.0.0.1:5000/api
    ProxyPassReverse  /api http://127.0.0.1:5000/api

    # Only proxy /api routes - reject everything else
    <Location "/">
        Require all denied
    </Location>
    <Location "/api">
        Require all granted
    </Location>

    # =========================================
    # Request Limits
    # =========================================
    # Max request body size: 64KB
    LimitRequestBody 65536

    # Max request header size: 8KB
    LimitRequestFieldSize 8190
    LimitRequestFields 50

    # Timeout: 30 seconds
    TimeOut 30

    # =========================================
    # Logging
    # =========================================
    ErrorLog ${APACHE_LOG_DIR}/cryptonotes-error.log
    CustomLog ${APACHE_LOG_DIR}/cryptonotes-access.log combined

    # Do NOT log request bodies (they contain encrypted messages)
    # Only log IP, timestamp, method, URI, status code
</VirtualHost>

# OCSP Stapling cache
SSLStaplingCache shmcb:/var/run/ocsp(128000)
```

### 7.4 Harden Global Apache Config

```bash
sudo nano /etc/apache2/conf-available/security.conf
```

Set:

```apache
# Hide Apache version
ServerTokens Prod
ServerSignature Off

# Disable TRACE method (prevents XST attacks)
TraceEnable Off

# Disable directory listing
<Directory />
    Options -Indexes -FollowSymLinks
    AllowOverride None
    Require all denied
</Directory>
```

### 7.5 Enable and Test

```bash
sudo a2ensite cryptonotes
sudo apache2ctl configtest
sudo systemctl restart apache2
```

If `configtest` says "Syntax OK", proceed. Otherwise, fix any errors it reports.

---

## 8. Part 6: Systemd Service for Auto-Start

Create a systemd service so the server starts automatically and restarts on crashes.

### 8.1 Create the Service File

```bash
sudo nano /etc/systemd/system/cryptonotes.service
```

```ini
[Unit]
Description=CryptoNotes E2E Encrypted Messaging Server
After=network.target

[Service]
# Run as the dedicated user, never as root
User=cryptonotes
Group=cryptonotes

# Working directory
WorkingDirectory=/home/cryptonotes/cryptonotes-server

# Start command - only listen on localhost (Apache handles external traffic)
ExecStart=/home/cryptonotes/.dotnet/dotnet /home/cryptonotes/cryptonotes-server/CryptoNotes.Server.dll

# Environment
Environment=ASPNETCORE_ENVIRONMENT=Production
Environment=ASPNETCORE_URLS=http://127.0.0.1:5000
Environment=DOTNET_CLI_TELEMETRY_OPTOUT=1

# Restart on failure
Restart=always
RestartSec=10

# Security: restrict what the service can do
NoNewPrivileges=true
ProtectSystem=strict
ProtectHome=read-only
ReadWritePaths=/home/cryptonotes/cryptonotes-server
ReadWritePaths=/home/cryptonotes/cryptonotes-data
PrivateTmp=true
ProtectKernelTunables=true
ProtectKernelModules=true
ProtectControlGroups=true
RestrictSUIDSGID=true
RestrictNamespaces=true
LockPersonality=true

# Resource limits
MemoryMax=256M
CPUQuota=80%

# Logging
StandardOutput=journal
StandardError=journal
SyslogIdentifier=cryptonotes

[Install]
WantedBy=multi-user.target
```

### 8.2 Enable and Start

```bash
sudo systemctl daemon-reload
sudo systemctl enable cryptonotes
sudo systemctl start cryptonotes

# Check status
sudo systemctl status cryptonotes

# View logs
sudo journalctl -u cryptonotes -f
```

---

## 9. Part 7: Firewall Configuration

### 9.1 Configure UFW

```bash
# Reset to defaults
sudo ufw default deny incoming
sudo ufw default allow outgoing

# Allow SSH on your custom port
sudo ufw allow 2222/tcp comment 'SSH'

# Allow HTTP (for Let's Encrypt and redirect)
sudo ufw allow 80/tcp comment 'HTTP redirect'

# Allow HTTPS
sudo ufw allow 443/tcp comment 'HTTPS'

# Enable the firewall
sudo ufw enable

# Verify
sudo ufw status verbose
```

**Expected output:**

```
Status: active

To                         Action      From
--                         ------      ----
2222/tcp                   ALLOW       Anywhere        # SSH
80/tcp                     ALLOW       Anywhere        # HTTP redirect
443/tcp                    ALLOW       Anywhere        # HTTPS
```

Note: Port 5000 is NOT exposed. Kestrel only listens on `127.0.0.1:5000` (localhost). External traffic goes through Apache on port 443.

### 9.2 Optional: Rate Limit SSH

```bash
sudo ufw limit 2222/tcp comment 'Rate limit SSH'
```

---

## 10. Part 8: Server Security Configuration

### 10.1 Token Signing Key

The most critical server-side secret. Generate and set it as described in [Section 5.3](#53-generate-a-strong-token-signing-key).

**Never**:
- Use the default placeholder key
- Commit the production key to git
- Share the key with anyone
- Use the same key across environments

### 10.2 Recommended Production Settings

Edit `~/cryptonotes-server/appsettings.Production.json`:

```json
{
  "Security": {
    "TokenSigningKey": "YOUR-RANDOM-64-CHAR-KEY",
    "TokenExpiryHours": 24,
    "MaxLoginAttemptsPerMinute": 5,
    "MaxMessageSizeBytes": 65536,
    "MinPasswordLength": 8
  }
}
```

| Setting | Description | Recommended |
|---------|-------------|-------------|
| `TokenSigningKey` | HMAC key for auth tokens | 64+ random chars |
| `TokenExpiryHours` | How long tokens are valid | 24 (re-login daily) |
| `MaxLoginAttemptsPerMinute` | Rate limit per IP | 5 |
| `MaxMessageSizeBytes` | Max encrypted message size | 65536 (64KB) |
| `MinPasswordLength` | Minimum account password | 8+ |

### 10.3 Database Permissions

The SQLite database should only be readable by the service user:

```bash
chmod 600 ~/cryptonotes-server/cryptonotes.db
```

### 10.4 Log Monitoring

Set up log monitoring to detect attacks:

```bash
# Watch for failed login attempts
sudo journalctl -u cryptonotes | grep -i "unauthorized"

# Watch Apache for suspicious requests
sudo tail -f /var/log/apache2/cryptonotes-access.log

# Watch Fail2Ban actions
sudo fail2ban-client status sshd
```

### 10.5 Optional: AppArmor Profile

For additional isolation, create an AppArmor profile:

```bash
sudo nano /etc/apparmor.d/cryptonotes
```

```
#include <tunables/global>

/home/cryptonotes/cryptonotes-server/CryptoNotes.Server.dll {
  #include <abstractions/base>
  #include <abstractions/nameservice>

  /home/cryptonotes/cryptonotes-server/** r,
  /home/cryptonotes/cryptonotes-server/cryptonotes.db rwk,
  /home/cryptonotes/.dotnet/** rix,
  /tmp/** rw,

  deny /etc/shadow r,
  deny /etc/passwd w,
  deny /home/cryptonotes/.ssh/** rw,
}
```

```bash
sudo apparmor_parser -r /etc/apparmor.d/cryptonotes
```

---

## 11. Part 9: Building the Mobile Clients

### 11.1 Development Environment Setup

#### macOS (for both Android and iOS)

1. Install **Visual Studio for Mac** from https://visualstudio.microsoft.com/vs/mac/
2. During installation, select:
   - .NET Core
   - Xamarin (Android + iOS)
3. Install **Xcode** from the Mac App Store (required for iOS)
4. Install Android SDK via Visual Studio preferences

#### Windows (Android only)

1. Install **Visual Studio 2019+** from https://visualstudio.microsoft.com/
2. Select the "Mobile development with .NET" workload
3. Ensure Android SDK 28+ is installed

### 11.2 Building the Android App

1. Open `CryptoNotes.sln` in Visual Studio
2. Set `CryptoNotes.Android` as the startup project
3. Select `Release` configuration
4. Build > Archive for Publishing
5. Sign the APK with your keystore:

```bash
# Generate a signing key (first time only)
keytool -genkey -v -keystore cryptonotes-release.keystore \
  -alias cryptonotes -keyalg RSA -keysize 4096 \
  -validity 10000
```

6. In Visual Studio: Archive > Distribute > Ad Hoc > Select keystore > Create APK

The signed APK will be in:
```
CryptoNotes.Android/bin/Release/com.cryptonotes.app-Signed.apk
```

### 11.3 Building the iOS App

1. Open `CryptoNotes.sln` in Visual Studio for Mac
2. Set `CryptoNotes.iOS` as the startup project
3. Requires an Apple Developer account ($99/year)
4. Create provisioning profiles in the Apple Developer portal
5. Select `Release | iPhone` configuration
6. Build > Archive for Publishing
7. Distribute via TestFlight or Ad Hoc

### 11.4 Distributing the App

**For private use / small groups:**
- Android: Distribute the signed APK directly (sideload)
- iOS: Use TestFlight (up to 10,000 beta testers)

**For public distribution:**
- Android: Publish on Google Play Store
- iOS: Submit to Apple App Store (requires App Review)

**Self-hosting APK downloads:**

You can host the APK on your server for direct download. Create a simple page:

```bash
sudo mkdir -p /var/www/cryptonotes-download
# Copy the APK there
sudo cp cryptonotes-release.apk /var/www/cryptonotes-download/
```

Add an Apache virtual host or location block to serve it over HTTPS.

---

## 12. Part 10: Client Setup Guide for Users

Give this section to your end users.

### Step 1: Install the App

- **Android**: Install the APK (enable "Install from unknown sources" if sideloading)
- **iOS**: Accept the TestFlight invitation and install

### Step 2: Create Your App Password

On first launch, you will see the lock screen:

1. Choose a strong password (minimum 8 characters)
2. **Remember this password** - if you forget it, all data is permanently lost
3. This password encrypts all your local data with AES-256 encryption
4. After 5 failed unlock attempts, the app automatically wipes all data

### Step 3: Generate Your PGP Key Pair

1. Open the side menu and tap **Your Keys**
2. Tap the **+** button to create a new key
3. Enter:
   - **Name**: A label for this key (e.g., "My Main Key")
   - **Email**: Your email address
   - **Password**: A password for this PGP key (can differ from app password)
4. Tap **Generate Key**

### Step 4: Register with the Server

1. Open the side menu and tap **Account**
2. Enter:
   - **Server URL**: `https://msg.example.com` (your server's address)
   - **Username**: Choose a unique username (3-50 characters, alphanumeric)
   - **Password**: Your server account password (min 8 characters)
   - **PGP Key Pair**: Select the key you generated in Step 3
3. Tap **Register**

### Step 5: Start Messaging

1. Open the side menu and tap **Messages**
2. Tap **New** in the top bar
3. Search for another user by username
4. Tap their name to open a chat
5. Type your message and tap the send button

**How it works behind the scenes:**
- Your message is encrypted with the recipient's PGP public key on your device
- The encrypted blob is sent to the server
- The server relays it to the recipient
- The recipient's app decrypts it with their private key
- The server never sees the plaintext

### Step 6: Security Best Practices for Users

- **Lock your phone** with a strong PIN/biometric
- **Do not share your app password** with anyone
- **Do not screenshot** sensitive conversations
- **Use Self Destruct** if you believe your device is compromised
- **Remember**: If you uninstall the app, all local messages and keys are deleted
- **Back up your PGP key pair** somewhere safe if you want to recover your identity

---

## 13. Part 11: Ongoing Maintenance

### 13.1 Regular Update Schedule

```bash
# Weekly: Update system packages
sudo apt update && sudo apt upgrade -y

# Monthly: Check for .NET updates
dotnet --list-sdks

# Quarterly: Review and rotate the token signing key
# (This will invalidate all existing sessions - users must re-login)
```

### 13.2 Monitoring Commands

```bash
# Server status
sudo systemctl status cryptonotes

# Server logs (last 100 lines)
sudo journalctl -u cryptonotes -n 100

# Apache access log
sudo tail -50 /var/log/apache2/cryptonotes-access.log

# Apache error log
sudo tail -50 /var/log/apache2/cryptonotes-error.log

# Disk usage
du -sh ~/cryptonotes-server/cryptonotes.db

# Active connections
ss -tunlp | grep -E '(5000|443|80)'

# Fail2Ban status
sudo fail2ban-client status

# TLS certificate expiry
sudo certbot certificates
```

### 13.3 Backup Strategy

**What to back up:**
- `~/cryptonotes-server/appsettings.Production.json` (contains token signing key)
- `/etc/apache2/sites-available/cryptonotes.conf`
- `/etc/systemd/system/cryptonotes.service`

**What NOT to back up** (or encrypt heavily if you do):
- `~/cryptonotes-server/cryptonotes.db` (contains user data)
- Backups of this file should be encrypted with GPG:

```bash
# Encrypted backup
gpg --symmetric --cipher-algo AES256 \
  -o ~/backup-$(date +%Y%m%d).db.gpg \
  ~/cryptonotes-server/cryptonotes.db

# Restore
gpg --decrypt ~/backup-20240101.db.gpg > ~/cryptonotes-server/cryptonotes.db
```

### 13.4 Deploying Updates

```bash
# Stop the service
sudo systemctl stop cryptonotes

# Back up the database
cp ~/cryptonotes-server/cryptonotes.db ~/cryptonotes-data/backup-$(date +%s).db

# Pull new code and rebuild
cd ~/CryptoNotes
git pull origin main
cd CryptoNotes.Server
dotnet publish -c Release -o ~/cryptonotes-server

# Restore production config (publish may overwrite it)
# Make sure appsettings.Production.json still has your key

# Restart
sudo systemctl start cryptonotes
sudo systemctl status cryptonotes
```

### 13.5 Security Auditing

Run these periodically:

```bash
# Check for open ports (should only be 2222, 80, 443)
sudo nmap -sT localhost

# Check SSL/TLS configuration
# Visit: https://www.ssllabs.com/ssltest/analyze.html?d=msg.example.com

# Check security headers
# Visit: https://securityheaders.com/?q=msg.example.com

# Check for failed login attempts in your app
sudo journalctl -u cryptonotes | grep -c "Unauthorized"

# Check Fail2Ban banned IPs
sudo fail2ban-client status sshd
```

---

## 14. Part 12: Security Architecture Reference

### 14.1 Encryption Layers

CryptoNotes uses **four layers of encryption**:

```
Layer 4: HTTPS/TLS 1.2+     (in transit - Apache/Let's Encrypt)
    |
Layer 3: PGP Encryption      (end-to-end - message content)
    |
Layer 2: AES-256-CBC          (at rest - local database fields)
    |
Layer 1: App Password Lock    (device access - PBKDF2 derived key)
```

| Layer | Protects Against | Algorithm | Key Source |
|-------|-----------------|-----------|-----------|
| TLS | Network eavesdropping, MITM | TLS 1.2+ with ECDHE | Let's Encrypt certificate |
| PGP | Server compromise, relay interception | RSA + AES (via PgpCore) | Per-user PGP key pair |
| AES-256 | Device theft, physical access to SQLite | AES-256-CBC, PKCS7 | PBKDF2 from app password |
| App Lock | Unauthorized app access | PBKDF2 (100K iterations, SHA-256) | User's app password |

### 14.2 Key Management

| Key | Where Generated | Where Stored | Who Has Access |
|-----|----------------|-------------|----------------|
| PGP Private Key | On user's device | Local SQLite (AES-encrypted) | User only |
| PGP Public Key | On user's device | Server DB + local SQLite | Everyone (by design) |
| App Password Hash | On user's device | `.cryptonotes_security` file | Device only |
| AES Data Encryption Key | On user's device | `.cryptonotes_security` (encrypted with password) | Derived from app password |
| Server Token Signing Key | Server admin | `appsettings.Production.json` | Server admin only |
| Auth Tokens | Server | Local SQLite (AES-encrypted) | User's device + server |
| TLS Certificate | Let's Encrypt | `/etc/letsencrypt/` | Apache |

### 14.3 What Happens If...

| Scenario | Impact | Data Exposed |
|----------|--------|-------------|
| **Server is hacked** | Attacker gets PGP-encrypted blobs + public keys + bcrypt hashes | No plaintext messages. Attacker cannot read any messages without users' private keys |
| **Server DB is leaked** | Same as above | Encrypted message blobs are useless without private keys |
| **User's phone is stolen (locked)** | Attacker must guess app password | Nothing, if app password is strong. Auto-wipe after 5 failed attempts |
| **User's phone is stolen (unlocked app)** | Attacker can read messages on screen | Local messages visible. No access to other users' messages |
| **TLS certificate is compromised** | MITM possible | Attacker sees PGP-encrypted blobs (still can't read plaintext) |
| **Token signing key is leaked** | Attacker can forge auth tokens | Can send/receive encrypted messages as any user, but still can't read them |
| **App password is forgotten** | User locked out forever | All local data inaccessible (encrypted). Must re-register |
| **5 failed password attempts** | Auto-wipe triggered | All local data destroyed permanently |

### 14.4 Threat Model Summary

**Protected against:**
- Server operator reading messages (E2E encryption)
- Network eavesdroppers (TLS + PGP)
- Physical device theft (app lock + AES + auto-wipe)
- Brute force on server accounts (rate limiting + bcrypt)
- Brute force on app password (auto-wipe after 5 attempts)
- SQL injection (parameterized queries)
- Session hijacking (HMAC tokens with expiry)

**Not protected against (limitations):**
- Compromised device with root/jailbreak access (can dump memory)
- Keylogger on the device (captures passwords as typed)
- Screenshots/screen recording on the device
- Targeted malware on the user's device
- Quantum computing (PGP/RSA will eventually be vulnerable)
- Metadata analysis (server knows who talks to whom, and when)
- User sharing their own keys/passwords

---

## 15. Troubleshooting

### Server won't start

```bash
# Check logs
sudo journalctl -u cryptonotes -n 50 --no-pager

# Common issues:
# - Port 5000 already in use: sudo lsof -i :5000
# - Missing appsettings.json: ls -la ~/cryptonotes-server/
# - Permission denied: check file ownership with ls -la
```

### Apache shows 502 Bad Gateway

```bash
# Is Kestrel running?
sudo systemctl status cryptonotes

# Is Kestrel listening?
curl http://127.0.0.1:5000/api/auth/login

# Check Apache error log
sudo tail -20 /var/log/apache2/cryptonotes-error.log
```

### SSL certificate issues

```bash
# Check certificate status
sudo certbot certificates

# Force renewal
sudo certbot renew --force-renewal

# Check SSL config
sudo apache2ctl -t -D DUMP_MODULES | grep ssl
```

### Client can't connect to server

1. Verify the server URL uses `https://` not `http://`
2. Check if the domain resolves: `dig msg.example.com`
3. Check if port 443 is open: `curl -v https://msg.example.com/api/auth/login`
4. Check firewall: `sudo ufw status`
5. Check Apache is running: `sudo systemctl status apache2`

### "Token expired" errors

Users see this when their auth token expires (default: 24 hours). They need to:
1. Go to **Account** in the app
2. Tap **Log Out**
3. Log in again with their credentials

### Database locked errors

```bash
# Check if multiple processes are accessing the DB
sudo lsof ~/cryptonotes-server/cryptonotes.db

# If the WAL file is large, run a checkpoint
sqlite3 ~/cryptonotes-server/cryptonotes.db "PRAGMA wal_checkpoint(TRUNCATE);"
```

### Auto-wipe triggered accidentally

If a user triggers the auto-wipe (5 failed password attempts):
- All local data is permanently destroyed
- They must tap "Start Fresh" and set a new password
- They must re-generate PGP keys
- They must re-register with the server (new account or contact admin)
- Previous message history on that device is gone

There is no recovery. This is by design.

---

## 16. Automation Scripts

CryptoNotes includes automation scripts to simplify deployment. These are in the `scripts/` directory.

### 16.1 Automated Server Hardening

Automates everything in [Part 1](#3-part-1-linux-server-setup-and-hardening):

```bash
# On a fresh Ubuntu 22.04 server:
sudo bash scripts/server-setup.sh
```

**What it does:**
- Creates a dedicated service user
- Hardens SSH (key-only auth, custom port, rate limiting)
- Installs and configures Fail2Ban (24h ban after 3 failed attempts)
- Applies kernel hardening (sysctl settings)
- Configures UFW firewall (SSH, HTTP, HTTPS only)
- Installs .NET Core runtime
- Enables automatic security updates
- Creates application directories with restrictive permissions

**Interactive prompts:**
- SSH port (default: 2222)
- Service username (default: cryptonotes)
- Your SSH public key (optional)

### 16.2 Automated Build & Deploy

Automates [Part 3](#5-part-3-compile-and-deploy-the-server):

```bash
# First deployment (generates config, installs systemd service):
bash scripts/deploy.sh --first-run

# Update deployment (rebuilds and restarts):
bash scripts/deploy.sh
```

**First run:**
- Compiles the .NET server in Release mode
- Generates a cryptographically random token signing key
- Creates `appsettings.Production.json` with secure permissions (600)
- Installs and enables the systemd service

**Update run:**
- Stops the running service
- Backs up the database before updating
- Rebuilds and deploys the new version
- Restarts the service

### 16.3 Automated SSL & Apache Setup

Automates [Part 4](#6-part-4-https-with-lets-encrypt) and [Part 5](#7-part-5-apache-reverse-proxy-configuration):

```bash
sudo bash scripts/ssl-setup.sh
```

**What it does:**
- Installs Apache and Certbot
- Obtains a Let's Encrypt certificate (4096-bit RSA)
- Generates 4096-bit DH parameters
- Configures Apache as a secure reverse proxy (A+ SSL Labs rating)
- Sets all security headers (HSTS, CSP, X-Frame-Options, etc.)
- Restricts proxied routes to `/api` and `/health` only
- Sets up daily auto-renewal cron job
- Verifies HTTPS connectivity

**Interactive prompts:**
- Your domain name (e.g., msg.example.com)
- Email for Let's Encrypt notifications

### 16.4 Database Backup Script

```bash
# Interactive backup:
bash scripts/backup.sh

# Automated backup (for cron):
bash scripts/backup.sh --auto

# Restore from backup:
bash scripts/backup.sh --restore /path/to/backup.db

# Restore encrypted backup:
bash scripts/backup.sh --restore /path/to/backup.db.gpg
```

**Features:**
- Uses `sqlite3 .backup` for crash-safe backups (no corruption risk)
- Optional GPG encryption of backups
- Automatic rotation (keeps last 30 backups)
- Integrity verification after backup
- Supports Docker volume detection

**Cron example** (daily at 2 AM):
```bash
crontab -e
# Add:
0 2 * * * /home/cryptonotes/CryptoNotes/scripts/backup.sh --auto
```

### 16.5 Health Check & Monitoring Script

```bash
# Check localhost:
bash scripts/health-check.sh

# Check remote server:
bash scripts/health-check.sh --url https://msg.example.com

# Continuous monitoring:
bash scripts/health-check.sh --watch --interval 60

# JSON output (for automation):
bash scripts/health-check.sh --json
```

**What it checks:**
- `/health` endpoint response and latency
- API endpoint functionality
- systemd service status
- Docker container status
- Disk usage (warning at 75%, critical at 90%)
- Database file size
- SSL certificate expiry (warning at 30 days, critical at 14 days)

**Cron example** (every 5 minutes):
```bash
*/5 * * * * /home/cryptonotes/CryptoNotes/scripts/health-check.sh --json >> /var/log/cryptonotes-health.log
```

---

## 17. Docker Deployment

For the fastest deployment, use Docker. All security settings are pre-configured.

### 17.1 Quick Start

```bash
cd CryptoNotes

# Copy and edit environment file
cp docker/.env.example docker/.env
nano docker/.env
# Set TOKEN_SIGNING_KEY to a random key:
#   openssl rand -base64 48

# Build and start
docker-compose -f docker/docker-compose.yml up -d
```

That's it. The server is running on `127.0.0.1:5000`. Set up Apache/Nginx as a reverse proxy for HTTPS (see [Section 16.3](#163-automated-ssl--apache-setup)).

### 17.2 Docker Security Features

The Docker setup includes:

- **Non-root container**: Runs as a dedicated `cryptonotes` user
- **Read-only filesystem**: App binaries cannot be modified at runtime
- **Dropped capabilities**: All Linux capabilities removed (`cap_drop: ALL`)
- **No privilege escalation**: `no-new-privileges` security option
- **Resource limits**: 256MB RAM, 80% CPU max
- **Internal network**: Container is on an isolated bridge network
- **Health checks**: Automatic container health monitoring
- **Persistent data**: Database stored in a named Docker volume
- **Auto-backup**: Companion container backs up the database daily
- **Environment-based config**: Secrets passed via environment variables (never in images)

### 17.3 Docker Commands

```bash
# View logs
docker-compose -f docker/docker-compose.yml logs -f cryptonotes

# Restart
docker-compose -f docker/docker-compose.yml restart cryptonotes

# Stop everything
docker-compose -f docker/docker-compose.yml down

# Update (rebuild with new code)
docker-compose -f docker/docker-compose.yml build --no-cache
docker-compose -f docker/docker-compose.yml up -d

# Check health
docker inspect --format='{{.State.Health.Status}}' cryptonotes-server

# Access backup files
docker volume inspect cryptonotes-backups
```

### 17.4 Full Automated Deployment (from scratch)

For a complete fresh server setup with Docker:

```bash
# 1. Harden the server
sudo bash scripts/server-setup.sh

# 2. Install Docker
curl -fsSL https://get.docker.com | sh
sudo usermod -aG docker cryptonotes

# 3. Configure and start
cp docker/.env.example docker/.env
nano docker/.env  # Set TOKEN_SIGNING_KEY
docker-compose -f docker/docker-compose.yml up -d

# 4. Set up SSL
sudo bash scripts/ssl-setup.sh

# 5. Verify
bash scripts/health-check.sh --url https://your-domain.com
```

---

## 18. Security Improvements (v2)

The latest security pass added these protections:

### Server-Side
- **Localhost-only binding**: Kestrel only listens on `127.0.0.1:5000` (not `0.0.0.0`). All external traffic must go through Apache.
- **Password complexity enforcement**: Passwords must contain uppercase, lowercase, digits, and special characters. Common passwords are rejected.
- **Progressive login delays**: Failed login attempts trigger increasing delays (0ms, 500ms, 1s, 2s, 4s) to slow brute-force attacks.
- **Automatic message expiry**: Messages older than 30 days are automatically purged (configurable via `MessageExpiryDays`).
- **Health endpoint**: `/health` endpoint for monitoring, with database connectivity verification.
- **Startup warning**: Server warns loudly if using the default or weak token signing key.
- **Additional security headers**: Content-Security-Policy, Permissions-Policy, Server header removal.
- **IP detection improvement**: Proper X-Forwarded-For handling for reverse proxy setups, using rightmost IP to prevent spoofing.

### Client-Side
- **Timer cleanup**: Chat page polling timer stops immediately when navigating away (prevents background network activity).
- **Memory clearing**: Plaintext messages and public keys are cleared from memory when leaving the chat page.
- **Credential clearing**: Auth tokens are purged from the API service when the app locks or goes to sleep.
- **Rate limiting on registration**: Registration endpoint now also has rate limiting to prevent account enumeration.
