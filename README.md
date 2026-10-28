# 鼠标滚轮增强 · Mouse wheel enhancement

> 普通滚动一个字都不改，只在**快速拨动滚轮**时按可调倍率加速；可选平滑推进；可按应用启用/禁用。

Windows 桌面小工具 · **就一个 exe，0.47 MB** · 无驱动 · 无后台服务 · 数据就在软件自己的文件夹里

- 中文名：**鼠标滚轮增强**
- 英文名：**Mouse wheel enhancement**
- 开发者：**QQQOrange**
- 项目主页 / GitHub：<https://github.com/QQQOrange/Mouse-wheel-enhancement>
- 克隆：`git clone git@github.com:QQQOrange/Mouse-wheel-enhancement.git`

---

## 它解决什么问题

滚轮在"需要翻很远"的时候太慢。这个工具只在**快速连续拨动**的那一瞬间介入，把这一段滚动的距离按倍率放大；你一放慢，它立刻退场，把控制权原样交回 Windows。

它不改鼠标速度/指针设置、不装驱动、不做任何全局 hook 之外的事。唯一会碰的系统设置是「每次滚动几行」——**只在你于设置页拖动那一项时**写，启动和退出都不碰它。

## 三条核心规则

1. **慢速滚动＝完全原生。** 不该接管时直接 `CallNextHookEx` 放行，不做"1.0 倍重发"。手感、精度、惯性与你没装它时逐字节一致。
2. **快滚才加速。** 判定依据是"每秒滚多少格"而不是"第几格"，所以逐格微调不会被误伤，连拨翻页不会误判。
3. **随时可退出链路。** 总开关、名单命中、Ctrl/Alt/Shift 组合、反向纠正，任何一种情况都立刻回到原生行为。

## 功能

| 功能 | 说明 |
|---|---|
| 快速滚动加速 | 触发阈值、满速点、倍率上限（1×–8×）全部可调，默认 10 格/秒 → 3×；所有数值都是整格，没有小数点 |
| 平滑滚动 | 把一次滚动拆成多帧推进（每格 4 帧），出帧节奏自动跟着显示器刷新率，接近触控板手感；收尾按指数曲线**渐停**（不足一格时也不会有"1 单位/帧"的恒定速度，结尾不会突然刹住） |
| 每格滚动格数 | 与系统「每次滚动以下行数」双向同步：这里调立刻写进系统，在系统里改回来后自动读回；软件不另存一份，不会出现两边不一致 |
| 应用黑白名单 | 按 exe 名匹配，可选窗口标题正则；托盘里一键切换 |
| 自动更新 | 走 GitHub Releases，程序内下载、SHA256 校验、替换自身、重启 |
| GitHub 令牌 | 选填，用于把查更新的接口额度从 60 次/小时提到 5000 次/小时；DPAPI 加密保存 |
| 运行时检测 | 启动前先查 .NET 10 桌面运行时；缺了就弹「未检测到 .NET 环境」，点「去下载 .NET 10」直接用默认浏览器打开微软官方页，装好点「重新检测」即启动；探测本身失败时**一律放行**，绝不误拦 |
| 界面主题 | 设置窗口支持浅色 / 深色，**默认跟随 Windows 的深浅色实时切换**（1~2 秒内变，不重启），标题栏一起变 |
| 对自己也生效 | 在设置窗口里快速拨动滚轮同样会加速（默认不排除自己）；想对比原生手感可打开「在本窗口暂停增强」 |
| 管理员开机启动 | 设置里勾选后，登录即用管理员权限启动（计划任务实现，**只弹一次 UAC**）；对管理员窗口也生效 |
| 帧率跟随显示器 | 自动按当前屏幕刷新率出帧（60/144/160/165/200/240Hz 都能对上），换屏或改分辨率 2 秒内自动生效 |
| 托盘常驻 | 启用/暂停、打开设置、把当前应用加入/移出名单、检查更新、退出 |
| 开机自启 | 写 HKCU Run 项，不需要管理员 |

参数含义与默认值、以及每个默认值"为什么是这个数"，见 **[DESIGN.md](DESIGN.md)** 与软件内的「帮助」页。

## 运行要求

