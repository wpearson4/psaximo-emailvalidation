#!/usr/bin/env bash
set -euo pipefail

mode="${1:---check}"
connection_name="Wired connection 1"
interface_name="ens19"
private_interface="ens18"
public_cidr="64.182.22.160/28"
gateway="64.182.22.161"
route_table="200"
rule_priority="100"
temporary_rule_priority="99"
primary_address="64.182.22.162/28"
target_addresses="64.182.22.162/28,64.182.22.163/32,64.182.22.164/32,64.182.22.165/32,64.182.22.166/32,64.182.22.167/32,64.182.22.168/32,64.182.22.169/32,64.182.22.170/32,64.182.22.171/32,64.182.22.172/32,64.182.22.173/32,64.182.22.174/32"

[[ "$EUID" -eq 0 ]] || {
    printf 'ERROR: run this script with sudo.\n' >&2
    exit 1
}

fail() {
    printf 'ERROR: %s\n' "$*" >&2
    exit 1
}

require_preflight() {
    [[ "$(ip -4 -o addr show dev "$private_interface" scope global | awk '{print $4}')" == "10.10.252.31/24" ]] ||
        fail "$private_interface does not own the expected management address"
    ip link show dev "$interface_name" >/dev/null || fail "$interface_name is missing"
    [[ "$(nmcli -g GENERAL.CONNECTION device show "$interface_name")" == "$connection_name" ]] ||
        fail "$interface_name is not managed by the expected NetworkManager connection"
    [[ "$(nmcli -g ipv4.never-default connection show "$connection_name")" == "yes" ]] ||
        fail "$interface_name is not marked never-default"
    ip route show table "$route_table" | grep -Fq "default via $gateway dev $interface_name" ||
        fail "routing table $route_table does not contain the expected public default route"
    ip route show default | grep -Fq "default via 10.10.252.2 dev $private_interface" ||
        fail "the private main-table default route is not intact"
    [[ "$(firewall-cmd --get-zone-of-interface="$interface_name")" == "outbound-only" ]] ||
        fail "$interface_name is not assigned to outbound-only"
    firewall-cmd --permanent --zone=outbound-only --list-all | grep -Fq 'target: DROP' ||
        fail "outbound-only does not have target DROP"
}

verify_target() {
    for octet in {162..174}; do
        ip -4 addr show dev "$interface_name" | grep -Fq "64.182.22.$octet/" ||
            fail "64.182.22.$octet is not bound to $interface_name"
        ip route get 1.1.1.1 from "64.182.22.$octet" |
            grep -Eq "from 64\.182\.22\.$octet.*via $gateway.*dev $interface_name.*table $route_table" ||
            fail "source routing failed for 64.182.22.$octet"
    done
    ! ip -4 addr show dev "$interface_name" | grep -Eq '64\.182\.22\.(160|161|175)/' ||
        fail "a reserved address is assigned to $interface_name"
    ip rule show | grep -Fq "from $public_cidr lookup $route_table" ||
        fail "the CIDR source-routing rule is missing"
    require_preflight
}

backup_state() {
    local backup_root="/var/backups/email-validation-outbound"
    local backup_dir="$backup_root/$(date -u +%Y%m%dT%H%M%SZ)"
    install -d -m 0700 "$backup_dir"
    nmcli connection show "$connection_name" > "$backup_dir/nmcli-connection.txt"
    ip -4 addr show dev "$interface_name" > "$backup_dir/ip-addresses.txt"
    ip rule show > "$backup_dir/ip-rules.txt"
    ip route show table "$route_table" > "$backup_dir/route-table-200.txt"
    firewall-cmd --permanent --zone=outbound-only --list-all > "$backup_dir/firewalld-outbound-only.txt"
    local keyfile connection_uuid
    connection_uuid="$(nmcli -g connection.uuid connection show "$connection_name")"
    keyfile="$(find /etc/NetworkManager/system-connections -maxdepth 1 -type f \
        -name "$connection_uuid*" -print -quit)"
    if [[ -z "$keyfile" ]]; then
        keyfile="$(grep -R -l -F "id=$connection_name" \
            /etc/NetworkManager/system-connections 2>/dev/null | head -n 1 || true)"
    fi
    if [[ -z "$keyfile" ]]; then
        local legacy_name
        legacy_name="${connection_name// /_}"
        [[ -f "/etc/sysconfig/network-scripts/ifcfg-$legacy_name" ]] &&
            keyfile="/etc/sysconfig/network-scripts/ifcfg-$legacy_name"
    fi
    [[ -n "$keyfile" ]] || fail "the NetworkManager keyfile could not be located"
    install -m 0600 "$keyfile" "$backup_dir/$(basename "$keyfile")"
    for companion in \
        "/etc/sysconfig/network-scripts/route-${connection_name// /_}" \
        "/etc/sysconfig/network-scripts/rule-${connection_name// /_}"; do
        [[ -f "$companion" ]] && install -m 0600 "$companion" "$backup_dir/$(basename "$companion")"
    done
    printf 'Backup captured at %s\n' "$backup_dir"
}

apply_target() {
    require_preflight
    backup_state
    ip rule show | grep -Fq "from $public_cidr lookup $route_table" ||
        ip rule add priority "$temporary_rule_priority" from "$public_cidr" table "$route_table"
    nmcli connection modify "$connection_name" \
        ipv4.addresses "$target_addresses" \
        ipv4.never-default yes \
        ipv4.routes "0.0.0.0/0 $gateway table=$route_table" \
        ipv4.routing-rules "priority $rule_priority from $public_cidr table $route_table" \
        connection.zone outbound-only
    nmcli device reapply "$interface_name"
    verify_target
    while ip rule show | grep -Eq "^$temporary_rule_priority:.*from $public_cidr lookup $route_table"; do
        ip rule del priority "$temporary_rule_priority" from "$public_cidr" table "$route_table"
    done
    verify_target
}

rollback_target() {
    require_preflight
    ip rule show | grep -Fq "from 64.182.22.162 lookup $route_table" ||
        ip rule add priority "$temporary_rule_priority" from 64.182.22.162 table "$route_table"
    nmcli connection modify "$connection_name" \
        ipv4.addresses "$primary_address" \
        ipv4.never-default yes \
        ipv4.routes "0.0.0.0/0 $gateway table=$route_table" \
        ipv4.routing-rules "priority $rule_priority from 64.182.22.162 table $route_table" \
        connection.zone outbound-only
    nmcli device reapply "$interface_name"
    while ip rule show | grep -Eq "^$temporary_rule_priority:.*from 64.182.22.162 lookup $route_table"; do
        ip rule del priority "$temporary_rule_priority" from 64.182.22.162 table "$route_table"
    done
    [[ "$(ip -4 -o addr show dev "$interface_name" scope global | awk '{print $4}')" == "$primary_address" ]] ||
        fail "rollback did not restore the original address set"
    ip rule show | grep -Fq "from 64.182.22.162 lookup $route_table" ||
        fail "rollback did not restore the original source rule"
    require_preflight
}

case "$mode" in
    --check)
        require_preflight
        printf 'Preflight passed. Current addresses and source rules:\n'
        ip -4 -br addr show dev "$interface_name"
        ip rule show
        ;;
    --apply)
        apply_target
        printf 'Outbound identity network configuration applied and verified.\n'
        ;;
    --rollback)
        rollback_target
        printf 'Original single-address network configuration restored and verified.\n'
        ;;
    *)
        fail "usage: $0 --check|--apply|--rollback"
        ;;
esac
