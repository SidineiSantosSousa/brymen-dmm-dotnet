<#
.SYNOPSIS
Publica o Brymen.Dmm.Api para Linux e empacota cada RID em tar.gz.

.DESCRIPTION
Sem parâmetros, publica linux-arm64 e linux-x64 self-contained, single-file e trimmed.

Saída:
  artifacts/publish/<rid>/                    publish + deploy/linux + README.md
  artifacts/brymen-dmm-<versão>-<rid>.tar.gz  a pasta acima, com o topo brymen-dmm-<versão>-<rid>/

Com -FrameworkDependent (exige o runtime do ASP.NET Core 10 no alvo, sem trimming), os nomes
ganham o sufixo "-fd". Os arquivos de deploy/linux vão para o pacote com fim de linha LF.

.PARAMETER Rid
RIDs a publicar (padrão: linux-arm64 e linux-x64).

.PARAMETER FrameworkDependent
Publica sem o runtime embutido.

.PARAMETER Configuration
Configuração do build (padrão: Release).

.PARAMETER Version
Versão do pacote (padrão: <Version> do Directory.Build.props).

.EXAMPLE
./deploy/publish.ps1

.EXAMPLE
./deploy/publish.ps1 -Rid linux-arm64

.EXAMPLE
./deploy/publish.ps1 -Rid linux-x64 -FrameworkDependent
#>
[CmdletBinding()]
param(
    [ValidateSet('linux-arm64', 'linux-x64')]
    [string[]] $Rid = @('linux-arm64', 'linux-x64'),

    [switch] $FrameworkDependent,

    [ValidateNotNullOrEmpty()]
    [string] $Configuration = 'Release',

    [string] $Version
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$project = Join-Path $repoRoot 'src/Brymen.Dmm.Api/Brymen.Dmm.Api.csproj'
$artifacts = Join-Path $repoRoot 'artifacts'
$publishRoot = Join-Path $artifacts 'publish'
$executable = 'Brymen.Dmm.Api'
$deployFiles = '99-brymen-bu86x.rules', 'brymen-dmm.service', 'brymen-dmm.env', 'install.sh', 'uninstall.sh'
$utf8NoBom = New-Object System.Text.UTF8Encoding $false

function Invoke-Native {
    param(
        [Parameter(Mandatory)] [string] $FilePath,
        [Parameter(Mandatory)] [string[]] $ArgumentList
    )
    & $FilePath @ArgumentList
    if ($LASTEXITCODE -ne 0) {
        throw "'$FilePath $($ArgumentList -join ' ')' terminou com código $LASTEXITCODE."
    }
}

function Get-PropsVersion {
    $propsPath = Join-Path $repoRoot 'Directory.Build.props'
    $node = ([xml](Get-Content -LiteralPath $propsPath -Raw)).SelectSingleNode('/Project/PropertyGroup/Version')
    if ($null -eq $node -or [string]::IsNullOrWhiteSpace($node.InnerText)) {
        throw "Não foi possível ler <Version> de $propsPath; use -Version."
    }
    return $node.InnerText.Trim()
}

# No Windows o tar.exe do sistema é o bsdtar; o tar do Git for Windows (GNU) trataria "C:" como host remoto.
function Get-TarCommand {
    $systemTar = if ($env:SystemRoot) { Join-Path $env:SystemRoot 'System32\tar.exe' } else { $null }
    if ($systemTar -and (Test-Path -LiteralPath $systemTar)) {
        return $systemTar
    }
    $tar = Get-Command tar -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($null -eq $tar) {
        throw 'tar não encontrado no PATH.'
    }
    return $tar.Source
}

# Um checkout no Windows pode trazer CRLF, que o bash e o udev não aceitam.
function Copy-DeployFiles {
    param([Parameter(Mandatory)] [string] $Destination)

    $target = Join-Path $Destination 'deploy/linux'
    New-Item -ItemType Directory -Force -Path $target | Out-Null
    foreach ($file in $deployFiles) {
        $text = [System.IO.File]::ReadAllText((Join-Path $PSScriptRoot "linux/$file"))
        [System.IO.File]::WriteAllText((Join-Path $target $file), $text.Replace("`r`n", "`n"), $utf8NoBom)
    }
    $readme = Join-Path $repoRoot 'README.md'
    if (Test-Path -LiteralPath $readme) {
        Copy-Item -LiteralPath $readme -Destination (Join-Path $Destination 'README.md')
    }
}

function Test-TarWriter {
    try {
        Add-Type -AssemblyName System.Formats.Tar -ErrorAction Stop
        return $true
    }
    catch {
        return $false
    }
}

# Com System.Formats.Tar (PowerShell 7.3+), o pacote leva o modo Unix de cada arquivo: o executável e
# os scripts saem com 0755 e o dono é root (0:0), mesmo quando o pacote é gerado no Windows.
function Write-TarGz {
    param(
        [Parameter(Mandatory)] [string] $Source,
        [Parameter(Mandatory)] [string] $Archive,
        [Parameter(Mandatory)] [string] $Prefix
    )
    $executableMode = [System.IO.UnixFileMode]'UserRead, UserWrite, UserExecute, GroupRead, GroupExecute, OtherRead, OtherExecute'
    $fileMode = [System.IO.UnixFileMode]'UserRead, UserWrite, GroupRead, OtherRead'
    $executables = @($executable, 'deploy/linux/install.sh', 'deploy/linux/uninstall.sh')

    $root = (Resolve-Path -LiteralPath $Source).Path
    $items = @(Get-ChildItem -LiteralPath $root -Recurse -Force | Sort-Object FullName)

    $stream = [System.IO.File]::Create($Archive)
    try {
        $gzip = New-Object System.IO.Compression.GZipStream($stream, [System.IO.Compression.CompressionLevel]::Optimal)
        try {
            $writer = New-Object System.Formats.Tar.TarWriter($gzip, [System.Formats.Tar.TarEntryFormat]::Pax, $false)
            try {
                $top = New-Object System.Formats.Tar.PaxTarEntry([System.Formats.Tar.TarEntryType]::Directory, "$Prefix/")
                $top.Mode = $executableMode
                $writer.WriteEntry($top)

                foreach ($item in $items) {
                    $relative = [System.IO.Path]::GetRelativePath($root, $item.FullName).Replace('\', '/')
                    if ($item.PSIsContainer) {
                        $entry = New-Object System.Formats.Tar.PaxTarEntry([System.Formats.Tar.TarEntryType]::Directory, "$Prefix/$relative/")
                        $entry.Mode = $executableMode
                    }
                    else {
                        $entry = New-Object System.Formats.Tar.PaxTarEntry([System.Formats.Tar.TarEntryType]::RegularFile, "$Prefix/$relative")
                        $entry.Mode = if ($executables -contains $relative) { $executableMode } else { $fileMode }
                        $entry.DataStream = [System.IO.File]::OpenRead($item.FullName)
                    }
                    $entry.ModificationTime = [DateTimeOffset]$item.LastWriteTimeUtc
                    try {
                        $writer.WriteEntry($entry)
                    }
                    finally {
                        if ($null -ne $entry.DataStream) {
                            $entry.DataStream.Dispose()
                        }
                    }
                }
            }
            finally {
                $writer.Dispose()
            }
        }
        finally {
            $gzip.Dispose()
        }
    }
    finally {
        $stream.Dispose()
    }
}

function New-Archive {
    param(
        [Parameter(Mandatory)] [string] $Folder,
        [Parameter(Mandatory)] [string] $Name
    )
    $source = Join-Path $publishRoot $Folder
    $archive = Join-Path $artifacts "$Name.tar.gz"
    if (Test-Path -LiteralPath $archive) {
        Remove-Item -LiteralPath $archive -Force
    }

    if (Test-TarWriter) {
        Write-TarGz -Source $source -Archive $archive -Prefix $Name
    }
    else {
        # Windows PowerShell 5.1: tar do sistema sobre uma cópia com o nome final, porque o bsdtar do
        # Windows não tem -s. Os bits de execução se perdem; o install.sh os restaura.
        $staging = Join-Path $artifacts '.package'
        if (Test-Path -LiteralPath $staging) {
            Remove-Item -LiteralPath $staging -Recurse -Force
        }
        New-Item -ItemType Directory -Force -Path $staging | Out-Null
        try {
            Copy-Item -LiteralPath $source -Destination (Join-Path $staging $Name) -Recurse
            Invoke-Native -FilePath (Get-TarCommand) -ArgumentList @('-czf', $archive, '-C', $staging, $Name)
        }
        finally {
            Remove-Item -LiteralPath $staging -Recurse -Force
        }
    }
    Write-Host "==> pacote: $archive"
}

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw 'dotnet não encontrado no PATH (instale o SDK do global.json).'
}
if (-not (Test-Path -LiteralPath $project)) {
    throw "Projeto não encontrado: $project"
}

$versionArgs = @()
if ($Version) {
    $versionArgs = @("-p:Version=$Version")
}
else {
    $Version = Get-PropsVersion
}
New-Item -ItemType Directory -Force -Path $publishRoot | Out-Null

foreach ($r in $Rid) {
    $suffix = if ($FrameworkDependent) { '-fd' } else { '' }
    $folder = "$r$suffix"
    $out = Join-Path $publishRoot $folder
    $name = "brymen-dmm-$Version-$r$suffix"

    $modeArgs = if ($FrameworkDependent) {
        @('--self-contained', 'false')
    }
    else {
        @('--self-contained', 'true', '-p:PublishTrimmed=true')
    }

    $mode = if ($FrameworkDependent) { 'framework-dependent' } else { 'self-contained' }
    Write-Host "==> publicando $r ($Configuration, $mode) em $out"
    if (Test-Path -LiteralPath $out) {
        Remove-Item -LiteralPath $out -Recurse -Force
    }

    $publishArgs = @(
        'publish', $project, '-c', $Configuration, '-r', $r, '-o', $out
        '-p:PublishSingleFile=true', '-p:DebugType=none'
        '-p:PublishDocumentationFiles=false', '-p:IsTransformWebConfigDisabled=true'
    ) + $modeArgs + $versionArgs
    Invoke-Native -FilePath 'dotnet' -ArgumentList $publishArgs

    if (-not (Test-Path -LiteralPath (Join-Path $out $executable))) {
        throw "O publish não gerou $(Join-Path $out $executable)."
    }
    Copy-DeployFiles -Destination $out
    New-Archive -Folder $folder -Name $name
}

Write-Host "==> concluído (versão $Version)"
