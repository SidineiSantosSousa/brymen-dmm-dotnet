#!/usr/bin/env bash
# Publica o Brymen.Dmm.Api para Linux e empacota cada RID em tar.gz.
#
# Uso:
#   deploy/publish.sh [-r|--rid linux-arm64|linux-x64] [--framework-dependent]
#                     [-c|--configuration Release] [--version X.Y.Z]
#
# Saída (sem opções: linux-arm64 e linux-x64, self-contained, single-file e trimmed):
#   artifacts/publish/<rid>/                    publish + deploy/linux + README.md
#   artifacts/brymen-dmm-<versão>-<rid>.tar.gz  a pasta acima, com o topo brymen-dmm-<versão>-<rid>/
# Com --framework-dependent (exige o runtime do ASP.NET Core 10 no alvo, sem trimming), os nomes
# ganham o sufixo "-fd".
set -euo pipefail

SCRIPT_DIR=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)
REPO_ROOT=$(cd -- "$SCRIPT_DIR/.." && pwd -P)
readonly SCRIPT_DIR REPO_ROOT
readonly PROJECT=$REPO_ROOT/src/Brymen.Dmm.Api/Brymen.Dmm.Api.csproj
readonly ARTIFACTS=$REPO_ROOT/artifacts
readonly EXECUTABLE=Brymen.Dmm.Api
SUPPORTED_RIDS=(linux-arm64 linux-x64)
DEPLOY_FILES=(99-brymen-bu86x.rules brymen-dmm.service brymen-dmm.env install.sh uninstall.sh)

log() { printf '==> %s\n' "$*"; }
die() { printf 'erro: %s\n' "$*" >&2; exit 1; }

usage() {
    cat <<EOF
Uso: $0 [-r|--rid <rid>] [--framework-dependent] [-c|--configuration <cfg>] [--version <x.y.z>]

  -r, --rid <rid>          publica só este RID (${SUPPORTED_RIDS[*]}); padrão: todos
  --framework-dependent    sem runtime embutido e sem trimming (sufixo -fd nos nomes)
  -c, --configuration      configuração do build (padrão: Release)
  --version <x.y.z>        versão do pacote (padrão: <Version> do Directory.Build.props)
EOF
}

rids=()
framework_dependent=false
configuration=Release
version=""
while (($# > 0)); do
    case $1 in
        -r | --rid)
            (($# >= 2)) || die "$1 exige um valor"
            rids+=("$2")
            shift
            ;;
        --framework-dependent) framework_dependent=true ;;
        -c | --configuration)
            (($# >= 2)) || die "$1 exige um valor"
            configuration=$2
            shift
            ;;
        --version)
            (($# >= 2)) || die "$1 exige um valor"
            version=$2
            shift
            ;;
        -h | --help) usage; exit 0 ;;
        *) usage >&2; die "argumento desconhecido: $1" ;;
    esac
    shift
done

((${#rids[@]} > 0)) || rids=("${SUPPORTED_RIDS[@]}")
for rid in "${rids[@]}"; do
    [[ " ${SUPPORTED_RIDS[*]} " == *" $rid "* ]] || die "RID não suportado: $rid (use ${SUPPORTED_RIDS[*]})"
done

command -v dotnet >/dev/null || die "dotnet não encontrado no PATH (instale o SDK do global.json)"
command -v tar >/dev/null || die "tar não encontrado no PATH"
[[ -f $PROJECT ]] || die "projeto não encontrado: $PROJECT"

version_args=()
if [[ -n $version ]]; then
    version_args=("-p:Version=$version")
else
    version=$(sed -n '/<Version>/{s:.*<Version>\(.*\)</Version>.*:\1:p;q;}' "$REPO_ROOT/Directory.Build.props")
    [[ -n $version ]] || die "não foi possível ler <Version> de Directory.Build.props; use --version"
fi

tar_version=$(tar --version 2>/dev/null || true)

# Copia os arquivos de instalação com fim de linha LF: um checkout no Windows pode trazê-los com
# CRLF, e o bash e o udev não aceitam.
copy_deploy_files() {
    local out=$1 file
    mkdir -p -- "$out/deploy/linux"
    for file in "${DEPLOY_FILES[@]}"; do
        tr -d '\r' <"$SCRIPT_DIR/linux/$file" >"$out/deploy/linux/$file"
    done
    chmod 0755 "$out/deploy/linux/install.sh" "$out/deploy/linux/uninstall.sh"
    if [[ -f $REPO_ROOT/README.md ]]; then
        cp -- "$REPO_ROOT/README.md" "$out/README.md"
    fi
}

create_archive() {
    local folder=$1 name=$2 archive=$ARTIFACTS/$2.tar.gz
    rm -f -- "$archive"
    if [[ $tar_version == *"GNU tar"* ]]; then
        tar -czf "$archive" -C "$ARTIFACTS/publish" --owner=0 --group=0 --numeric-owner \
            --transform "s|^$folder|$name|" "$folder"
    else
        # bsdtar (macOS, Windows) não tem --transform, e o do Windows nem -s: empacota uma cópia com o
        # nome final.
        local staging=$ARTIFACTS/.package
        rm -rf -- "$staging"
        mkdir -p -- "$staging"
        cp -R -- "$ARTIFACTS/publish/$folder" "$staging/$name"
        tar -czf "$archive" -C "$staging" "$name"
        rm -rf -- "$staging"
    fi
    log "pacote: $archive"
}

publish_rid() {
    local rid=$1 suffix="" mode_args
    if $framework_dependent; then
        suffix=-fd
        mode_args=(--self-contained false)
    else
        mode_args=(--self-contained true -p:PublishTrimmed=true)
    fi
    local folder=$rid$suffix
    local out=$ARTIFACTS/publish/$folder
    local name=brymen-dmm-$version-$rid$suffix

    log "publicando $rid ($configuration, $([[ -n $suffix ]] && echo framework-dependent || echo self-contained)) em $out"
    rm -rf -- "$out"
    dotnet publish "$PROJECT" -c "$configuration" -r "$rid" -o "$out" "${mode_args[@]}" \
        -p:PublishSingleFile=true -p:DebugType=none \
        -p:PublishDocumentationFiles=false -p:IsTransformWebConfigDisabled=true \
        ${version_args[@]+"${version_args[@]}"}

    [[ -f $out/$EXECUTABLE ]] || die "o publish não gerou $out/$EXECUTABLE"
    chmod 0755 "$out/$EXECUTABLE"
    copy_deploy_files "$out"
    create_archive "$folder" "$name"
}

mkdir -p -- "$ARTIFACTS/publish"
for rid in "${rids[@]}"; do
    publish_rid "$rid"
done
log "concluído (versão $version)"
