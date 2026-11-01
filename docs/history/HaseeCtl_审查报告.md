# HaseeCtl 1.0.0 全面审查报告

审查对象：F:\vibecode\REA_CON\HaseeCtl（提交 25e5607，约 5761 行 C#），安装目录 %LOCALAPPDATA%\Programs\HaseeCtl。
审查方式：逐文件静态阅读 Core / Agent / App 全部源码，对照 docs/规划书.md；查看今日运行日志；对正在运行的 Agent 做资源采样。
限制：本机只有 .NET SDK 8.0.423，项目目标 net10.0，无法重新编译；测试工程只有一个空的 `Test1()`，没有可运行的测试；没有在真机上逐键按 Fn 组合键做交互测试（需要你本人按键配合）。

## 总体结论

| 维度 | 评价 | 说明 |
|---|---|---|
| 1. 功能能否独立稳定运行 | 基本可用，有 3 个功能性缺陷 | 不依赖官方 Control Center 的 UI 和服务逻辑，但热键依赖官方 CCDCHUService 的日志通道 |
| 2. 代码稳定 / 长时间存活 | 内存健康，并发与边界处理有隐患 | Agent 运行 15 分钟：私有内存 12.6 MB、句柄 337、线程 11、CPU 0.4 s，两次采样无变化 |
| 3. 性能与可维护性 | 中等 | IOCTL 调用冗余；无测试；App 与 Agent 同时直写硬件；部分死代码 |

## 一、功能逐项检查

| 功能 | 状态 | 问题 |
|---|---|---|
| 风扇曲线 / 方案保存 | 可用 | 切换性能模式后不会重新下发曲线（规划书 R1 要求保持）；曲线没有最低转速保护，当前「均衡」方案是 24% 一直到 95°C，完全依赖 EC 自身过热保护 |
| 开机 / 唤醒自动应用 | 可用 | 唤醒事件被处理两次（PowerWindow 的 PBT_APMRESUMEAUTOMATIC 和 SystemEvents.Resume 都调用 OnResume，TrayApp.cs:46-48、404-424），整套设置重复下发 |
| 插拔电源重新应用 | 未实现 | 规划书写了，代码里没有 |
| 定时检查 App(4,5) 防 EC 复位 | 未实现 | 规划书写了，代码里没有 |
| Fn 热键 + OSD | 可用但有风险 | ① OSD 在热键后台线程 "hasee-hotkeys" 上直接创建 WinForms 窗体（TrayApp.cs:206 → Osd.Show），该线程没有消息循环，计时器不触发，OSD 可能卡住不消失；② 热键来源依赖 CCDCHUService 日志，服务停止后监听循环（HotkeyListener.cs:214）不会切换通道，热键静默失效，异常被吞且不写日志；③ Agent 启动时驱动未就绪则热键永远不启动（TrayApp.cs:38） |
| 性能模式切换 | 可用 | Dashboard 的「切换模式」先 CyclePowerMode 再 ApplyPerformance，写两次 EC |
| 充电上限 | **关闭功能无效** | Engine.ApplyCharge（Engine.cs:437）在 Enabled=false 时直接 return，「关闭充电上限」按钮和托盘开关都不会把关闭命令发给 EC，电池仍停在原上限。托盘文字写死 70/80 |
| 显卡切换 | 可用 | 无明显问题 |
| 键盘灯 | 可用 | 亮度滑块每移动一格就写一次 EC 并保存一次配置文件，拖动时会产生几十次写入 |
| 触控板 / 摄像头 / Fn 锁 / 飞行模式 | 可用 | 今日 22:26:42 和 22:26:44 日志出现两次「系统功能调用失败: Object reference not set to an instance of an object.」，SystemService.Try 只记录 ex.Message 不记堆栈，无法定位是哪项功能；托盘菜单里的勾选状态是建菜单时捕获的，可能与实际不符 |
| 更新检查 | 可用 | 每次新建 HttpClient，频率低，影响可忽略 |
| 安装 / 卸载 | 可用 | 当前用户安装、计划任务失败时退到启动文件夹，设计合理；安装目录残留 .pdb |

## 二、稳定性与长时间运行

