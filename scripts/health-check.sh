#!/usr/bin/env bash
# =============================================================================
# CryptoNotes Health Check & Monitoring Script
# Checks server health and optionally sends alerts.
#
# Usage:
#   bash scripts/health-check.sh                        # Check localhost
#   bash scripts/health-check.sh --url https://example.com  # Check remote
#   bash scripts/health-check.sh --watch                # Continuous monitoring
#   bash scripts/health-check.sh --json                 # JSON output (for automation)
#
# Cron example (every 5 minutes):
#   */5 * * * * /home/cryptonotes/CryptoNotes/scripts/health-check.sh --json >> /var/log/cryptonotes-health.log
# =============================================================================

set -euo pipefail

RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
CYAN='\033[0;36m'
NC='\033[0m'

# Defaults
BASE_URL="http://127.0.0.1:5000"
WATCH_MODE=false
JSON_OUTPUT=false
WATCH_INTERVAL=60

# Parse arguments
while [ $# -gt 0 ]; do
    case "$1" in
        --url)       BASE_URL="$2"; shift 2 ;;
        --watch)     WATCH_MODE=true; shift ;;
        --json)      JSON_OUTPUT=true; shift ;;
        --interval)  WATCH_INTERVAL="$2"; shift 2 ;;
        *)           shift ;;
    esac
done

# ---------------------------------------------------------------------------
# Check functions
# ---------------------------------------------------------------------------
check_health_endpoint() {
    local start_ms=$(date +%s%N)
    local http_code
    http_code=$(curl -s -o /dev/null -w "%{http_code}" \
        --connect-timeout 5 --max-time 10 \
        "$BASE_URL/health" 2>/dev/null || echo "000")
    local end_ms=$(date +%s%N)
    local latency_ms=$(( (end_ms - start_ms) / 1000000 ))

    echo "$http_code $latency_ms"
}

check_api_endpoint() {
    local http_code
    http_code=$(curl -s -o /dev/null -w "%{http_code}" \
        --connect-timeout 5 --max-time 10 \
        -X POST "$BASE_URL/api/auth/login" \
        -H "Content-Type: application/json" \
        -d '{"username":"healthcheck","password":"healthcheck"}' \
        2>/dev/null || echo "000")
    echo "$http_code"
}

check_systemd_service() {
    if systemctl is-active --quiet cryptonotes 2>/dev/null; then
        echo "running"
    elif systemctl is-enabled --quiet cryptonotes 2>/dev/null; then
        echo "stopped"
    else
        echo "not_installed"
    fi
}

check_docker_container() {
    if docker ps --filter name=cryptonotes-server --format "{{.Status}}" 2>/dev/null | grep -q "Up"; then
        echo "running"
    elif docker ps -a --filter name=cryptonotes-server --format "{{.Status}}" 2>/dev/null | grep -q .; then
        echo "stopped"
    else
        echo "not_found"
    fi
}

check_disk_usage() {
    local deploy_dir="$HOME/cryptonotes-server"
    local data_dir="$HOME/cryptonotes-data"

    local disk_percent=""
    if [ -d "$deploy_dir" ]; then
        disk_percent=$(df "$deploy_dir" 2>/dev/null | tail -1 | awk '{print $5}' | tr -d '%')
    elif [ -d "$data_dir" ]; then
        disk_percent=$(df "$data_dir" 2>/dev/null | tail -1 | awk '{print $5}' | tr -d '%')
    else
        disk_percent=$(df / 2>/dev/null | tail -1 | awk '{print $5}' | tr -d '%')
    fi
    echo "${disk_percent:-0}"
}

check_db_size() {
    local db_path="$HOME/cryptonotes-server/cryptonotes.db"
    if [ -f "$db_path" ]; then
        du -h "$db_path" | cut -f1
    else
        echo "N/A"
    fi
}

check_ssl_expiry() {
    local domain
    domain=$(echo "$BASE_URL" | sed 's|https://||' | sed 's|http://||' | sed 's|/.*||' | sed 's|:.*||')

    if [ "$domain" = "127.0.0.1" ] || [ "$domain" = "localhost" ]; then
        echo "N/A"
        return
    fi

    local expiry
    expiry=$(echo | openssl s_client -connect "$domain:443" -servername "$domain" 2>/dev/null \
        | openssl x509 -noout -enddate 2>/dev/null \
        | cut -d= -f2)

    if [ -n "$expiry" ]; then
        local expiry_epoch
        expiry_epoch=$(date -d "$expiry" +%s 2>/dev/null || date -j -f "%b %d %T %Y %Z" "$expiry" +%s 2>/dev/null || echo "0")
        local now_epoch
        now_epoch=$(date +%s)
        local days_left=$(( (expiry_epoch - now_epoch) / 86400 ))
        echo "$days_left"
    else
        echo "N/A"
    fi
}

