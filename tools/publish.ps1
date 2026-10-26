# tools/publish.ps1 —— 一条命令完成「发布 + 装到本机 + 自检」
#
# 用法：
#   tools\publish.ps1                          # 默认：框架依赖（主程序约 0.38 MB），系统需有 .NET 10 桌面运行时
#   tools\publish.ps1 -SelfContained           # 自包含（约 67 MB），拷到没装 .NET 的机器也能跑
#   tools\publish.ps1 -OutDir "D:\somewhere"   # 换安装目录
#   tools\publish.ps1 -NoInstall               # 只发布，不覆盖本机那份
#
# 产物是**两个**文件：
#   MouseWheelEnhancement.exe      启动器（.NET Framework 4.8，约 23 KB）—— 检查 .NET 10，缺了弹窗给下载按钮
#   MouseWheelEnhancement.App.exe  主程序（.NET 10 WPF）
# 另外打一个 zip，便于整包分发。
#
# 为什么写成脚本而不是让人记命令：
#   ① 两个项目、两个目标框架，参数漏一个就变成"对方打不开"；
#   ② 装之前必须**关掉正在运行的实例**，否则文件被占用；
#   ③ 装完顺手跑自检，避免留下一个跑不起来的包；
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
$launcher = Join-Path $root "Launcher\bin\$Configuration\MouseWheelEnhancement.exe"

Write-Host "==> 1/3 编译启动器（.NET Framework 4.8）" -ForegroundColor Cyan
dotnet build "Launcher\MouseWheelEnhancement.Launcher.csproj" -c $Configuration -v q --nologo
if ($LASTEXITCODE -ne 0) { throw "启动器编译失败（退出码 $LASTEXITCODE）" }
if (-not (Test-Path $launcher)) { throw "没找到启动器：$launcher" }

Write-Host "==> 2/3 发布主程序：$mode" -ForegroundColor Cyan
Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue
dotnet publish -c $Configuration -p:PublishSingleFile=true --self-contained $sc -o $stage -v q --nologo
if ($LASTEXITCODE -ne 0) { throw "发布失败（dotnet publish 退出码 $LASTEXITCODE）" }

$app = Join-Path $stage "MouseWheelEnhancement.App.exe"
if (-not (Test-Path $app)) { throw "没找到主程序产物：$app" }

# 把两个文件凑成一个安装包目录
$pkg = Join-Path $root "bin\package"
Remove-Item $pkg -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $pkg | Out-Null
Copy-Item $launcher (Join-Path $pkg "MouseWheelEnhancement.exe") -Force
Copy-Item $app (Join-Path $pkg "MouseWheelEnhancement.App.exe") -Force

$zip = Join-Path $root "bin\MouseWheelEnhancement-win-x64.zip"
Remove-Item $zip -Force -ErrorAction SilentlyContinue
Compress-Archive -Path (Join-Path $pkg "*") -DestinationPath $zip -CompressionLevel Optimal

$launcherSize = (Get-Item (Join-Path $pkg "MouseWheelEnhancement.exe")).Length
$appSize = (Get-Item $app).Length
$totalMb = ($launcherSize + $appSize) / 1MB
$sha = (Get-FileHash $app -Algorithm SHA256).Hash

Write-Host ("==> 3/3 产物：启动器 {0:N0} B + 主程序 {1:N0} B = {2:0.00} MB" -f $launcherSize, $appSize, $totalMb) -ForegroundColor Green
Write-Host "    主程序 SHA256 $sha"
Write-Host "    整包 $zip"

if ($NoInstall) {
    Write-Host "==> 跳过安装（-NoInstall）" -ForegroundColor Yellow
    Write-Host "    产物目录：$pkg"
    return
}

Write-Host "==> 装到 $OutDir" -ForegroundColor Cyan
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

# 正在运行的实例会锁住文件，先退出
Get-Process -Name "MouseWheelEnhancement*" -ErrorAction SilentlyContinue | ForEach-Object {
    Write-Host "    先退出正在运行的实例 (PID $($_.Id) $($_.ProcessName))" -ForegroundColor Yellow
    $_.CloseMainWindow() | Out-Null
    Start-Sleep -Milliseconds 600
    if (-not $_.HasExited) { $_.Kill() }
    $_.WaitForExit(3000) | Out-Null
}

Copy-Item (Join-Path $pkg "MouseWheelEnhancement.exe") (Join-Path $OutDir "MouseWheelEnhancement.exe") -Force
Copy-Item (Join-Path $pkg "MouseWheelEnhancement.App.exe") (Join-Path $OutDir "MouseWheelEnhancement.App.exe") -Force
Write-Host "    已安装到：$OutDir" -ForegroundColor Green

Write-Host "==> 自检" -ForegroundColor Cyan
& (Join-Path $OutDir "MouseWheelEnhancement.exe") --dialogtest | Out-Null
if ($LASTEXITCODE -ne 0) {
    Write-Host "    启动器自检失败！看 $(Join-Path $OutDir 'Mouse wheel enhancement data\launcher-selftest.txt')" -ForegroundColor Red
    exit $LASTEXITCODE
}
& (Join-Path $OutDir "MouseWheelEnhancement.App.exe") --selftest | Out-Null
if ($LASTEXITCODE -ne 0) {
    Write-Host "    主程序自检失败！看 $(Join-Path $OutDir 'Mouse wheel enhancement data\selftest.txt')" -ForegroundColor Red
    exit $LASTEXITCODE
}
Write-Host "    两个自检都通过" -ForegroundColor Green

Write-Host ""
Write-Host ("完成。{0:0.00} MB，双击 {1} 即可运行。" -f $totalMb, (Join-Path $OutDir "MouseWheelEnhancement.exe")) -ForegroundColor Cyan
