# 一键发布 Windows x64 单文件版本，并同步到外层 run 目录（消除"跑错旧 exe"的反复踩坑）。
# 用法：
#   .\publish.ps1                    # 发布到仓库内 .\run，并同步到 ..\run（桌面整理工具\run）
#   .\publish.ps1 -Output D:\out      # 发布到指定目录（不自动同步外层）
#   .\publish.ps1 -Release            # 额外把 exe 上传到 GitHub release v1.1.0（需 gh 已登录）
param(
    [string]$Output = (Join-Path $PSScriptRoot "run"),
    [switch]$Release
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
if (-not (Test-Path $exe)) {
    Write-Host "!!! 发布产物缺失：$exe" -ForegroundColor Red
    exit 1
}

# 记录版本（git 短哈希 + 构建时间）到 VERSION.txt，供主窗口显示"当前跑的是哪一版"。
$version = "unknown"
try {
    $hash = (git -C $PSScriptRoot rev-parse --short HEAD 2>$null).Trim()
    if ($hash) { $version = "$hash @ $(Get-Date -Format 'yyyy-MM-dd HH:mm')" }
} catch { }
$version | Out-File -FilePath (Join-Path $Output "VERSION.txt") -Encoding utf8 -NoNewline
Write-Host "    版本：$version" -ForegroundColor DarkGray

function Get-Md5($path) {
    $algo = [System.Security.Cryptography.MD5]::Create()
    $bytes = [System.IO.File]::ReadAllBytes($path)
    ($algo.ComputeHash($bytes) | ForEach-Object { $_.ToString("x2") }) -join ""
}

$innerHash = Get-Md5 $exe
Write-Host ">>> 发布完成。" -ForegroundColor Green
Write-Host "    exe: $exe"
Write-Host "    运行：直接在文件管理器中双击即可（无需 .NET SDK）。"

# 自动同步到外层 run 目录（桌面整理工具\run）。这是反复踩坑点：用户常双击外层那份旧 exe。
if (-not $Output.EndsWith([System.IO.Path]::DirectorySeparatorChar + "run")) {
    Write-Host ">>> -Output 非默认 run，跳过外层同步。" -ForegroundColor DarkGray
} else {
    $outerRun = Join-Path $PSScriptRoot "..\run"   # DesktopOrganizer\..\run = 桌面整理工具\run
    $outerExe = Join-Path $outerRun "DesktopOrganizer.exe"
    try {
        if (Test-Path $outerRun) {
            Copy-Item $exe $outerExe -Force
            # 同步 pdb（调试用，非必需但保持一致）
            $pdb = Join-Path $Output "DesktopOrganizer.pdb"
            if (Test-Path $pdb) { Copy-Item $pdb (Join-Path $outerRun "DesktopOrganizer.pdb") -Force }
            Copy-Item (Join-Path $Output "VERSION.txt") (Join-Path $outerRun "VERSION.txt") -Force
            $outerHash = Get-Md5 $outerExe
            if ($innerHash -eq $outerHash) {
                Write-Host ">>> 外层 run 已同步，两份 exe 哈希一致：$innerHash" -ForegroundColor Green
            } else {
                Write-Host "!!! 外层 run 同步后哈希不一致（内层 $innerHash / 外层 $outerHash），请检查。" -ForegroundColor Red
            }
        } else {
            Write-Host ">>> 外层 run 目录不存在，跳过同步：$outerRun" -ForegroundColor DarkGray
        }
    } catch {
        Write-Host "!!! 外层 run 同步失败（可能被占用？）：$_" -ForegroundColor Red
        Write-Host "    请先关闭正在运行的 DesktopOrganizer 再发布。" -ForegroundColor Yellow
    }
}

# 可选：上传到 GitHub release。
if ($Release) {
    $tag = "v1.1.0"
    if (-not (Get-Command gh -ErrorAction SilentlyContinue)) {
        Write-Host "!!! 未找到 gh，跳过 release 上传。" -ForegroundColor Red
    } else {
        Write-Host ">>> 上传到 GitHub release $tag ..." -ForegroundColor Cyan
        gh release upload $tag $exe --clobber
        Write-Host ">>> release 上传完成。" -ForegroundColor Green
    }
}
