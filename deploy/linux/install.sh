#!/usr/bin/env bash
# Instala ou atualiza o Brymen DMM REST como serviço systemd (Linux arm64 ou x64).
#
# Uso:
#   sudo bash install.sh [--no-start] <brymen-dmm-<versão>-<rid>.tar.gz | pasta do publish>
#
# Idempotente: rodar de novo troca os binários em /opt/brymen-dmm e reinstala a regra udev e a
# unit. Os dados (/var/lib/brymen-dmm) e os ajustes locais (/etc/brymen-dmm/brymen-dmm.env)
# são preservados.
set -euo pipefail

readonly APP_NAME=brymen-dmm
readonly SERVICE_USER=brymen-dmm
readonly SERVICE_GROUP=brymen-dmm
readonly EXECUTABLE=Brymen.Dmm.Api
readonly INSTALL_DIR=/opt/brymen-dmm
readonly STATE_DIR=/var/lib/brymen-dmm
readonly CONFIG_DIR=/etc/brymen-dmm
readonly UDEV_RULE=99-brymen-bu86x.rules
readonly UDEV_RULES_DIR=/etc/udev/rules.d
readonly UNIT=brymen-dmm.service
readonly UNIT_DIR=/etc/systemd/system
readonly ENV_TEMPLATE=brymen-dmm.env
readonly MODULES_LOAD=/etc/modules-load.d/brymen-dmm.conf
# HID_ID do BC-86X no uevent do dispositivo HID: barramento USB (0003), VID 0820, PID 0001.
readonly CABLE_HID_ID=0003:00000820:00000001

SCRIPT_DIR=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)
readonly SCRIPT_DIR

log() { printf '==> %s\n' "$*"; }
warn() { printf 'aviso: %s\n' "$*" >&2; }
die() { printf 'erro: %s\n' "$*" >&2; exit 1; }

usage() {
    cat <<EOF
Uso: sudo bash $0 [--no-start] <pacote.tar.gz | pasta do publish>

Instala o Brymen DMM REST em $INSTALL_DIR, cria o usuário de sistema $SERVICE_USER,
instala a regra udev ($UDEV_RULES_DIR/$UDEV_RULE) e a unit ($UNIT_DIR/$UNIT).

  <pacote.tar.gz>     artifacts/brymen-dmm-<versão>-<rid>.tar.gz gerado por deploy/publish.sh
  <pasta do publish>  pasta com o executável $EXECUTABLE (ex.: artifacts/publish/linux-arm64)
  --no-start          instala e habilita o serviço, mas não o (re)inicia
EOF
}

work_dir=""
cleanup() {
    if [[ -n $work_dir ]]; then
        rm -rf -- "$work_dir"
    fi
}
trap cleanup EXIT

start_service=true
source_path=""
while (($# > 0)); do
    case $1 in
        --no-start) start_service=false ;;
        -h | --help) usage; exit 0 ;;
        -*) usage >&2; die "opção desconhecida: $1" ;;
        *)
            [[ -z $source_path ]] || die "informe um único pacote ou pasta"
            source_path=$1
            ;;
    esac
    shift
done

check_environment() {
    [[ -n $source_path ]] || { usage >&2; exit 2; }
    [[ $(uname -s) == Linux ]] || die "este instalador é só para Linux"
    [[ $EUID -eq 0 ]] || die "execute como root (sudo bash $0 ...)"
    [[ -d /run/systemd/system ]] || die "o systemd não está ativo nesta máquina"

    local cmd
    for cmd in systemctl udevadm install getent groupadd useradd od tar find grep; do
        command -v "$cmd" >/dev/null || die "comando necessário não encontrado: $cmd"
    done

    local file
    for file in "$UDEV_RULE" "$UNIT" "$ENV_TEMPLATE" uninstall.sh; do
        [[ -f $SCRIPT_DIR/$file ]] || die "arquivo ausente ao lado do instalador: $SCRIPT_DIR/$file"
    done
}

# Aceita o tar.gz (com ou sem diretório de topo) ou a pasta do publish.
resolve_package_root() {
    if [[ -d $source_path ]]; then
        package_root=$source_path
    elif [[ -f $source_path ]]; then
        case $source_path in
            *.tar.gz | *.tgz) ;;
            *) die "esperado um .tar.gz ou uma pasta: $source_path" ;;
        esac
        work_dir=$(mktemp -d)
        log "extraindo $source_path"
        tar -xzf "$source_path" -C "$work_dir"
        package_root=$work_dir
    else
        die "pacote ou pasta não encontrado: $source_path"
    fi

    # Caminhos relativos: o filtro de deploy/ não pode alcançar a pasta onde o pacote está
    # (ex.: ~/deploy/brymen-dmm-0.1.0-linux-arm64).
    local exe
    exe=$(cd -- "$package_root" && find . -maxdepth 3 -type f -name "$EXECUTABLE" -not -path '*/deploy/*' -print -quit)
    [[ -n $exe ]] || die "executável $EXECUTABLE não encontrado em $source_path (publique com -r linux-arm64 ou -r linux-x64)"
    app_dir=$(cd -- "$package_root" && cd -- "$(dirname -- "$exe")" && pwd -P)
}

