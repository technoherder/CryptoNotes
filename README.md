# CryptoNotes

End-to-end encrypted messaging platform built with Xamarin.Forms and ASP.NET Core. Messages are encrypted on-device with PGP before leaving your phone — the server only ever sees ciphertext.

```
+------------------+         HTTPS (TLS 1.2+)        +-------------------+
|  Mobile Client   | <-----------------------------> |   Relay Server    |
|  (Android/iOS)   |    PGP-encrypted blobs only      |   (ASP.NET Core)  |
|                  |                                   |                   |
|  - PGP keygen    |                                   |  Reverse Proxy    |
|  - AES-256 local |                                   |    -> Kestrel     |
|  - App password  |                                   |    -> SQLite      |
|  - Auto-wipe     |                                   |  (encrypted msgs) |
+------------------+                                   +-------------------+
```

## Features

### Encrypted Messaging
- **End-to-end PGP encryption** — messages are encrypted with the recipient's public key on your device
- **Zero-knowledge relay server** — the server stores and forwards ciphertext; it never has access to plaintext
- **Real-time message polling** with automatic delivery tracking

### Key Management
- Generate PGP key pairs on-device
- Import and manage a trusted public keychain
- Share public keys via the system share sheet
- Manual encrypt/decrypt tools for standalone PGP operations

### Device Security
- **AES-256-CBC local encryption** — all data at rest is encrypted with a key derived from your app password
- **App lock** with PBKDF2-derived passphrase (100K iterations, SHA-256)
- **Auto-wipe after 5 failed unlock attempts** — all keys, messages, and credentials are permanently destroyed
- **In-memory cleanup** — plaintext is cleared from memory when navigating away from conversations

### Server Security
- BCrypt password hashing (cost factor 12)
- HMAC-SHA256 authentication tokens with configurable expiry
- Rate limiting with progressive delays on failed login attempts
- Automatic message expiry (default 30 days)
- Comprehensive security headers (CSP, HSTS, X-Frame-Options, etc.)
- **Admin dashboard** at `/admin` with IP allowlist (localhost + configurable CIDR ranges)

## Project Structure

```
CryptoNotes/
├── CryptoNotes/              # Shared Xamarin.Forms project (UI + logic)
│   ├── Views/                # XAML pages and code-behind
│   ├── Models/               # Data models
│   ├── ViewModels/           # MVVM view models
│   └── Services/             # Encryption, messaging, security services
├── CryptoNotes.Android/      # Android platform project
├── CryptoNotes.iOS/          # iOS platform project
├── CryptoNotes.Server/       # ASP.NET Core relay server
│   ├── Controllers/          # Auth, Messages, Users, Admin endpoints
│   ├── Models/               # Server data models
│   └── Data/                 # EF Core database context
├── CryptoNotes.UITests/      # UI test project
├── docker/                   # Dockerfile and docker-compose
│   ├── Dockerfile
│   └── docker-compose.yml
├── scripts/                  # Deployment automation
│   ├── server-setup.sh       # Server hardening
│   ├── deploy.sh             # Build and deploy
│   ├── ssl-setup.sh          # Let's Encrypt + Apache
│   ├── backup.sh             # Database backup/restore
│   └── health-check.sh       # Monitoring
└── WIKI.md                   # Full deployment guide
```

## Quick Start

### Server

**Option A: Bare metal**

```bash
cd CryptoNotes/CryptoNotes.Server
dotnet restore
dotnet run
# Server starts on http://127.0.0.1:5000
```

**Option B: Docker**

```bash
cp docker/.env.example docker/.env
# Edit docker/.env — set TOKEN_SIGNING_KEY (run: openssl rand -base64 48)
docker-compose -f docker/docker-compose.yml up -d
```

### Mobile Client

1. Open `CryptoNotes.sln` in Visual Studio (with Xamarin workload installed)
2. Set `CryptoNotes.Android` or `CryptoNotes.iOS` as the startup project
3. Build and deploy to a device or emulator

### First Run

1. Set an app password on the lock screen
2. Generate a PGP key pair under **Your Keys**
3. Register with your server under **Account**
4. Search for other users and start a conversation

