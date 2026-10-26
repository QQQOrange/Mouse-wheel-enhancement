# tools/publish.ps1 —— 一条命令完成「发布 + 装到本机 + 自检」
#
# 用法：
#   powershell -ExecutionPolicy Bypass -File tools\publish.ps1
#   powershell -ExecutionPolicy Bypass -File tools\publish.ps1 -OutDir "D:\software\myself"
#   powershell -ExecutionPolicy Bypass -File tools\publish.ps1 -NoInstall   # 只发布，不覆盖本机那份
#
# 为什么写成脚本而不是让人记三条命令：
#   ① 单文件自包含发布要带 -r win-x64 --self-contained，漏一个参数就变成"对方打不开"；
#   ② 装的时候必须**关掉正在运行的实例**，否则文件被占用、复制失败；
#   ③ 装完顺手跑一次 --selftest，避免把一个跑不起来的包留下来。

param(
    [string]$OutDir = "D:\software\myself",
    [string]$Configuration = "Release",
    [switch]$NoInstall
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
Set-Location $root

Write-Host "==> 发布单文件自包含 exe" -ForegroundColor Cyan
dotnet publish -c $Configuration -p:PublishSingleFile=true -r win-x64 --self-contained -v q --nologo
if ($LASTEXITCODE -ne 0) { throw "发布失败（dotnet publish 退出码 $LASTEXITCODE）" }

$exe = Join-Path $root "bin\$Configuration\net10.0-windows\win-x64\publish\MouseWheelEnhancement.exe"
if (-not (Test-Path $exe)) { throw "没找到产物：$exe" }
$size = [math]::Round((Get-Item $exe).Length / 1MB, 1)
Write-Host "    产物 $size MB：$exe" -ForegroundColor Green

if ($NoInstall) { Write-Host "==> 跳过安装（-NoInstall）" -ForegroundColor Yellow; return }

Write-Host "==> 装到 $OutDir" -ForegroundColor Cyan
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

# 正在运行的实例会锁住文件（Windows 锁写不锁改名，但这里要直接覆盖，所以先退掉）
Get-Process -Name "MouseWheelEnhancement" -ErrorAction SilentlyContinue | ForEach-Object {
    Write-Host "    先退出正在运行的实例 (PID $($_.Id))" -ForegroundColor Yellow
    $_.CloseMainWindow() | Out-Null
    Start-Sleep -Milliseconds 600
    if (-not $_.HasExited) { $_.Kill() }
    $_.WaitForExit(3000) | Out-Null
}

$target = Join-Path $OutDir "MouseWheelEnhancement.exe"
Copy-Item $exe $target -Force
Write-Host "    已安装：$target" -ForegroundColor Green

# 数据目录跟着 exe 走；跑一次自检顺便把它建出来，也验证这份包能跑
Write-Host "==> 自检" -ForegroundColor Cyan
& $target --selftest | Out-Null
if ($LASTEXITCODE -ne 0) {
    Write-Host "    自检失败！看 $(Join-Path $OutDir 'Mouse wheel enhancement data\selftest.txt')" -ForegroundColor Red
    exit $LASTEXITCODE
}
Write-Host "    自检通过" -ForegroundColor Green

Write-Host ""
Write-Host "完成。双击 $target 即可运行。" -ForegroundColor Cyan
