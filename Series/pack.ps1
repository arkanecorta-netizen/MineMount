<#
.SYNOPSIS
  Crea el paquete ZIP de una Serie para publicarlo en GitHub Releases.

.DESCRIPTION
  Uso:
    .\pack.ps1 -Id NSE6 -Version 1.0.0

  Entrada:  Series\<Id>\NseResources\   (mods, config, resourcepacks, shaderpacks, ...)
  Salida:   Series\<Id>\<Id>-Resources.zip
            (contenido de NseResources\ + manifest.json con sha256 de cada archivo)

  Publicación manual en GitHub (muy importante):
    1. git commit de catalog.json y assets.
    2. Crear el release con tag:  <id en minúsculas>-v<version>   (ej: nse6-v1.0.0)
    3. Marcarlo como PRERELEASE. Así GitHub lo excluye de /releases/latest
       y el updater de MineMount (que consulta /releases/latest) no se rompe.
    4. Subir <Id>-Resources.zip como asset del release.
    5. Actualizar version/tag en Series\catalog.json.

.NOTES
  El launcher descarga el asset, valida manifest.json (sha256), extrae a staging
  y fusiona los archivos en %USERPROFILE%\Documents\MineMount\Series\<Id>\.
#>

[CmdletBinding()]
param(
    [string]$Id = "NSE6",
    [string]$Version = "1.0.0"
)

$ErrorActionPreference = "Stop"

$seriesRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$seriesDir = Join-Path $seriesRoot $Id
$sourceDir = Join-Path $seriesDir "NseResources"
$zipName = "$Id-Resources.zip"
$zipPath = Join-Path $seriesDir $zipName
$stageDir = Join-Path $seriesDir "stage"

if (-not (Test-Path $sourceDir)) {
    Write-Error "No existe la carpeta de recursos: $sourceDir"
}

if (Test-Path $stageDir) { Remove-Item $stageDir -Recurse -Force }
New-Item -ItemType Directory -Path $stageDir | Out-Null

Get-ChildItem -Path $sourceDir -Recurse -File | ForEach-Object {
    $relative = $_.FullName.Substring($sourceDir.Length + 1)
    $dest = Join-Path $stageDir $relative
    New-Item -ItemType Directory -Path (Split-Path -Parent $dest) -Force | Out-Null
    Copy-Item $_.FullName $dest
}

# manifest.json con sha256/size de cada archivo (verificado por el launcher)
$files = @()
Get-ChildItem -Path $stageDir -Recurse -File | Where-Object { $_.Name -ne "manifest.json" } | ForEach-Object {
    $relative = $_.FullName.Substring($stageDir.Length + 1).Replace("\", "/")
    $files += [pscustomobject]@{
        path   = $relative
        sha256 = (Get-FileHash $_.FullName -Algorithm SHA256).Hash
        size   = $_.Length
    }
}

$manifest = [pscustomobject]@{
    id      = $Id
    version = $Version
    files   = $files
}

$manifestPath = Join-Path $stageDir "manifest.json"
$manifest | ConvertTo-Json -Depth 5 | Set-Content -Path $manifestPath -Encoding UTF8

if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
Compress-Archive -Path (Join-Path $stageDir "*") -DestinationPath $zipPath -CompressionLevel Optimal
Remove-Item $stageDir -Recurse -Force

$zipHash = (Get-FileHash $zipPath -Algorithm SHA256).Hash
$zipSize = (Get-Item $zipPath).Length
$tag = "$($Id.ToLower())-v$Version"

Write-Host ""
Write-Host "Paquete creado: $zipPath" -ForegroundColor Green
Write-Host "  Archivos : $($files.Count)"
Write-Host "  Tamano   : $([math]::Round($zipSize / 1MB, 2)) MB"
Write-Host "  SHA256   : $zipHash"
Write-Host ""
Write-Host "Siguiente paso (release en GitHub):" -ForegroundColor Cyan
Write-Host "  tag    : $tag"
Write-Host("  asset  : " + $zipName)
Write-Host "  marca  : PRERELEASE (obligatorio para no romper /releases/latest)"
Write-Host "  update : version + tag en Series\catalog.json"
