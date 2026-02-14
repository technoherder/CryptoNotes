#!/usr/bin/env bash
# =============================================================================
# CryptoNotes Backup Script
# Creates encrypted backups of the CryptoNotes database.
#
# Usage:
#   bash scripts/backup.sh                    # Interactive backup
#   bash scripts/backup.sh --auto             # Automated (cron) mode
#   bash scripts/backup.sh --restore <file>   # Restore from backup
#
# Cron example (daily at 2 AM):
#   0 2 * * * /home/cryptonotes/CryptoNotes/scripts/backup.sh --auto
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

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
PROJECT_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"

# Defaults
DEPLOY_DIR="$HOME/cryptonotes-server"
BACKUP_DIR="$HOME/cryptonotes-backups"
MAX_BACKUPS=30
AUTO_MODE=false
RESTORE_FILE=""

# Parse arguments
for arg in "$@"; do
    case "$arg" in
        --auto)       AUTO_MODE=true ;;
        --restore)    shift; RESTORE_FILE="$1" ;;
    esac
done

# Handle --restore <file> pattern
if [ "$1" = "--restore" ] 2>/dev/null && [ -n "${2:-}" ]; then
    RESTORE_FILE="$2"
fi

mkdir -p "$BACKUP_DIR"
chmod 700 "$BACKUP_DIR"

# ---------------------------------------------------------------------------
# Restore mode
# ---------------------------------------------------------------------------
if [ -n "$RESTORE_FILE" ]; then
    if [ ! -f "$RESTORE_FILE" ]; then
        err "Backup file not found: $RESTORE_FILE"
    fi

    log "Restoring from: $RESTORE_FILE"
    warn "This will REPLACE the current database."
    echo ""

    if [ "$AUTO_MODE" != true ]; then
        echo -e "${CYAN}[?]${NC} Continue? (y/N)"
        read -r confirm
        if [ "$confirm" != "y" ] && [ "$confirm" != "Y" ]; then
            echo "Aborted."
            exit 0
        fi
    fi

    # Stop the service
    if systemctl is-active --quiet cryptonotes 2>/dev/null; then
        log "Stopping CryptoNotes service..."
        sudo systemctl stop cryptonotes
    fi

    # Backup current DB before restoring
    DB_PATH="$DEPLOY_DIR/cryptonotes.db"
    if [ -f "$DB_PATH" ]; then
        TIMESTAMP=$(date +%Y%m%d-%H%M%S)
        cp "$DB_PATH" "$BACKUP_DIR/pre-restore-$TIMESTAMP.db"
        log "Current database backed up to pre-restore-$TIMESTAMP.db"
    fi

    # Restore (handle encrypted backups)
    if [[ "$RESTORE_FILE" == *.gpg ]]; then
        log "Decrypting backup..."
        gpg --decrypt "$RESTORE_FILE" > "$DB_PATH"
    else
        cp "$RESTORE_FILE" "$DB_PATH"
    fi

    chmod 600 "$DB_PATH"
    log "Database restored."

    # Restart
    if systemctl is-enabled --quiet cryptonotes 2>/dev/null; then
        sudo systemctl start cryptonotes
        log "CryptoNotes service restarted."
    fi

    log "Restore complete!"
    exit 0
fi

# ---------------------------------------------------------------------------
# Backup mode
# ---------------------------------------------------------------------------
DB_PATH="$DEPLOY_DIR/cryptonotes.db"

if [ ! -f "$DB_PATH" ]; then
    # Try Docker volume
    DOCKER_DB=$(docker volume inspect cryptonotes-data --format '{{.Mountpoint}}' 2>/dev/null || true)
    if [ -n "$DOCKER_DB" ] && [ -f "$DOCKER_DB/cryptonotes.db" ]; then
        DB_PATH="$DOCKER_DB/cryptonotes.db"
        log "Found database in Docker volume."
    else
        err "Database not found at $DB_PATH or in Docker volumes."
    fi
fi

TIMESTAMP=$(date +%Y%m%d-%H%M%S)
BACKUP_FILE="$BACKUP_DIR/cryptonotes-$TIMESTAMP.db"

# Use SQLite .backup for a safe, consistent backup (no corruption risk)
if command -v sqlite3 &>/dev/null; then
    log "Creating safe SQLite backup..."
    sqlite3 "$DB_PATH" ".backup '$BACKUP_FILE'"
else
    log "sqlite3 not found, using file copy..."
    cp "$DB_PATH" "$BACKUP_FILE"
fi

chmod 600 "$BACKUP_FILE"

# Get backup size
BACKUP_SIZE=$(du -h "$BACKUP_FILE" | cut -f1)
log "Backup created: $BACKUP_FILE ($BACKUP_SIZE)"

# Encrypt backup if GPG is available
if command -v gpg &>/dev/null; then
    GPG_RECIPIENT=""
    if [ "$AUTO_MODE" != true ]; then
        echo -e "${CYAN}[?]${NC} Encrypt backup with GPG? Enter recipient email (or leave blank to skip):"
        read -r GPG_RECIPIENT
    fi

    if [ -n "$GPG_RECIPIENT" ]; then
        gpg --encrypt --recipient "$GPG_RECIPIENT" "$BACKUP_FILE"
        # Securely delete unencrypted backup
        shred -u "$BACKUP_FILE" 2>/dev/null || rm -f "$BACKUP_FILE"
        BACKUP_FILE="${BACKUP_FILE}.gpg"
        log "Backup encrypted with GPG."
    fi
fi

# Rotate old backups
BACKUP_COUNT=$(ls -1 "$BACKUP_DIR"/cryptonotes-*.db* 2>/dev/null | wc -l)
if [ "$BACKUP_COUNT" -gt "$MAX_BACKUPS" ]; then
    EXCESS=$((BACKUP_COUNT - MAX_BACKUPS))
    log "Rotating $EXCESS old backup(s)..."
    ls -1t "$BACKUP_DIR"/cryptonotes-*.db* | tail -n "$EXCESS" | while read -r old; do
        shred -u "$old" 2>/dev/null || rm -f "$old"
    done
fi

# Verify backup integrity
if command -v sqlite3 &>/dev/null && [[ "$BACKUP_FILE" != *.gpg ]]; then
    INTEGRITY=$(sqlite3 "$BACKUP_FILE" "PRAGMA integrity_check;" 2>/dev/null || echo "failed")
    if [ "$INTEGRITY" = "ok" ]; then
        log "Backup integrity verified."
    else
        warn "Backup integrity check returned: $INTEGRITY"
    fi
fi

echo ""
log "Backup complete!"
log "  File: $BACKUP_FILE"
log "  Size: $BACKUP_SIZE"
log "  Backups retained: $(ls -1 "$BACKUP_DIR"/cryptonotes-*.db* 2>/dev/null | wc -l)/$MAX_BACKUPS"
echo ""
