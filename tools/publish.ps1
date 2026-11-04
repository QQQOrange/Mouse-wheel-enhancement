# tools/publish.ps1 —— 一条命令完成「发布 + 自检 + 装到本机」
#
# 用法：
#   tools\publish.ps1                          # 默认：框架依赖（约 0.65 MB）
#   tools\publish.ps1 -SelfContained           # 自包含（约 65 MB），没装 .NET 的机器也能跑
#   tools\publish.ps1 -OutDir "D:\somewhere"   # 换安装目录
#   tools\publish.ps1 -NoInstall               # 只发布，不覆盖本机那份
#
# 产物只有**一个** exe：MouseWheelEnhancement.exe
#   主程序（.NET 10）作为嵌入资源打在里面，运行时释放到 %LOCALAPPDATA% 再执行。
#   用户看到的、能拷走的、能删的，都只有一个文件。
#
# 构建顺序不能反：**先发布主程序，再编译启动器** —— 启动器要把主程序当资源嵌进去。
#
# ⚠ 两个 SHA256 别弄混：
#   · 启动器（MouseWheelEnhancement.exe）—— 用户双击的那个整包；
#   · 主程序（MouseWheelEnhancement.App.exe）—— **自动更新下载并校验的就是它**。
#   发版说明里要写的是**主程序**的哈希（见 UpdateService.AssetName）。
#   写错成启动器的哈希，客户端每次更新都会报"校验失败"。

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
$stage = Join-Path $root "bin\publish-stage"

Write-Host "==> 1/4 发布主程序：$mode" -ForegroundColor Cyan
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
if ([string]::IsNullOrWhiteSpace($ver)) { throw "主程序没有文件版本 —— 检查 csproj 里的 <FileVersion>" }
Write-Host ("    主程序 {0:N0} B，版本 {1}" -f (Get-Item $app).Length, $ver)

Write-Host "==> 2/4 编译启动器（把主程序嵌进去）" -ForegroundColor Cyan
# 整体一个 token（含空格的路径不加引号会按空格拆开，AppPayload 就传丢了）
dotnet build "Launcher\MouseWheelEnhancement.Launcher.csproj" -c $Configuration -v q --nologo "-p:AppPayload=$app" "-p:Version=$ver"
if ($LASTEXITCODE -ne 0) { throw "启动器编译失败（退出码 $LASTEXITCODE）" }

$launcher = Join-Path $root "Launcher\bin\$Configuration\MouseWheelEnhancement.exe"
if (-not (Test-Path $launcher)) { throw "没找到启动器：$launcher" }

$pkg = Join-Path $root "bin\package"
Remove-Item $pkg -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $pkg | Out-Null

$one = Join-Path $pkg "MouseWheelEnhancement.exe"
Copy-Item $launcher $one -Force

# 内嵌校验：启动器必须比主程序大（主程序是真的被嵌进去了）。
# 少这一条的话，-p:AppPayload 因路径问题失效时产物依然"能编译、能自检"，
# 直到用户双击才报"这个 exe 里没有内嵌的主程序"。
$oneSize = (Get-Item $one).Length
$appSize = (Get-Item $app).Length
if ($oneSize -le $appSize) {
    throw "启动器只有 $oneSize B，不比主程序 $appSize B 大 —— 主程序没被嵌进去（检查 -p:AppPayload）"
}

$shaApp = (Get-FileHash $app -Algorithm SHA256).Hash
$shaLauncher = (Get-FileHash $one -Algorithm SHA256).Hash
# 给发版脚本（tools/make-release.py）读：自动更新校验的是主程序这一份
Set-Content -LiteralPath (Join-Path $pkg "SHA256.txt") -Value $shaApp -Encoding ascii -NoNewline

$zip = Join-Path $root "bin\MouseWheelEnhancement-win-x64.zip"
Remove-Item $zip -Force -ErrorAction SilentlyContinue
Compress-Archive -Path $one -DestinationPath $zip -CompressionLevel Optimal

Write-Host "==> 3/4 自检（对**新产物**跑，不先覆盖本机）" -ForegroundColor Cyan
& $one --dialogtest | Out-Null
if ($LASTEXITCODE -ne 0) {
    Write-Host "    启动器自检失败！看 $(Join-Path $pkg 'Mouse wheel enhancement data\launcher-selftest.txt')" -ForegroundColor Red
    throw "发布中止：新产物没通过启动器自检（本机未受影响）"
}
Write-Host "    启动器自检通过" -ForegroundColor Green

Write-Host ("==> 4/4 产物：单个 exe {0:N0} B（{1:0.00} MB）" -f $oneSize, ($oneSize / 1MB)) -ForegroundColor Green
Write-Host "    整包     $zip"
Write-Host "    启动器 SHA256 $shaLauncher  （仅供核对，自动更新不校验它）"
Write-Host "    主程序 SHA256 $shaApp  <-- 发版说明里写这一行" -ForegroundColor Yellow
Write-Host "               Release 说明需要包含：SHA256: $shaApp"

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
    if (-not $_.HasExited) {
        # 以管理员身份运行的实例杀不掉（Access is denied）。
        # 这不是安装失败：单文件 exe 的映像不锁文件，照样能覆盖；
        # 新版本会在用户下次启动时生效。所以只警告，不中断。
        try { $_.Kill() } catch {
            Write-Host "    跳过（拒绝访问，可能以管理员运行）：新版本会在下次启动时生效" -ForegroundColor DarkYellow
        }
    }
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

# 先备份再覆盖：自检失败时能原样退回去（以前是覆盖完才发现坏，用户手里就没好的了）
$dest = Join-Path $OutDir "MouseWheelEnhancement.exe"
$backup = "$dest.old"
if (Test-Path $dest) { Copy-Item $dest $backup -Force }
Copy-Item $one $dest -Force
Write-Host "    已安装到：$OutDir（只有一个 exe）" -ForegroundColor Green

Write-Host "==> 装机自检（走真实链路：启动器 → 释放主程序 → 转发参数）" -ForegroundColor Cyan
$failed = $false

& $dest --dialogtest | Out-Null
if ($LASTEXITCODE -ne 0) {
    Write-Host "    启动器自检失败" -ForegroundColor Red
    $failed = $true
}

if (-not $failed) {
    & $dest --selftest | Out-Null
    if ($LASTEXITCODE -ne 0) {
        Write-Host "    主程序自检失败！看 $(Join-Path $OutDir 'Mouse wheel enhancement data\selftest.txt')" -ForegroundColor Red
        $failed = $true
    }
}

if ($failed) {
    if (Test-Path $backup) {
        Copy-Item $backup $dest -Force
        Remove-Item $backup -Force -ErrorAction SilentlyContinue
        Write-Host "    已回滚到安装前的版本" -ForegroundColor Yellow
    } else {
        Remove-Item $dest -Force -ErrorAction SilentlyContinue
        Write-Host "    安装前的版本不存在，已删掉这份没通过自检的产物" -ForegroundColor Yellow
    }
    throw "发布中止：装机自检没通过"
}

Remove-Item $backup -Force -ErrorAction SilentlyContinue
Write-Host "    两个自检都通过" -ForegroundColor Green

Write-Host ""
Write-Host ("完成。一个 exe，{0:0.00} MB，双击 {1} 即可运行。" -f ($oneSize / 1MB), $dest) -ForegroundColor Cyan
