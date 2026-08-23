[CmdletBinding()]
param(
    [ValidatePattern('^\d+\.\d+\.\d+(\.\d+)?$')]
    [string]$Version = '1.0.0'
)

$ErrorActionPreference = 'Stop'
$sourceRoot = $PSScriptRoot
$project = Join-Path $sourceRoot 'StarTrace.Client\StarTrace.Client.csproj'
$installerProject = Join-Path $sourceRoot 'Installer\StarTrace.Installer.wixproj'
$publishDirectory = Join-Path $sourceRoot 'artifacts\win-x64'
$installerDirectory = Join-Path $sourceRoot 'artifacts\installer'

if (Get-Process -Name 'StarTrace.Client' -ErrorAction SilentlyContinue) {
    throw 'StarTrace 客户端正在运行。请关闭客户端后重新构建安装包。'
}

New-Item -ItemType Directory -Force -Path $publishDirectory, $installerDirectory | Out-Null

dotnet publish $project `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:Platform=x64 `
    -p:PublishSingleFile=false `
    -o $publishDirectory
if ($LASTEXITCODE -ne 0) { throw 'StarTrace 客户端发布失败。' }

dotnet build $installerProject `
    -c Release `
    -p:PublishDir=$publishDirectory `
    -p:InstallerVersion=$Version `
    -p:OutputPath=$installerDirectory
if ($LASTEXITCODE -ne 0) { throw 'StarTrace MSI 构建失败。' }

$msi = Join-Path $installerDirectory 'StarTrace-Setup-x64.msi'
if (-not (Test-Path -LiteralPath $msi)) { throw "未找到预期安装包：$msi" }
Write-Output "MSI ready: $msi"