**内存**：Agent 实测稳定在 12.6 MB 私有内存，没有轮询定时器，静态看也没有持续增长的集合。唯一的缓慢泄漏点是托盘每次点击都新建 ContextMenuStrip 而不释放旧的（用户点击才触发，量很小）。App 窗口每次切页都 new 一个页面，旧页面可被回收；Dashboard 页 Unloaded 时停了计时器，正确。

**今天 Agent 重启了约 10 次**，日志里每次都是正常的「Agent 启动」，没有崩溃记录，推测是开发过程中反复安装导致，不是程序自己崩溃。建议正式使用一两天后再看日志确认。

**并发隐患（中）**：
- Engine 只有 ApplyAll 加了锁；热键线程、托盘菜单、唤醒事件、配置文件监视线程会同时调用 ApplyFan / ApplyPerformance 等，EC 的多步写入可能交错。
- 配置文件监视线程重载时整体替换 Settings 对象，热键线程可能正在修改并保存旧对象，导致改动丢失。
- ScheduleReload（TrayApp.cs:82）在 400 ms 内丢弃后续变化，最后一次修改可能没被 Agent 读到。
- App 和 Agent 两个进程都直接写硬件，规划书里的命名管道没有做。

**数据安全（中）**：SettingsStore.Load（SettingsStore.cs:162-184）读到损坏的 JSON 时直接用默认值覆盖原文件，不留备份，用户保存的风扇方案会全部丢失。App 和 Agent 共用同名的 .tmp 临时文件，同时保存会冲突。

**小问题**：AcpiBridge.FallbackPath 写成 `\?\ACPI#...`，少了一个反斜杠（AcpiBridge.cs:17），主路径能找到设备所以目前不影响；App 第二次启动时静默退出，不会把已有窗口带到前台。

**做得好的地方**：每次调用重新打开驱动句柄，睡眠唤醒后不会拿到失效句柄；硬件调用全部有 try/catch；配置用 tmp+Move 原子写入；App 和 Agent 都有单实例互斥；JSON 用源生成器；SendInput 的 INPUT 结构大小（40 字节）正确；飞行模式 DLL 缺失时有捕获。

## 三、性能与可维护性

**性能**：
- 每读一个字节（ReadAppByte）都要通过 IOCTL 读取整个 4 KiB 存储区；KeyboardLightService.BuildOnValue 一次要读 5 遍，SaveAppInfo 读 3 遍，FanService.SetMode 也有重复读取。可以在一次操作内缓存一次读取结果。
- App 打开时每秒读一次 WMI12 传感器，Dashboard 再每 3 秒读显卡和充电状态；UiState.GpuSupported 每次绑定都访问 EC；UiState.Profiles 每次取值都 new 一个集合。窗口开着时开销不大，但没有必要。
- 日志每行一次 File.AppendAllText，按天分文件但从不清理。量很小，可接受。

**可维护性**：
- 测试：tests 里只有空测试，FanCurve 校验、配置读写、版本号比较这类纯逻辑完全可以写单元测试。
- 死代码：FanService.SetTurboFan / DisableTurboFan 没人调用；FanPage.OnCopyCpuToGpu 的 if/else 两个分支一样；PerformancePage 里 `engine.Power is null ? Supported(Caps!) : Supported(Caps!)` 两边相同，且 Caps 为空时 `Caps!` 会抛空引用；BatteryPage.OnEnableChanged 是空方法。
- 规划书与实现不一致：Agent 未做 NativeAOT、无命名管道、无 App(4,5) 巡检、无插电重应用。文档应该更新，或者补实现。
- 结构清晰：Hw / Features / Config / Apply 分层合理，注释写明了每个命令来自官方哪个函数，后续维护容易追溯。

## 修复优先级建议

1. 充电上限关闭无效：ApplyCharge 在 Enabled=false 时也要下发关闭命令。
2. OSD 线程：OnHotkey 里用 `_ui.BeginInvoke` 切回 UI 线程再显示 OSD（WakeHook 已经这样做了）。
3. 风扇曲线在性能模式切换后重新下发；加插电事件重应用和定时巡检。
4. 热键通道：服务停止时切回备用通道，异常写日志；驱动晚就绪时补启动热键。
5. Engine 所有硬件写入走同一把锁；唤醒事件去重。
6. 配置损坏时先备份为 settings.json.bad 再写默认值；tmp 文件名加进程 ID。
7. SystemService.Try 记录完整异常，查清 22:26 的空引用。
8. 风扇曲线加最低转速保护（例如 85°C 以上不低于 50%）。→ 按你的要求已取消，只保留形状校验，不干预转速。
9. 补单元测试，清理死代码，更新规划书。

