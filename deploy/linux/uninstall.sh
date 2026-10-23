#!/usr/bin/env bash
# Remove o Brymen DMM REST instalado pelo install.sh.
#
# Uso:
#   sudo bash uninstall.sh [--purge]
#
# Sem --purge, remove o serviço, a regra udev, o /etc/modules-load.d/brymen-dmm.conf e /opt/brymen-dmm, e mantém os dados
# (/var/lib/brymen-dmm), os ajustes (/etc/brymen-dmm) e o usuário brymen-dmm.
# Com --purge, apaga também esses três. Idempotente: pode ser executado de novo sem erro.
set -euo pipefail

readonly SERVICE_USER=brymen-dmm
readonly SERVICE_GROUP=brymen-dmm
readonly INSTALL_DIR=/opt/brymen-dmm
readonly STATE_DIR=/var/lib/brymen-dmm
readonly CONFIG_DIR=/etc/brymen-dmm
readonly UDEV_RULE_PATH=/etc/udev/rules.d/99-brymen-bu86x.rules
readonly MODULES_LOAD=/etc/modules-load.d/brymen-dmm.conf
# HID_ID do BC-86X no uevent do dispositivo HID: barramento USB (0003), VID 0820, PID 0001.
readonly CABLE_HID_ID=0003:00000820:00000001
readonly UNIT=brymen-dmm.service
readonly UNIT_PATH=/etc/systemd/system/$UNIT

log() { printf '==> %s\n' "$*"; }
warn() { printf 'aviso: %s\n' "$*" >&2; }
die() { printf 'erro: %s\n' "$*" >&2; exit 1; }

usage() {
    cat <<EOF
Uso: sudo bash $0 [--purge]

  --purge   apaga também $STATE_DIR (dados e backups), $CONFIG_DIR e o usuário/grupo $SERVICE_USER
EOF
}

purge=false
while (($# > 0)); do
    case $1 in
        --purge) purge=true ;;
        -h | --help) usage; exit 0 ;;
        *) usage >&2; die "argumento desconhecido: $1" ;;
    esac
    shift
done

[[ $(uname -s) == Linux ]] || die "este script é só para Linux"
[[ $EUID -eq 0 ]] || die "execute como root (sudo bash $0)"

have_systemd() {
    command -v systemctl >/dev/null && [[ -d /run/systemd/system ]]
}

remove_unit() {
    if have_systemd && { [[ -e $UNIT_PATH ]] || systemctl is-active --quiet "$UNIT"; }; then
        log "parando e desabilitando $UNIT"
        systemctl disable --now --quiet "$UNIT" || warn "não foi possível parar $UNIT"
    fi
    if [[ -e $UNIT_PATH ]]; then
        log "removendo $UNIT_PATH"
        rm -f -- "$UNIT_PATH"
    fi
    if [[ -d $UNIT_PATH.d ]]; then
        log "removendo os drop-ins $UNIT_PATH.d"
        rm -rf -- "$UNIT_PATH.d"
    fi
    if have_systemd; then
        systemctl daemon-reload
        systemctl reset-failed "$UNIT" 2>/dev/null || true
    fi
}

# Remove a ACL de acesso (a do uaccess) sem exigir o pacote acl, que nem toda imagem traz (o Orange Pi OS não traz).
remove_acl() {
    if command -v setfacl >/dev/null; then
        setfacl -b -- "$1" || warn "não foi possível remover a ACL de $1"
    elif command -v python3 >/dev/null; then
        python3 -c 'import errno, os, sys
try:
    os.removexattr(sys.argv[1], "system.posix_acl_access")
except OSError as e:
    if e.errno != errno.ENODATA:
        raise' "$1" || warn "não foi possível remover a ACL de $1"
    else
        warn "sem setfacl nem python3: a ACL de $1 só some ao reconectar o cabo"
    fi
}

# Como no install.sh: o "change" vai só aos hidraw do BC-86X (pelo HID_ID do dispositivo HID pai) e tira o
# /dev/brymen-bu86x. Ele não devolve o dono, o modo nem a ACL que a regra aplicou, porque nenhuma regra restante os
# redefine (visto no Orange Pi 5: o nó ficou com o gid órfão, 0660 e a ACL). Por isso o nó do cabo volta aqui ao padrão
# do kernel, root:root 0600 sem ACL, como depois de reconectar o cabo. Os outros hidraw da máquina não são tocados.
reset_cable_nodes() {
    local node uevent dev
    local -a names=() matches=()
    for node in /sys/class/hidraw/hidraw*; do
        uevent=$node/device/uevent
        [[ -r $uevent ]] && grep -qix "HID_ID=$CABLE_HID_ID" "$uevent" || continue
        names+=("${node##*/}")
        matches+=("--sysname-match=${node##*/}")
    done
    ((${#names[@]} > 0)) || return 0
    udevadm trigger --action=change --subsystem-match=hidraw "${matches[@]}"
    udevadm settle --timeout=10 || warn "udevadm settle expirou"
    for dev in "${names[@]/#//dev/}"; do
        [[ -c $dev ]] || continue
        log "restaurando $dev para root:root 0600"
        { chown root:root -- "$dev" && chmod 0600 -- "$dev"; } || warn "não foi possível restaurar o dono e o modo de $dev"
        remove_acl "$dev"
    done
}

remove_udev_rule() {
    [[ -e $UDEV_RULE_PATH ]] || return 0
    log "removendo $UDEV_RULE_PATH"
    rm -f -- "$UDEV_RULE_PATH"
    if command -v udevadm >/dev/null && udevadm control --reload; then
        reset_cable_nodes
    fi
}

# O módulo hid continua carregado (outros dispositivos HID dependem dele); só o carregamento no boot sai.
remove_modules_load() {
    [[ -e $MODULES_LOAD ]] || return 0
    log "removendo $MODULES_LOAD"
    rm -f -- "$MODULES_LOAD"
}

remove_application() {
    local dir
    for dir in "$INSTALL_DIR" "$INSTALL_DIR.new" "$INSTALL_DIR.old"; do
        if [[ -e $dir ]]; then
            log "removendo $dir"
            rm -rf -- "$dir"
        fi
    done
}

purge_data() {
    local dir
    for dir in "$STATE_DIR" "$CONFIG_DIR"; do
        if [[ -e $dir ]]; then
            log "apagando $dir"
            rm -rf -- "$dir"
        fi
    done
    if getent passwd "$SERVICE_USER" >/dev/null; then
        log "removendo o usuário $SERVICE_USER"
        userdel "$SERVICE_USER"
    fi
    # userdel pode já ter removido o grupo primário (USERGROUPS_ENAB).
    if getent group "$SERVICE_GROUP" >/dev/null; then
        log "removendo o grupo $SERVICE_GROUP"
        groupdel "$SERVICE_GROUP"
    fi
}

remove_unit
remove_udev_rule
remove_modules_load
remove_application

if $purge; then
    purge_data
else
    log "mantidos: $STATE_DIR, $CONFIG_DIR e o usuário $SERVICE_USER (use --purge para apagar)"
fi
log "desinstalação concluída"
