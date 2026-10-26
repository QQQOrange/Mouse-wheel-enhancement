# tools/publish.ps1 —— 一条命令完成「发布 + 装到本机 + 自检」
#
# 用法：
#   tools\publish.ps1                          # 默认：框架依赖（约 0.4 MB），要求系统里有 .NET 10 桌面运行时
#   tools\publish.ps1 -SelfContained           # 自包含（约 67 MB），拷到没装 .NET 的机器也能跑
#   tools\publish.ps1 -OutDir "D:\somewhere"   # 换安装目录
#   tools\publish.ps1 -NoInstall               # 只发布，不覆盖本机那份
#
# 为什么写成脚本而不是让人记命令：
#   ① 单文件发布的参数很多，漏一个就变成"对方打不开"；
#   ② 装之前必须**关掉正在运行的实例**，否则文件被占用；
#   ③ 装完顺手跑一次 --selftest，避免留下一个跑不起来的包；
#   ④ 顺手算出 SHA256，发版时要用。

param(
    [string]$OutDir = "D:\software\myself",
    [string]$Configuration = "Release",
    [switch]$SelfContained,
    [switch]$NoInstall
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
Set-Location $root

$mode = if ($SelfContained) { "自包含（含 .NET 运行时）" } else { "框架依赖（用系统已装的 .NET 10 桌面运行时）" }
$sc = if ($SelfContained) { "true" } else { "false" }
$stage = Join-Path $root "bin\publish-stage"

Write-Host "==> 发布单文件 exe：$mode" -ForegroundColor Cyan
Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue
dotnet publish -c $Configuration -p:PublishSingleFile=true --self-contained $sc -o $stage -v q --nologo
if ($LASTEXITCODE -ne 0) { throw "发布失败（dotnet publish 退出码 $LASTEXITCODE）" }

$exe = Join-Path $stage "MouseWheelEnhancement.exe"
if (-not (Test-Path $exe)) { throw "没找到产物：$exe" }

$sizeMb = (Get-Item $exe).Length / 1MB
$sha = (Get-FileHash $exe -Algorithm SHA256).Hash
Write-Host ("    产物 {0:0.00} MB" -f $sizeMb) -ForegroundColor Green
Write-Host "    SHA256 $sha"

if ($NoInstall) {
    Write-Host "==> 跳过安装（-NoInstall）" -ForegroundColor Yellow
    Write-Host "    产物目录：$stage"
    return
}

Write-Host "==> 装到 $OutDir" -ForegroundColor Cyan
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

# 正在运行的实例会锁住文件，先退出
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
Write-Host ("完成。{0:0.00} MB，双击 {1} 即可运行。" -f $sizeMb, $target) -ForegroundColor Cyan