---

## 修复记录（2026-11-01）

全部 9 条优先项和正文中列出的次要问题都已修改。构建 0 错误 0 个 C# 警告（只剩 NuGet 漏洞库无法访问的 NU1900），单元测试 33/33 通过，新版已重新打包并安装到 `%LOCALAPPDATA%\Programs\HaseeCtl`，Agent 正在用你原来的配置运行。

| # | 问题 | 修改 |
|---|---|---|
| 1 | 充电上限关闭无效 | `Engine.ApplyCharge` 总是下发 `Ex4(31, enabled, start, stop)`，关闭时发 0；托盘标签读取实际设置，不再写死 70/80 |
| 2 | OSD 在后台线程创建 | `TrayApp.ShowOsd` 统一 `_ui.BeginInvoke` 回到 UI 线程；字体缓存，计时器随窗体释放 |
| 3 | 切性能模式后风扇曲线丢失 | `ApplyPerformance` 默认重新下发风扇；新增插电/拔电重应用；30 秒巡检 `VerifyFan` 比对 App(4,5)，被改动就恢复 |
| 4 | 热键通道 | 每 5 秒重新判断 CCDCHUService，服务停了自动切到驱动通道；异常写完整堆栈并退避重试；驱动晚就绪时巡检会补启动热键 |
| 5 | 并发与重复唤醒 | 新增 `HardwareLock`（进程内锁 + 会话互斥体 `Local\HaseeCtl.Hardware`），App 和 Agent 的硬件操作都走它；唤醒 15 秒内去重 |
| 6 | 配置损坏/tmp 冲突 | 损坏文件先备份为 `settings.json.corrupt-时间.bak`；读不到时保留当前设置而不是覆盖；tmp 名带进程号；`UpdateSettings` 写入前先合并另一个进程的修改 |
| 7 | 异常信息不全 | `SystemService.Try`、Engine 各步骤、App/Agent 未处理异常都用 `Log.Error` 记录调用方和完整堆栈 |
| 8 | 风扇最低转速 | 按你的要求**没有加任何软件限速**：`FanCurve.Normalize` 只保证温度递增、转速不减、范围 0–100%，`Validate` 也不再拦低速曲线；是否有限速完全由 EC/BIOS 硬件决定 |
| 9 | 测试/死代码/文档 | 33 个单元测试（曲线、WMI14 编码、热键解析、配置损坏恢复、Engine 假驱动、并发）；删除 `FanService.SetTurboFan/DisableTurboFan`、重复分支；规划书改为实际架构（WinForms 不支持 NativeAOT；用共享锁+配置合并代替命名管道） |

次要问题：托盘菜单改为单个实例、打开时刷新并释放旧项；`AcpiBridge.FallbackPath` 改为 `\?\`；App 第二次启动会把已有窗口调到前台；配置监听改为 300ms 去抖（不再丢变更）；读 App 页一次 IOCTL（键盘灯、风扇保存）；键盘类型缓存；亮度滑块范围改为 1–4 并在停止拖动 250ms 后才写 EC；`PerformancePage` 未连接时不再空引用；`BatteryPage` 启用开关生效；`SettingsPage` 重载配置后界面同步；日志保留 14 天并带进程号。

**关于风扇限速**：早先版本曾加过「85°C 以上不低于 50%」的软件保护，已按你的要求删除——你有外置散热，软件不再擅自抬转速。24% 的「均衡」曲线现在会原样下发。

**验证**：真机上 Probe 读写正常；App 截图模式打开 7 个页面无报错；新 Agent 运行时私有内存约 13.6–13.8MB、句柄 350–370、CPU 时间几乎不增长，日志显示热键通道、性能模式、风扇方案、触控板都已应用。未能在会话里实际按 Fn 键和睡眠唤醒，这两项需要你手动按一下确认。