# Arquitetura do userland, só para as mensagens: a do dpkg (amd64, arm64, armhf...) ou os bits do getconf.
userland_architecture() {
    if command -v dpkg >/dev/null; then
        dpkg --print-architecture
    else
        printf '%s bits\n' "$(getconf LONG_BIT 2>/dev/null || echo '?')"
    fi
}

# Evita instalar o pacote de x64 num Raspberry Pi (e vice-versa), que só falharia no ExecStart. Quem decide é o
# userland, e não o kernel: o Raspberry Pi OS de 32 bits (armhf) roda com kernel arm64 no Pi 4/400 e no Pi 5, e
# lá o uname -m mostra aarch64. O teste é o interpretador do ELF (PT_INTERP do executável, da glibc): sem ele, o
# ExecStart falha com status=203/EXEC "No such file or directory".
check_architecture() {
    local exe=$app_dir/$EXECUTABLE magic machine package interpreter userland kernel
    magic=$(od -An -tx1 -N4 -- "$exe" | tr -d ' \n')
    [[ $magic == 7f454c46 ]] || die "$EXECUTABLE não é um executável ELF; o pacote não é de Linux"

    # e_machine: 2 bytes little-endian no offset 18 do cabeçalho ELF.
    machine=$(od -An -tx1 -j18 -N2 -- "$exe" | tr -d ' \n')
    case $machine in
        3e00) package=linux-x64 interpreter=/lib64/ld-linux-x86-64.so.2 ;;
        b700) package=linux-arm64 interpreter=/lib/ld-linux-aarch64.so.1 ;;
        *) die "$EXECUTABLE não é de x86-64 nem de aarch64 (e_machine $machine); use o publish de linux-arm64 ou linux-x64" ;;
    esac
    [[ -e $interpreter ]] && return 0

    userland=$(userland_architecture)
    kernel=$(uname -m)
    # O getconf responde pela libc do userland, e não pelo kernel.
    if [[ $(getconf LONG_BIT 2>/dev/null || true) == 32 ]]; then
        die "o sistema é de 32 bits (userland $userland, kernel $kernel) e só há pacotes de 64 bits; no Raspberry Pi, instale o Raspberry Pi OS de 64 bits (arm64)"
    fi
    die "o pacote $package não roda neste sistema (userland $userland, kernel $kernel): falta $interpreter; use o pacote da arquitetura do sistema (linux-arm64 ou linux-x64, com glibc)"
}

nologin_shell() {
    local candidate
    for candidate in /usr/sbin/nologin /sbin/nologin /usr/bin/nologin; do
        if [[ -x $candidate ]]; then
            printf '%s\n' "$candidate"
            return 0
        fi
    done
    printf '/bin/false\n'
}

ensure_service_account() {
    if ! getent group "$SERVICE_GROUP" >/dev/null; then
        log "criando o grupo de sistema $SERVICE_GROUP"
        groupadd --system "$SERVICE_GROUP"
    fi
    if ! getent passwd "$SERVICE_USER" >/dev/null; then
        log "criando o usuário de sistema $SERVICE_USER"
        useradd --system --gid "$SERVICE_GROUP" --home-dir "$STATE_DIR" --no-create-home \
            --shell "$(nologin_shell)" --comment "Brymen DMM REST" "$SERVICE_USER"
    fi
    install -d -m 0750 -o "$SERVICE_USER" -g "$SERVICE_GROUP" "$STATE_DIR"
}

# Copia para uma pasta irmã e troca por rename: o processo em execução mantém o executável
# antigo aberto até o restart, sem "Text file busy".
install_application() {
    local staging=$INSTALL_DIR.new previous=$INSTALL_DIR.old
    log "copiando a aplicação para $INSTALL_DIR"
    rm -rf -- "$staging" "$previous"
    mkdir -p -- "$staging"
    cp -R -- "$app_dir/." "$staging/"
    rm -rf -- "$staging/deploy"
    # O desinstalador fica junto da aplicação, para não depender do pacote extraído.
    cp -- "$SCRIPT_DIR/uninstall.sh" "$staging/uninstall.sh"
    chown -R root:root "$staging"
    chmod -R u=rwX,go=rX "$staging"
    # Pacotes gerados no Windows não carregam o bit de execução.
    chmod 0755 "$staging/$EXECUTABLE" "$staging/uninstall.sh"

    if [[ -e $INSTALL_DIR ]]; then
        mv -- "$INSTALL_DIR" "$previous"
    fi
    mv -- "$staging" "$INSTALL_DIR"
    rm -rf -- "$previous"
}

