# 一键发布 Windows x64 单文件版本。
# 用法：
#   .\publish.ps1                    # 发布到仓库根目录的 .\run 下
#   .\publish.ps1 -Output D:\out      # 发布到指定目录
param(
    [string]$Output = (Join-Path $PSScriptRoot "run")
)

$ErrorActionPreference = "Stop"

Write-Host ">>> 正在发布 DesktopOrganizer（win-x64 单文件自包含）..." -ForegroundColor Cyan

dotnet publish (Join-Path $PSScriptRoot "src\DesktopOrganizer") `
    -c Release `
    -r win-x64 `
    --self-contained `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -o $Output

if ($LASTEXITCODE -ne 0) {
    Write-Host "!!! 发布失败（exit code $LASTEXITCODE）" -ForegroundColor Red
    exit $LASTEXITCODE
}

$exe = Join-Path $Output "DesktopOrganizer.exe"
Write-Host ">>> 发布完成。" -ForegroundColor Green
Write-Host "    exe: $exe"
Write-Host "    运行：直接在文件管理器中双击即可（无需 .NET SDK）。"