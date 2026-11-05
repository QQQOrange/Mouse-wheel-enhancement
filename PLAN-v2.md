# 待完成：5 项（v1.9.4 之后）

这份文件是给下一次开发的交接单。每项都写清"改哪里、怎么验"，不再靠回忆。

## 1. 横向滚动摊帧

- 位置：Core/ScrollAnimator.cs + Core/WheelEngine.cs 的 Shift 分支
- 做法：仿纵向加一条平行通道（_pendingHMilli / _carryHMilli / _lastHTicks），
  出口用 InputInjector.SendHWheel；FrameAmount 直接复用
- 验收：SelfTest.Scroll.cs 增加横向整段模拟，断言"总量守恒 / 末尾不突增 / 单帧 ≤ 一格"
- 注意：整格语义的目标（WPF/WinForms/Qt/PPT）不走摊帧，仍发单个 HWHEEL

## 2. 中键拖拽平移

- 位置：Core/WheelEngine.cs HookProc + Core/AppConfig.cs + MainWindow.xaml(开关)
- 做法：WM_MBUTTONDOWN 置 _panActive、记锚点；WM_MOUSEMOVE 累计 dy，
  每 40px 折算一格走 _animator.PushPassthrough；WM_MBUTTONUP 结束
- 默认 **关闭**（ehavior.middleDragPan），开启后吞掉中键按下/抬起
- 验收：自检断言"40px → 1 格、方向正确、未开启时零干预"

## 3. UI 像素基线回归

- 位置：App.Diagnostics.cs + 	ools/ui-baseline/
- 做法：--uibase 生成基线 PNG；--uicheck 渲染后逐像素比较，差异 > 0.3% 失败并输出热区
- 验收：改一个颜色后 --uicheck 必须失败；不改时必须通过

## 4. beta / stable 通道

- 位置：UpdateService.cs + Core/AppConfig.cs + 更新页
- 做法：update.channel（stable/beta）；beta 时取 /releases?per_page=20 里最新一条（含 prerelease）
- 验收：自检覆盖"beta 选中 prerelease / stable 忽略 prerelease"

## 5. 边界回弹（实验，默认关闭）

- 前提：全局钩子**看不到**目标的滚动位置，只能做启发式
- 做法：连续两次同向注入后仍无位移反馈 → 判定到底，反向注入 8px 再回弹
- 标注：实验项，默认关闭；若实测误判率高就删掉，不做半成品