## Server Configuration

Configuration lives in `appsettings.json` (override with `appsettings.Production.json` in production):

| Setting | Default | Description |
|---------|---------|-------------|
| `Security:TokenSigningKey` | *(placeholder)* | HMAC key for auth tokens — **must change in production** |
| `Security:TokenExpiryHours` | `24` | Auth token lifetime |
| `Security:MaxLoginAttemptsPerMinute` | `5` | Rate limit per IP |
| `Security:MaxMessageSizeBytes` | `65536` | Max encrypted message size (64 KB) |
| `Security:MinPasswordLength` | `8` | Minimum account password length |
| `Security:MessageExpiryDays` | `30` | Auto-delete messages older than this |
| `Admin:AllowedIPs` | `[]` | IPs permitted to access `/admin` (CIDR supported) |

### Admin Dashboard

The server includes a built-in admin panel at `/admin` showing server stats, message activity, and recent registrations. Access is restricted by IP:

- **Localhost** (`127.0.0.1` / `::1`) is always allowed
- Add external IPs to `Admin:AllowedIPs` in `appsettings.json`:

```json
"Admin": {
  "AllowedIPs": [
    "203.0.113.45",
    "10.0.0.0/8"
  ]
}
```

Requests from unlisted IPs receive a `403 Forbidden` response that includes the caller's IP for easy configuration.

## Encryption Architecture

CryptoNotes uses four layers of encryption:

| Layer | Purpose | Algorithm | Protects Against |
|-------|---------|-----------|-----------------|
| **TLS 1.2+** | In transit | ECDHE + AES-GCM | Network eavesdropping, MITM |
| **PGP** | End-to-end | RSA + AES (PgpCore) | Server compromise, relay interception |
| **AES-256-CBC** | At rest (local DB) | AES-256 with PKCS7 padding | Device theft, physical access |
| **App Lock** | Device access | PBKDF2 (100K iterations, SHA-256) | Unauthorized app access |

The server **never has access to**: private PGP keys, plaintext messages, app lock passwords, or AES data encryption keys.

## Production Deployment

For a complete production deployment guide covering server hardening, TLS setup, Apache reverse proxy, systemd services, firewall configuration, and mobile client builds, see **[WIKI.md](WIKI.md)**.

### Automated Deployment

```bash
# 1. Harden the server
sudo bash scripts/server-setup.sh

# 2. Build and deploy
bash scripts/deploy.sh --first-run

# 3. Set up HTTPS + Apache
sudo bash scripts/ssl-setup.sh

# 4. Verify
bash scripts/health-check.sh
```

## API Endpoints

| Method | Endpoint | Auth | Description |
|--------|----------|------|-------------|
| `POST` | `/api/auth/register` | No | Register with username, password, and PGP public key |
| `POST` | `/api/auth/login` | No | Authenticate and receive a bearer token |
| `GET` | `/api/users/search?query=` | Yes | Search users by username prefix |
| `GET` | `/api/users/{username}/publickey` | Yes | Get a user's PGP public key |
| `POST` | `/api/messages/send` | Yes | Send a PGP-encrypted message |
| `GET` | `/api/messages/receive` | Yes | Fetch undelivered messages |
| `GET` | `/api/messages/conversations` | Yes | List conversations with unread counts |
| `GET` | `/api/messages/conversation/{user}` | Yes | Paginated message history |
| `GET` | `/health` | No | Server health check |
| `GET` | `/admin` | IP | Admin dashboard (allowlisted IPs only) |

## Tech Stack

- **Client**: Xamarin.Forms, PgpCore, SQLite-net
- **Server**: ASP.NET Core 3.1, Entity Framework Core, SQLite, BCrypt.Net
- **Encryption**: PGP (RSA + AES via PgpCore), AES-256-CBC, PBKDF2, BCrypt

## Requirements

- **Server**: .NET Core 3.1+, Linux/macOS/Windows
- **Android**: SDK 28+ (Android 9.0+)
- **iOS**: iOS 13+
- **Development**: Visual Studio 2019+ with Xamarin workload (macOS required for iOS builds)

## License

See repository license file for details.