- Windows 10 1809 或更高（Windows 11 推荐）
- [.NET 10 桌面运行时](https://dotnet.microsoft.com/download/dotnet/10.0)

> 为什么需要运行时：v1.0.0 曾把整个 .NET 打进 exe，结果 66.65 MB ——
> 而这个程序自己的代码只有 210 KB。改成框架依赖后发布包 **0.46 MB**（缩小 99.3%）。
>
> **没装 .NET 10 会怎样**：包里的 `MouseWheelEnhancement.exe` 是个 25 KB 的启动器
> （.NET Framework 4.8 写的，Win10/11 自带），它会先检查运行时；缺了就直接弹一个中文窗口，
> 上面有「下载 .NET 10」按钮，点了用默认浏览器打开微软官方下载页，装好点「重新检测」即可继续。
> 它只在真的缺运行时才会拦你，检测本身出问题时一律放行（不会误拦）。
>
> 目标机器完全不能装运行时的话，用 `tools\publish.ps1 -SelfContained` 另出 67 MB 自包含包。

## 构建

```bash
dotnet build -c Release
```

## 发布（单文件 exe）

一条命令完成「发布 + 装到本机 + 自检 + 算 SHA256」：

```
tools\publish.ps1                  # 框架依赖，两个文件共约 0.46 MB（默认）
tools\publish.ps1 -SelfContained   # 自包含，约 67 MB（目标机器没装 .NET 时用）
```

产物就是**一个 exe**（约 0.47 MB）：

```
MouseWheelEnhancement.exe          双击它就完事
Mouse wheel enhancement data\      你的设置和日志（第一次运行后生成）
```

主程序（.NET 10 WPF）作为**嵌入资源**打在这个 exe 里，运行时自动释放到
`%LOCALAPPDATA%\MouseWheelEnhancement\app\<版本>\` 再启动。用户看到的、
能拷走的、能删的，都只有一个文件。

> **为什么要藏一个主程序在里面**：主程序是框架依赖的 .NET 10 应用，缺运行时时
> .NET 的 apphost 会在加载我们任何一行代码之前就失败 —— 所以"缺环境时弹窗告诉用户去装"
> 必须有一个不依赖 .NET 10 的进程先跑起来（这个 exe 的外壳是 .NET Framework 4.8，
> Win10/11 内置）。把主程序嵌进去，就既能弹窗、又只需要一个文件。

打包产物 `MouseWheelEnhancement-win-x64.zip` 里就是上面这个目录结构，整包解压即可。

> 编译启动器需要 .NET Framework 4.8 参考程序集（装了 Visual Studio 的机器都有）。

默认装到 **`D:\software\myself`**（`-OutDir` 换目录，`-NoInstall` 只发布不覆盖）。
也可以手动：

```bash
# 默认：框架依赖单文件
dotnet publish -c Release -p:PublishSingleFile=true --self-contained false -o out

# 自包含单文件（压缩只对自包含有效）
dotnet publish -c Release -p:PublishSingleFile=true --self-contained true -o out
```

产物是单个 `MouseWheelEnhancement.exe`，拷到任意目录双击即可运行；
数据会在 exe 旁边生成 `Mouse wheel enhancement data` 文件夹。

> 更新器的资产名固定是 **`MouseWheelEnhancement.App.exe`**（主程序本体，约 466 KB）。
> 发版时必须把这个文件按此名上传，**不能**上传启动器整包（`MouseWheelEnhancement.exe`，约 505 KB）——
> 否则更新完"主程序"会变成一个启动器，两个程序互相拉起。见 DESIGN 第 21 节。
> Release 说明里写一行 `SHA256: <64 位十六进制>`，客户端下载后会做校验。

## 自检

```bash
# 272 条纯逻辑断言：倍率曲线、速度判定、起步护栏、摊帧、滑行收尾、帧率跟随、整格化、主题、
# 自我增强、版本比较、令牌清洗、名单匹配、配置读写
MouseWheelEnhancement.App.exe --selftest   # 退出码 0 = 全部通过

# 启动器：检查运行时 / 构造「缺少运行时」窗口（不弹给用户，只验证控件与布局）
MouseWheelEnhancement.exe --runtimecheck
MouseWheelEnhancement.exe --dialogtest

# 端到端冒烟：装钩子、建窗口（七页逐个布局）、注入滚轮跑通 钩子→判定→平滑→注入
MouseWheelEnhancement.App.exe --smoke

# 出帧追踪：真开动画线程、先闲够再推入，量"空闲后第一帧发了多少"
# （不装钩子、不注入，已有实例在跑时也能跑）
MouseWheelEnhancement.App.exe --frametrace

# 自身窗口滚动实测：把光标真的挪到本程序帮助页上滚，量"一格到底滚了多少像素"。
# 用来排查"设置页里一滚动就是一页 / 加速失效"这类只有自己页面犯的问题。
# 可选 lines=N 先把系统「每次滚动几行」设成 N（1/2/3 各跑一次最直观）
MouseWheelEnhancement.App.exe --wheeltest lines=1

# 资源占用实测：仅托盘 / 连设置窗口（并验证关掉后回落、能重新打开）
MouseWheelEnhancement.App.exe --measure
MouseWheelEnhancement.App.exe --measure --show

# 更新链路诊断：检查 + 真的下载校验（--as 假装旧版本，不替换程序）
MouseWheelEnhancement.App.exe --checkupdate
MouseWheelEnhancement.App.exe --checkupdate --as 1.0.0 --download

# 「以管理员身份开机启动」的计划任务状态（只读；--create / --delete 才真的改）
MouseWheelEnhancement.App.exe --taskcheck
```

## 装在哪、数据放哪

软件本体就是一个 exe，放在 `D:\software\myself\MouseWheelEnhancement.exe` 即可
（自动更新也会就地替换这个文件，不需要卸载重装）。

数据在 exe 旁边：

```
<程序目录>\Mouse wheel enhancement data\
    config.json     全部设置（GitHub 令牌以 DPAPI 加密后存在这里）
    logs\app.log    运行日志
```

程序目录不可写时（例如装在 `C:\Program Files` 且非管理员）自动退到
`%APPDATA%\Mouse wheel enhancement data\`，「关于」页会写出实际路径。

## 体积与资源占用

| | v1.0.0 | v1.2.0 |
|---|---:|---:|
| 发布包 | 66.65 MB | **0.46 MB**（启动器 37 KB + 主程序 0.43 MB） |
| 托盘常驻工作集 | 70 MB | 77 MB |
| 打开设置后关闭（退回托盘） | 197 MB 一直占着 | **53 MB**（销毁窗口 + 主动归还） |

体积构成、方案对比、修掉的 10 个 bug / 泄漏项，见 [DESIGN.md 第 12 节](DESIGN.md)。

## 已知限制

- **管理员窗口**：Windows 不允许普通权限程序拦截更高权限窗口的输入。需要覆盖这类窗口时，请以管理员身份运行（「概览」页有一键重启）。
- **全屏独占游戏**：用 Raw Input / DirectInput 独占总线的程序绕过系统鼠标钩子，任何同类工具都拿不到事件。
- **横向滚轮**：v1 不做增强，Shift + 滚轮原样放行。

## 许可证

[MIT](LICENSE) © 2026 QQQOrange
