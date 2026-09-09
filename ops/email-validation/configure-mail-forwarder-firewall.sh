#!/usr/bin/env bash
set -euo pipefail

mode="${1:---check}"
private_interface="ens18"
private_address="10.10.252.31"
outbound_interface="ens19"
outbound_zone="outbound-only"
smtp_rule='rule family="ipv4" destination address="10.10.252.31/32" port port="25" protocol="tcp" accept'

[[ "$EUID" -eq 0 ]] || {
    printf 'ERROR: run this script with sudo.\n' >&2
    exit 1
}

fail() {
    printf 'ERROR: %s\n' "$*" >&2
    exit 1
}

private_zone() {
    firewall-cmd --get-zone-of-interface="$private_interface" 2>/dev/null |
        sed -n '1p'
}

verify() {
    local zone
    zone="$(private_zone)"
    [[ -n "$zone" ]] || fail "$private_interface is not assigned to a firewalld zone"
    ip -4 -o addr show dev "$private_interface" scope global |
        grep -Fq "$private_address/" || fail "$private_interface does not own $private_address"
    [[ "$(firewall-cmd --get-zone-of-interface="$outbound_interface")" == "$outbound_zone" ]] ||
        fail "$outbound_interface is not assigned to $outbound_zone"
    firewall-cmd --permanent --zone="$zone" --query-rich-rule="$smtp_rule" >/dev/null ||
        fail "SMTP is not allowed specifically on $private_address in zone $zone"
    ! firewall-cmd --permanent --zone="$outbound_zone" --query-service=smtp >/dev/null ||
        fail "SMTP must not be enabled on $outbound_zone"
    ! firewall-cmd --permanent --zone="$outbound_zone" --query-port=25/tcp >/dev/null ||
        fail "TCP/25 must not be enabled on $outbound_zone"
}

apply() {
    local zone backup_dir
    zone="$(private_zone)"
    [[ -n "$zone" ]] || fail "$private_interface is not assigned to a firewalld zone"
    backup_dir="/var/backups/email-validation-mail-forwarder/$(date -u +%Y%m%dT%H%M%SZ)"
    install -d -m 0700 "$backup_dir"
    firewall-cmd --permanent --zone="$zone" --list-all >"$backup_dir/private-zone.txt"
    firewall-cmd --permanent --zone="$outbound_zone" --list-all >"$backup_dir/outbound-zone.txt"
    firewall-cmd --permanent --zone="$zone" --add-rich-rule="$smtp_rule" >/dev/null
    firewall-cmd --reload >/dev/null
    verify
    printf 'Mail-forwarder firewall rule applied; backup captured at %s\n' "$backup_dir"
}

rollback() {
    local zone
    zone="$(private_zone)"
    [[ -n "$zone" ]] || fail "$private_interface is not assigned to a firewalld zone"
    firewall-cmd --permanent --zone="$zone" --remove-rich-rule="$smtp_rule" >/dev/null || true
    firewall-cmd --reload >/dev/null
    printf 'Mail-forwarder firewall rule removed from zone %s.\n' "$zone"
}

case "$mode" in
    --check) verify ;;
    --apply) apply ;;
    --rollback) rollback ;;
    *) fail "usage: $0 --check|--apply|--rollback" ;;
esac
