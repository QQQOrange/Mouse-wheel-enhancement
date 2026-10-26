# 鼠标滚轮增强 · Mouse wheel enhancement

> 普通滚动一个字都不改，只在**快速拨动滚轮**时按可调倍率加速；可选平滑推进；可按应用启用/禁用。

Windows 桌面小工具 · 单 exe · 无驱动 · 无后台服务 · 数据就在软件自己的文件夹里

- 中文名：**鼠标滚轮增强**
- 英文名：**Mouse wheel enhancement**
- 开发者：**QQQOrange**
- 项目主页 / GitHub：<https://github.com/QQQOrange/Mouse-wheel-enhancement>
- 克隆：`git clone git@github.com:QQQOrange/Mouse-wheel-enhancement.git`

---

## 它解决什么问题

滚轮在"需要翻很远"的时候太慢。这个工具只在**快速连续拨动**的那一瞬间介入，把这一段滚动的距离按倍率放大；你一放慢，它立刻退场，把控制权原样交回 Windows。

它不改系统滚动行数、不改鼠标设置、不装驱动、不做任何全局 hook 之外的事。

## 三条核心规则

1. **慢速滚动＝完全原生。** 不该接管时直接 `CallNextHookEx` 放行，不做"1.0 倍重发"。手感、精度、惯性与你没装它时逐字节一致。
2. **快滚才加速。** 判定依据是"每秒滚多少格"而不是"第几格"，所以逐格微调不会被误伤，连拨翻页不会误判。
3. **随时可退出链路。** 总开关、名单命中、Ctrl/Alt/Shift 组合、反向纠正，任何一种情况都立刻回到原生行为。

## 功能

| 功能 | 说明 |
|---|---|
| 快速滚动加速 | 触发阈值、满速点、倍率上限（1×–8×）全部可调，默认 10 格/秒 → 3×；所有数值都是整格，没有小数点 |
| 平滑滚动 | 把一次滚动拆成多帧推进（默认 120 fps、每格 4 帧），接近触控板手感 |
| 应用黑白名单 | 按 exe 名匹配，可选窗口标题正则；托盘里一键切换 |
| 自动更新 | 走 GitHub Releases，程序内下载、SHA256 校验、替换自身、重启 |
| GitHub 令牌 | 选填，用于把查更新的接口额度从 60 次/小时提到 5000 次/小时；DPAPI 加密保存 |
| 托盘常驻 | 启用/暂停、打开设置、把当前应用加入/移出名单、检查更新、退出 |
| 开机自启 | 写 HKCU Run 项，不需要管理员 |

参数含义与默认值、以及每个默认值"为什么是这个数"，见 **[DESIGN.md](DESIGN.md)** 与软件内的「帮助」页。

## 运行要求

- Windows 10 1809 或更高（Windows 11 推荐）
- [.NET 10 桌面运行时](https://dotnet.microsoft.com/download/dotnet/10.0)（用自包含发布版则不需要）

## 构建

```bash
dotnet build -c Release
```

## 发布（单文件 exe）

一条命令完成「发布 + 装到本机 + 自检」：

```powershell
tools\publish.ps1
```

默认装到 **`D:\software\myself`**（参数 `-OutDir` 可改，`-NoInstall` 表示只发布不覆盖）。
也可以手动发布：

```bash
dotnet publish -c Release -p:PublishSingleFile=true -r win-x64 --self-contained
```

产物在 `bin/Release/net10.0-windows/win-x64/publish/MouseWheelEnhancement.exe`，自带运行时（约 70 MB），
拷到任意目录双击即可运行；数据会在 exe 旁边生成 `Mouse wheel enhancement data` 文件夹。

> 更新器的资产名固定是 `MouseWheelEnhancement.exe`：发版时请把 exe 以此名上传到 Release。
> Release 说明里写一行 `SHA256: <64 位十六进制>`，客户端下载后会做校验。

## 自检

```bash
# 89 条纯逻辑断言：倍率曲线、速度判定、摊帧、整格化、版本比较、令牌清洗、名单匹配、配置读写
MouseWheelEnhancement.exe --selftest      # 退出码 0 = 全部通过

# 端到端冒烟：装钩子、建窗口（七页逐个布局）、注入滚轮跑通 钩子→判定→平滑→注入
MouseWheelEnhancement.exe --smoke
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

## 已知限制

- **管理员窗口**：Windows 不允许普通权限程序拦截更高权限窗口的输入。需要覆盖这类窗口时，请以管理员身份运行（「概览」页有一键重启）。
- **全屏独占游戏**：用 Raw Input / DirectInput 独占总线的程序绕过系统鼠标钩子，任何同类工具都拿不到事件。
- **横向滚轮**：v1 不做增强，Shift + 滚轮原样放行。

## 许可证

[MIT](LICENSE) © 2026 QQQOrange
