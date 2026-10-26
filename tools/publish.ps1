# tools/publish.ps1 —— 一条命令完成「发布 + 装到本机 + 自检」
#
# 用法：
#   tools\publish.ps1                          # 默认：框架依赖（约 0.48 MB）
#   tools\publish.ps1 -SelfContained           # 自包含（约 65 MB），没装 .NET 的机器也能跑
#   tools\publish.ps1 -OutDir "D://somewhere"   # 换安装目录
#   tools\publish.ps1 -NoInstall               # 只发布，不覆盖本机那份
#
# 产物只有**一个** exe：MouseWheelEnhancement.exe
#   主程序（.NET 10）作为嵌入资源打在里面，运行时释放到 %LOCALAPPDATA% 再执行。
#   用户看到的、能拷走的、能删的，都只有一个文件。
#
# 构建顺序不能反：**先发布主程序，再编译启动器** —— 启动器要把主程序当资源嵌进去。

param(
    [string]$OutDir = "D://software//myself",
    [string]$Configuration = "Release",
    [switch]$SelfContained,
    [switch]$NoInstall
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
Set-Location $root

$mode = if ($SelfContained) { "自包含（含 .NET 运行时）" } else { "框架依赖（用系统已装的 .NET 10 桌面运行时）" }
$stage = Join-Path $root "bin\publish-stage"

Write-Host "==> 1/3 发布主程序：$mode" -ForegroundColor Cyan
Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue
if ($SelfContained) {
    dotnet publish -c $Configuration -r win-x64 -p:PublishSingleFile=true --self-contained true -o $stage -v q --nologo
} else {
    dotnet publish -c $Configuration -p:PublishSingleFile=true --self-contained false -o $stage -v q --nologo
}
if ($LASTEXITCODE -ne 0) { throw "主程序发布失败（退出码 $LASTEXITCODE）" }

$app = Join-Path $stage "MouseWheelEnhancement.App.exe"
if (-not (Test-Path $app)) { throw "没找到主程序产物：$app" }

$ver = (Get-Item $app).VersionInfo.FileVersion
Write-Host ("    主程序 {0:N0} B，版本 {1}" -f (Get-Item $app).Length, $ver)

Write-Host "==> 2/3 编译启动器（把主程序嵌进去）" -ForegroundColor Cyan
dotnet build "Launcher\MouseWheelEnhancement.Launcher.csproj" -c $Configuration -v q --nologo -p:AppPayload="$app" -p:Version="$ver"
if ($LASTEXITCODE -ne 0) { throw "启动器编译失败（退出码 $LASTEXITCODE）" }

$launcher = Join-Path $root "Launcher\bin\$Configuration\MouseWheelEnhancement.exe"
if (-not (Test-Path $launcher)) { throw "没找到启动器：$launcher" }

$pkg = Join-Path $root "bin\package"
Remove-Item $pkg -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $pkg | Out-Null
Copy-Item $launcher (Join-Path $pkg "MouseWheelEnhancement.exe") -Force

$one = Join-Path $pkg "MouseWheelEnhancement.exe"
$size = (Get-Item $one).Length
$zip = Join-Path $root "bin\MouseWheelEnhancement-win-x64.zip"
Remove-Item $zip -Force -ErrorAction SilentlyContinue
Compress-Archive -Path $one -DestinationPath $zip -CompressionLevel Optimal

$sha = (Get-FileHash $one -Algorithm SHA256).Hash
Write-Host ("==> 3/3 产物：单个 exe {0:N0} B（{1:0.00} MB）" -f $size, ($size / 1MB)) -ForegroundColor Green
Write-Host "    SHA256 $sha"
Write-Host "    整包 $zip"

if ($NoInstall) {
    Write-Host "==> 跳过安装（-NoInstall）" -ForegroundColor Yellow
    return
}

Write-Host "==> 装到 $OutDir" -ForegroundColor Cyan
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

Get-Process -Name "MouseWheelEnhancement*" -ErrorAction SilentlyContinue | ForEach-Object {
    Write-Host "    先退出正在运行的实例 (PID $($_.Id) $($_.ProcessName))" -ForegroundColor Yellow
    $_.CloseMainWindow() | Out-Null
    Start-Sleep -Milliseconds 600
    if (-not $_.HasExited) { $_.Kill() }
    $_.WaitForExit(3000) | Out-Null
}

# 清掉上一版留下的 app 子目录（v1.3.0 早期是「exe + app\」两个可见文件）
$oldAppDir = Join-Path $OutDir "app"
if (Test-Path $oldAppDir) {
    Remove-Item $oldAppDir -Recurse -Force
    Write-Host "    清理旧布局：app\ 子目录" -ForegroundColor Yellow
}
$legacy = Join-Path $OutDir "MouseWheelEnhancement.App.exe"
if (Test-Path $legacy) {
    Remove-Item $legacy -Force
    Write-Host "    清理旧布局：根目录的 MouseWheelEnhancement.App.exe" -ForegroundColor Yellow
}

Copy-Item $one (Join-Path $OutDir "MouseWheelEnhancement.exe") -Force
Write-Host "    已安装到：$OutDir（只有一个 exe）" -ForegroundColor Green

Write-Host "==> 自检" -ForegroundColor Cyan
& (Join-Path $OutDir "MouseWheelEnhancement.exe") --dialogtest | Out-Null
if ($LASTEXITCODE -ne 0) {
    Write-Host "    启动器自检失败！看 $(Join-Path $OutDir 'Mouse wheel enhancement data\launcher-selftest.txt')" -ForegroundColor Red
    exit $LASTEXITCODE
}
# 主程序自检：启动器会把它释放到缓存并转发参数，跑的就是真实链路
& (Join-Path $OutDir "MouseWheelEnhancement.exe") --selftest | Out-Null
if ($LASTEXITCODE -ne 0) {
    Write-Host "    主程序自检失败！看 $(Join-Path $OutDir 'Mouse wheel enhancement data\selftest.txt')" -ForegroundColor Red
    exit $LASTEXITCODE
}
Write-Host "    两个自检都通过" -ForegroundColor Green

Write-Host ""
Write-Host ("完成。一个 exe，{0:0.00} MB，双击 {1} 即可运行。" -f ($size / 1MB), (Join-Path $OutDir "MouseWheelEnhancement.exe")) -ForegroundColor Cyan