# Reaplica as regras só aos hidraw do BC-86X, achados pelo HID_ID do dispositivo HID pai (barramento USB 0003, VID 0820,
# PID 0001). Um "udevadm trigger --subsystem-match=hidraw" mandaria um "change" a todos os hidraw da máquina, e as regras
# dos outros dispositivos HID rodariam de novo. O --sysname-match, e não o caminho do sysfs como argumento, funciona
# também nos udev antigos. Sem o cabo ligado, não há o que disparar: a regra vale quando ele for ligado.
trigger_cable_nodes() {
    local node uevent
    local -a matches=()
    for node in /sys/class/hidraw/hidraw*; do
        uevent=$node/device/uevent
        [[ -r $uevent ]] && grep -qix "HID_ID=$CABLE_HID_ID" "$uevent" || continue
        matches+=("--sysname-match=${node##*/}")
        log "reaplicando as regras udev a /dev/${node##*/}"
    done
    if ((${#matches[@]} == 0)); then
        log "nenhum BC-86X ligado agora; a regra vale quando ele for ligado"
        return 0
    fi
    udevadm trigger --action=change --subsystem-match=hidraw "${matches[@]}"
    udevadm settle --timeout=10 || warn "udevadm settle expirou"
}

install_udev_rule() {
    log "instalando a regra udev $UDEV_RULES_DIR/$UDEV_RULE"
    install -D -m 0644 "$SCRIPT_DIR/$UDEV_RULE" "$UDEV_RULES_DIR/$UDEV_RULE"
    if udevadm control --reload; then
        trigger_cable_nodes
    else
        warn "não foi possível recarregar o udev; a regra vale a partir do próximo boot"
    fi
}

# O DeviceAllow=char-hidraw da unit só é resolvido na partida do serviço, pela lista de /proc/devices. Onde o hidraw
# vem no módulo hid (kernels genéricos do Debian e do Ubuntu), ele só aparece lá quando o módulo carrega: sem nenhum
# dispositivo HID no boot, ou com o serviço subindo antes de o udev carregar o módulo, o hidraw ficaria negado (EPERM)
# até reiniciar o serviço. O systemd-modules-load roda antes do sysinit.target e, portanto, antes da unit. Com o HID
# embutido no kernel (Raspberry Pi OS), o modprobe não faz nada.
install_modules_load() {
    log "instalando $MODULES_LOAD (módulo hid carregado no boot)"
    install -d -m 0755 "$(dirname -- "$MODULES_LOAD")"
    printf '%s\n' "# Instalado pelo install.sh do brymen-dmm: o hidraw precisa estar em /proc/devices na partida do serviço." hid \
        >"$MODULES_LOAD"
    chmod 0644 "$MODULES_LOAD"
    if command -v modprobe >/dev/null; then
        modprobe hid || warn "não foi possível carregar o módulo hid agora; ele é carregado no próximo boot"
    fi
    grep -qw hidraw /proc/devices || warn "o hidraw não está em /proc/devices: confira o CONFIG_HIDRAW do kernel"
}

install_unit() {
    log "instalando a unit $UNIT_DIR/$UNIT"
    install -D -m 0644 "$SCRIPT_DIR/$UNIT" "$UNIT_DIR/$UNIT"
    install -d -m 0755 "$CONFIG_DIR"
    if [[ ! -e $CONFIG_DIR/$APP_NAME.env ]]; then
        install -m 0640 "$SCRIPT_DIR/$ENV_TEMPLATE" "$CONFIG_DIR/$APP_NAME.env"
    fi
    systemctl daemon-reload
    systemctl enable --quiet "$UNIT"
}

restart_service() {
    if ! $start_service; then
        log "serviço habilitado, mas não iniciado (--no-start): sudo systemctl restart $UNIT"
        return 0
    fi
    log "reiniciando $UNIT"
    # Type=notify: o restart só retorna depois do READY=1 da aplicação (ou da falha).
    if ! systemctl restart "$UNIT"; then
        journalctl -u "$UNIT" -n 40 --no-pager >&2 || true
        die "o serviço não subiu; veja: journalctl -u $UNIT"
    fi
    systemctl --no-pager --lines=0 status "$UNIT" || true
}

summary() {
    printf '\n'
    log "instalado em $INSTALL_DIR (dados em $STATE_DIR, ajustes em $CONFIG_DIR/$APP_NAME.env)"
    if [[ -e /dev/brymen-bu86x ]]; then
        log "cabo encontrado: /dev/brymen-bu86x -> $(readlink -f /dev/brymen-bu86x)"
    else
        warn "/dev/brymen-bu86x não existe: ligue o cabo BC-86X (ou use Meter__Transport=Simulated)"
    fi
    printf '    curl http://localhost:5080/health/ready\n'
    printf '    curl http://localhost:5080/api/v1/meter\n'
    printf '    journalctl -u %s -f\n' "$UNIT"
}

check_environment
resolve_package_root
check_architecture
ensure_service_account
install_application
install_udev_rule
install_modules_load
install_unit
restart_service
summary
