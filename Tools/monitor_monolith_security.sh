#!/usr/bin/env bash
set -euo pipefail

state_dir=/var/lib/monolith-security-monitor
state_file="$state_dir/status.env"
window="2 minutes ago"

test -d "$state_dir"

ssh_failures=$(journalctl --since "$window" -u ssh -u sshd --no-pager 2>/dev/null |
    grep -Eci 'Failed password|Invalid user|authentication failure' || true)
banned=0
if command -v fail2ban-client >/dev/null 2>&1; then
    banned=$(fail2ban-client status sshd 2>/dev/null |
        sed -n 's/.*Currently banned:[[:space:]]*//p' | head -n1)
    banned=${banned:-0}
fi

syn_recv=$(ss -Hnt state syn-recv '( sport = :22 or sport = :80 or sport = :1212 or sport = :1213 )' 2>/dev/null |
    wc -l | tr -d ' ')
game_connections=$(ss -Hnt state established '( sport = :1212 )' 2>/dev/null |
    wc -l | tr -d ' ')
disk_percent=$(df -P / | awk 'NR==2 {gsub(/%/, "", $5); print $5}')
service_state=$(systemctl is-active monolith-ds.service 2>/dev/null || true)
observed_at=$(date -u +%Y-%m-%dT%H:%M:%SZ)

status=ok
warn() {
    status=warning
    logger -p authpriv.warning -t monolith-security -- "$1"
}

if (( ssh_failures >= 20 )); then
    warn "SSH authentication spike: ${ssh_failures} failures in two minutes"
fi
if (( banned >= 50 )); then
    warn "fail2ban pressure: ${banned} addresses currently banned"
fi
if (( syn_recv >= 100 )); then
    warn "connection pressure: ${syn_recv} sockets in SYN-RECV on protected public ports"
fi
if (( game_connections >= 150 )); then
    warn "game TCP connection pressure: ${game_connections} established connections"
fi
if (( disk_percent >= 90 )); then
    warn "root filesystem pressure: ${disk_percent}% used"
fi
if [[ "$service_state" != active ]]; then
    warn "monolith-ds.service is ${service_state:-unknown}"
fi

tmp=$(mktemp "$state_dir/status.XXXXXX")
trap 'rm -f -- "$tmp"' EXIT
{
    printf 'observed_at_utc=%q\n' "$observed_at"
    printf 'status=%q\n' "$status"
    printf 'ssh_failures_2m=%q\n' "$ssh_failures"
    printf 'fail2ban_currently_banned=%q\n' "$banned"
    printf 'public_syn_recv=%q\n' "$syn_recv"
    printf 'game_tcp_established=%q\n' "$game_connections"
    printf 'root_disk_percent=%q\n' "$disk_percent"
    printf 'service_state=%q\n' "$service_state"
} > "$tmp"
chmod 0640 "$tmp"
mv -f -- "$tmp" "$state_file"
trap - EXIT