# ---------------------------------------------------------------------------
# Run all checks
# ---------------------------------------------------------------------------
run_checks() {
    local timestamp
    timestamp=$(date -u +"%Y-%m-%dT%H:%M:%SZ")

    # Health endpoint
    local health_result
    health_result=$(check_health_endpoint)
    local health_code
    health_code=$(echo "$health_result" | awk '{print $1}')
    local health_latency
    health_latency=$(echo "$health_result" | awk '{print $2}')

    # API endpoint
    local api_code
    api_code=$(check_api_endpoint)

    # Service status
    local systemd_status
    systemd_status=$(check_systemd_service)
    local docker_status
    docker_status=$(check_docker_container)

    # Disk and DB
    local disk_usage
    disk_usage=$(check_disk_usage)
    local db_size
    db_size=$(check_db_size)

    # SSL
    local ssl_days
    ssl_days=$(check_ssl_expiry)

    # Determine overall status
    local overall="healthy"
    local issues=""

    if [ "$health_code" = "000" ]; then
        overall="down"
        issues="Server unreachable"
    elif [ "$health_code" != "200" ]; then
        overall="degraded"
        issues="Health endpoint returned $health_code"
    fi

    if [ "$health_latency" -gt 5000 ] 2>/dev/null; then
        overall="degraded"
        issues="${issues:+$issues; }High latency: ${health_latency}ms"
    fi

    if [ "$disk_usage" -gt 90 ] 2>/dev/null; then
        overall="warning"
        issues="${issues:+$issues; }Disk usage: ${disk_usage}%"
    fi

    if [ "$ssl_days" != "N/A" ] && [ "$ssl_days" -lt 14 ] 2>/dev/null; then
        overall="warning"
        issues="${issues:+$issues; }SSL expires in ${ssl_days} days"
    fi

    # Output
    if [ "$JSON_OUTPUT" = true ]; then
        cat << JSONEOF
{"timestamp":"$timestamp","status":"$overall","health_endpoint":{"code":$health_code,"latency_ms":$health_latency},"api_endpoint":{"code":$api_code},"service":{"systemd":"$systemd_status","docker":"$docker_status"},"disk_usage_percent":$disk_usage,"db_size":"$db_size","ssl_days_remaining":"$ssl_days"${issues:+,"issues":"$issues"}}
JSONEOF
    else
        echo ""
        echo "============================================="
        echo "  CryptoNotes Health Check"
        echo "  $timestamp"
        echo "============================================="
        echo ""

        # Overall status with color
        case "$overall" in
            healthy)  echo -e "  Status:      ${GREEN}HEALTHY${NC}" ;;
            degraded) echo -e "  Status:      ${YELLOW}DEGRADED${NC}" ;;
            warning)  echo -e "  Status:      ${YELLOW}WARNING${NC}" ;;
            down)     echo -e "  Status:      ${RED}DOWN${NC}" ;;
        esac

        echo ""
        echo "  Endpoints:"

        if [ "$health_code" = "200" ]; then
            echo -e "    /health:       ${GREEN}OK${NC} (${health_latency}ms)"
        elif [ "$health_code" = "000" ]; then
            echo -e "    /health:       ${RED}UNREACHABLE${NC}"
        else
            echo -e "    /health:       ${YELLOW}HTTP $health_code${NC} (${health_latency}ms)"
        fi

        # API returns 400/401 on a dummy login = working correctly
        if [ "$api_code" = "400" ] || [ "$api_code" = "401" ]; then
            echo -e "    /api/auth:     ${GREEN}OK${NC} (returned $api_code)"
        elif [ "$api_code" = "000" ]; then
            echo -e "    /api/auth:     ${RED}UNREACHABLE${NC}"
        else
            echo -e "    /api/auth:     ${YELLOW}HTTP $api_code${NC}"
        fi

        echo ""
        echo "  Services:"
        case "$systemd_status" in
            running)       echo -e "    systemd:       ${GREEN}Running${NC}" ;;
            stopped)       echo -e "    systemd:       ${RED}Stopped${NC}" ;;
            not_installed) echo -e "    systemd:       ${CYAN}Not installed${NC}" ;;
        esac
        case "$docker_status" in
            running)   echo -e "    Docker:        ${GREEN}Running${NC}" ;;
            stopped)   echo -e "    Docker:        ${RED}Stopped${NC}" ;;
            not_found) echo -e "    Docker:        ${CYAN}Not deployed${NC}" ;;
        esac

        echo ""
        echo "  Resources:"
        if [ "$disk_usage" -gt 90 ]; then
            echo -e "    Disk usage:    ${RED}${disk_usage}%${NC}"
        elif [ "$disk_usage" -gt 75 ]; then
            echo -e "    Disk usage:    ${YELLOW}${disk_usage}%${NC}"
        else
            echo -e "    Disk usage:    ${GREEN}${disk_usage}%${NC}"
        fi
        echo "    Database:      $db_size"

        if [ "$ssl_days" != "N/A" ]; then
            if [ "$ssl_days" -lt 14 ] 2>/dev/null; then
                echo -e "    SSL expires:   ${RED}${ssl_days} days${NC}"
            elif [ "$ssl_days" -lt 30 ] 2>/dev/null; then
                echo -e "    SSL expires:   ${YELLOW}${ssl_days} days${NC}"
            else
                echo -e "    SSL expires:   ${GREEN}${ssl_days} days${NC}"
            fi
        fi

        if [ -n "$issues" ]; then
            echo ""
            echo -e "  ${YELLOW}Issues: $issues${NC}"
        fi

        echo ""
    fi
}

# ---------------------------------------------------------------------------
# Main
# ---------------------------------------------------------------------------
if [ "$WATCH_MODE" = true ]; then
    echo -e "${CYAN}Monitoring CryptoNotes (Ctrl+C to stop)...${NC}"
    echo ""
    while true; do
        run_checks
        sleep "$WATCH_INTERVAL"
    done
else
    run_checks
fi
