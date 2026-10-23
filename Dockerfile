# syntax=docker/dockerfile:1
#
# Imagem multi-arquitetura (linux/amd64 e linux/arm64) do Brymen DMM REST.
#
#   docker buildx build --platform linux/amd64,linux/arm64 -t brymen-dmm:0.1.0 --push .
#   docker buildx build --platform linux/arm64 -t brymen-dmm:0.1.0 --load .
#
# Sem o BuildKit (Docker sem o plugin buildx, ou DOCKER_BUILDKIT=0), o builder legado não define o BUILDPLATFORM nem o
# TARGETARCH: passe os dois, com a arquitetura da máquina do build e a do alvo (amd64 ou arm64). Ex., num Raspberry Pi
# ou Orange Pi de 64 bits:
#   DOCKER_BUILDKIT=0 docker build --build-arg BUILDPLATFORM=linux/arm64 --build-arg TARGETARCH=arm64 -t brymen-dmm:0.1.0 .
#
# O SDK roda na arquitetura de quem compila ($BUILDPLATFORM) e publica para a arquitetura alvo
# ($TARGETARCH): não há emulação QEMU no build. O executável é self-contained e trimmed, por isso o
# runtime é a imagem runtime-deps, sem .NET instalado.
#
# Acesso ao cabo: veja docker-compose.yml (devices + group_add) e a seção Docker do README.md.

ARG DOTNET_VERSION=10.0
# Declarado antes do 1º FROM para o builder legado, que só substitui num FROM os ARG do escopo global (sem isto, o
# --build-arg BUILDPLATFORM não chega ao FROM e o build falha com 'failed to parse platform : "" is an invalid OS
# component'). Sem valor padrão: no BuildKit, a declaração sem valor mantém o BUILDPLATFORM automático, e um padrão
# tomaria o lugar dele (o estágio de build rodaria emulado na arquitetura do padrão).
ARG BUILDPLATFORM

FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:${DOTNET_VERSION} AS build
ARG TARGETARCH
ARG CONFIGURATION=Release
WORKDIR /source

# /data do estágio final nasce aqui, na arquitetura de quem compila: um RUN no estágio final rodaria
# na arquitetura alvo e exigiria emulação (QEMU) no build cross-arch.
RUN mkdir -p /rootfs/data

# O global.json fica de fora: ele fixa o SDK do desenvolvimento, e aqui quem fixa é a tag da imagem.
# Restore em camada própria, refeita só quando um .csproj ou o Directory.Build.props muda.
# As propriedades precisam ser as mesmas do publish (RID, self-contained e trimming).
COPY Directory.Build.props ./
COPY src/Brymen.Dmm.Protocol/Brymen.Dmm.Protocol.csproj src/Brymen.Dmm.Protocol/
COPY src/Brymen.Dmm.Hid/Brymen.Dmm.Hid.csproj src/Brymen.Dmm.Hid/
COPY src/Brymen.Dmm/Brymen.Dmm.csproj src/Brymen.Dmm/
COPY src/Brymen.Dmm.Api/Brymen.Dmm.Api.csproj src/Brymen.Dmm.Api/
RUN dotnet restore src/Brymen.Dmm.Api/Brymen.Dmm.Api.csproj -a "$TARGETARCH" \
        -p:SelfContained=true -p:PublishTrimmed=true -p:PublishSingleFile=true

COPY src/ src/
# O transporte Simulated embute as capturas reais do BM525s (EmbeddedResource do Brymen.Dmm.Hid).
COPY docs/protocol/captures/ docs/protocol/captures/
# -a amd64/arm64 vira o RID linux-x64/linux-arm64 (o SO vem da imagem do SDK).
RUN dotnet publish src/Brymen.Dmm.Api/Brymen.Dmm.Api.csproj --no-restore \
        -c "$CONFIGURATION" -a "$TARGETARCH" --self-contained true \
        -p:PublishTrimmed=true -p:PublishSingleFile=true -p:DebugType=none \
        -p:PublishDocumentationFiles=false -p:IsTransformWebConfigDisabled=true \
        -o /app

FROM mcr.microsoft.com/dotnet/runtime-deps:${DOTNET_VERSION} AS runtime
LABEL org.opencontainers.image.title="brymen-dmm" \
      org.opencontainers.image.description="API REST para o multímetro Brymen BM525s (Minipa ET-2615A) pelo cabo óptico BC-86X"

# /data precisa existir com o dono certo antes do VOLUME, para que o volume nasça gravável.
# APP_UID (1654, usuário "app") vem da imagem base. Este estágio não tem RUN, para não precisar
# de emulação.
COPY --from=build --chown=$APP_UID:$APP_UID /rootfs/data /data

WORKDIR /app
COPY --from=build /app/ ./

# Urls sem prefixo prevalece sobre o appsettings.json; ASPNETCORE_HTTP_PORTS vazio evita o aviso
# de portas sobrescritas (a imagem base define 8080).
ENV Urls=http://+:5080 \
    ASPNETCORE_HTTP_PORTS= \
    DataDirectory=/data

EXPOSE 5080
VOLUME ["/data"]
USER $APP_UID

# A runtime-deps não tem curl nem wget; o bash fala HTTP pelo /dev/tcp. Se mudar a porta em Urls,
# ajuste também a porta abaixo.
HEALTHCHECK --interval=30s --timeout=5s --start-period=15s --retries=3 \
    CMD ["bash", "-c", "exec 3<>/dev/tcp/127.0.0.1/5080 && printf 'GET /health/live HTTP/1.1\\r\\nHost: localhost\\r\\nConnection: close\\r\\n\\r\\n' >&3 && head -n 1 <&3 | grep -q ' 200 '"]

ENTRYPOINT ["/app/Brymen.Dmm.Api"]
