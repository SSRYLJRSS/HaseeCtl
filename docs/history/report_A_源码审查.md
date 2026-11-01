# HaseeCtl 源码深度审查报告（Report A）

- 审查对象：`F:\vibecode\REA_CON\HaseeCtl`（C# .NET 10，HaseeCtl.Core / HaseeCtl.Agent / HaseeCtl.App 三层）
- 目标机型：神舟 HASEE CV15S（蓝天 Clevo / Insyde BIOS，`ACPI\CLV0001`，接口 GUID `{86994c74-ad43-4812-b7e7-0c420b5c5fd7}`）
- 审查目标：卸载官方 Control Center 后 HaseeCtl 能否完全顶上；每个 UI 选项的"读—改—存—应用"是否闭合；无 bug
- 审查方式：全部源码逐行阅读（Core 24 个文件 / Agent 4 个 / App 11 个 + installer 4 个 + docs 2 个 + tests 2 个），用 `ops_grep` 做全局交叉验证，并与 Report B（`report_B_硬件与协议验证.md`）的实机事实相互印证
- 审查日期：2026-11-01
- **本次审查未修改 HaseeCtl 目录下任何文件**，唯一写入为本报告

> 结论先行：**整体架构成熟度高于一般个人项目**（协议层、能力位、双进程锁、配置损坏恢复、单实例、日志轮转都是对的设计，并且踩中了官方 tool 的关键细节，例如 `PublishDate` 式的 `SaveAppInfo` 保留 App(4,4/6/7/8)）。但存在 **5 个 P0 级"用户说出的话和机器实际状态不一致"的缺陷**，其中 2 个（风扇曲线 T1/D1 失效 + 无任何过温兜底）在**当前实机配置下已经有真实的散热隐患**。要达成"每个选项都能正常读取/调整/修改且没有 bug"的目标，P0 必须全修，P1 至少要修掉与卸载场景直接相关的 4 条。

---

## 0. 审查范围与方法

| 层 | 文件 | 行数 | 说明 |
| --- | --- | --- | --- |
| Core | `Apply\Engine.cs` | 411 | 唯一的"配置 → EC"下发口 |
| Core | `Config\SettingsStore.cs` | 257 | 配置读写 + 目录探测 + 日志 |
| Core | `Config\Settings.cs` | 101 | 配置模型与默认值 |
| Core | `Features\HotkeyListener.cs` | 242 | 双通道热键 |
| Core | `Hw\AcpiBridge.cs` | 219 | 4 个 IOCTL 的协议层 |
| Core | `Features\SystemService.cs` | 213 | 触控板/摄像头/飞行模式/键盘锁 |
| Core | `Features\KeyboardLightService.cs` | 128 | RGB 键盘灯 |
| Core | `Features\FanService.cs` | 114 | 风扇（模式/曲线/偏移/自清洁） |
| Core | `Features\FanCurve.cs` | 92 | 曲线模型与 WMI14 编码 |
| Core | `Hw\Dchu.cs` | 88 | 命令封装 |
| Core | `Features\Capabilities.cs` | 77 | 能力位 |
| Core | `Apply\HardwareLock.cs` | 76 | 跨进程硬件锁 |
| Core | `Features\PowerModeService.cs` / `PowerModes.cs` | 65 / 54 | 性能模式 |
| Core | `Hw\ServiceCheck.cs` / `EventLogNative.cs` / `NativeMethods.cs` | 63 / 39 / 47 | SCM、事件日志、P/Invoke |
| Core | `Config\UpdateChecker.cs` / `Features\GpuService.cs` / `DttCheck.cs` / `BatteryInfo.cs` / `ChargeService.cs` / `FanStatus.cs` / `IAcpiBridge.cs` | 58 / 39 / 36 / 35 / 30 / 29 / 36 | 其余 |
| Agent | `TrayApp.cs` / `Osd.cs` / `WakeHook.cs` / `Program.cs` | 565 / 139 / 83 / 35 | 托盘、OSD、热键分发、唤醒钩子 |
| App | `Pages\FanPage.xaml.cs` | 346 | 风扇页 |
| App | `Controls\FanCurveEditor.cs` | 305 | 曲线编辑器绘制与拖拽 |
| App | `Pages\SettingsPage.xaml.cs` | 213 | 设置页 |
| App | `Pages\LightPage.xaml.cs` / `BatteryPage.xaml.cs` / `App.xaml.cs` | 171 / 134 / 124 | |
| App | `Pages\GpuPage.xaml.cs` / `DashboardPage.xaml.cs` / `PerformancePage.xaml.cs` / `UiState.cs` / `MainWindow.xaml.cs` | 89 / 100 / 98 / 83 / 76 | |
| installer | `install.ps1` / `uninstall.ps1` / `build.ps1` / `安装.cmd` / `卸载.cmd` | 106 / 83 / 14 / 5 / 6 | |
| docs | `docs\规划书.md` / `docs\使用说明.md` | 93 / 88 | 承诺 vs 实现核对 |
| tests | `EngineTests.cs` / `FanCurveTests.cs` / `TestSetup.cs` | | 33 个用例 |

判定口径：

- **P0**：功能不可用 / 崩溃 / 数据丢失 / 硬件安全 —— 用户目标"每个选项都能正常读取/调整/修改且没有 bug"直接不成立
- **P1**：特定条件下出错，或界面显示与硬件实际不符（用户会"以为改了其实没改"）
- **P2**：次要缺陷、代码质量、日志噪声、可维护性

---

## 1. 执行摘要

### 1.1 一句话结论

**能顶上，但现在还不能说"没有 bug"。** 协议层、能力位、热键键码表、卸载兼容性这几项做到了官方工具的水平甚至更好（12 组规划书承诺的 Fn 键全部实现，官方 444 MB → 34.6 MB）；但**风扇曲线有一个"看得见改不了"的死角（T1/D1）**、**没有任何过温兜底**、**飞行模式连按无法切回来**、**触控板 Fn 键实际上没在切换**、**多处"配置已保存但 EC 没收到"**——这些都属于"用户以为改了，机器没改"。此外，**配置里那条 40–95 °C 恒 25% 的曲线在程序侧零兜底**（唯一的曲线求值函数 `DutyAt()` 是死代码）。

### 1.2 P0 / P1 清单（一句话一条）

**P0（7 条）**

| # | 缺陷 | 位置 |
| --- | --- | --- |
| P0-1 | 风扇曲线的节点 1（T1/D1）**不能独立生效**：`ToWmi14()` 只写 T2/D2/T3/D3，WMI 14 包体里没有独立表达 T1/D1 的字段；T1/D1 唯一作用是参与算 R12 斜率（`FanCurve.cs:21-23` `R12 => Slope(T1,D1,T2,D2)`），所以它**只改变 T2 之前的升温速率，改不了"起转温度 = 40 °C"这个固件常量**。但 UI 把它写成"起转 40 °C/25%（固件固定）"、配置里又存着用户自己的 `d1=25`（EC 实际 `b[o+5]=80`，差 3.2 倍）——**产品语义承诺了做不到的事** | `Core\Features\FanCurve.cs:71-91`（Put 只写 T2/D2/T3/D3）、`:21-23`（R12 依赖 T1/D1）、`Core\Features\FanService.cs:105-113`（`b[o+4]=40; b[o+5]=80;`）、`App\Pages\FanPage.xaml.cs:61-80`、`:82-88` |
| P0-2 | 全程序没有任何过温兜底：`Normalize` 允许 D1=D2=D3=0，当前实机"均衡"曲线在 40–95 °C 区间恒为 25% 占空比，95 °C 之后才升到 86%，程序不做温度监控、不强制全速、不告警 | `Core\Features\FanCurve.cs:32-53`、`Config\Settings.cs` 默认值、实机 `settings.json` |
| P0-3 | 飞行模式的读状态和写状态是两个不同的世界：`ToggleAirplane()` 只发 `SetWMI(121,26)` + 调 `InsydeAirplanemodehid.dll`，**从不**写 `HKLM\...\RadioManagement\SystemRadioState`，而 `AirplaneOn()` 只读这个注册表键 ⇒ 连按两次永远是"开、开"，切不回来 | `Core\Features\SystemService.cs:75-90` |
| P0-4 | `InsydeAirplanemodehid.dll` 加载失败（DLL 缺失 / 导出签名不符）时异常被 `catch` 吞成一行日志，`ToggleAirplane` 仍 `return target`，OSD 照样弹"飞行模式已开启"——**假成功** | `Core\Features\SystemService.cs:82-90`、`:129-132` |
| P0-5 | Fn+触控板（键码 93 / 252）实际上**不切换触控板**：`TouchpadKey()` 只是"读 Windows 精确触控板的当前状态，再把它原样写回 EC"，没有任何一次 `SendCtrlWinF24()` 的调用 | `Agent\TrayApp.cs:362-372`、对比 `Core\Features\SystemService.cs:26-36` |
| P0-6 | 显卡模式切换不是"先确认硬件接受、再落盘"：`GpuPage.Apply()` 先 `_state.Update(s => s.Gpu.Mode = ...)` 落盘，再 `Gpu?.Switch(m)` 写 EC；EC 写失败时配置已经改了，界面也不会读回校验 | `App\Pages\GpuPage.xaml.cs:63-88` |
| P0-7 | 充电上限的"应用"路径在 `ApplyAll` 里被 `if (Settings.Charge.Enabled)` 短路：用户关闭充电上限后如果 EC 因断电/唤醒/固件重载把限充值恢复了，**开机/唤醒/定时校验都不会再下发"关闭"**，EC 继续把电池锁在 70–80% | `Core\Apply\Engine.cs:157`、`:223-230` |


**P1（17 条，其中 P1-16 已降级为"文档不准确"，实质功能缺陷 16 条）**

| # | 缺陷 | 位置 |
| --- | --- | --- |
| P1-1 | 跨进程硬件锁超时后**照常进临界区**（`WaitOne(5000)` 返回 false 只写一行日志，`Scope` 照返回），App 与 Agent 并发写 EC 的窗口是真实存在的 | `Core\Apply\HardwareLock.cs:36-52` |
| P1-2 | 双进程配置"整文件覆盖"，且 `UpdateSettings` 在 `Save` 失败时仍然保留了内存里的改动却不更新 `_stamp` ⇒ 下一次 `UpdateSettings` 会重新 `Load` 把改动冲掉 | `Core\Config\SettingsStore.cs:180-208`、`Core\Apply\Engine.cs:126-143` |
| P1-3 | `FileStamp` 只用 `(LastWriteTimeUtc.Ticks, Length)` 判定"文件变了吗"：同长度、同一时间戳刻度内的两次保存会被判为"没变"，热键/托盘读到的是旧配置 | `Core\Config\SettingsStore.cs:62-72`、`Agent\TrayApp.cs:162` |
| P1-4 | App 每次切换导航都 `new XXXPage()`，页面内部草稿（`FanPage._draft`、`GpuPage._current`、`BatteryPage._limit`、`LightPage._left/_mid/_right`）**切走即丢**，且用户不会收到任何提示 | `App\MainWindow.xaml.cs:22-37` + 各页 `OnLoaded` 的 `if (_state is not null) return;` |
| P1-5 | 风扇"保存修改 / 另存为"只落盘不下发，但会同步修改 `ActiveProfile` ⇒ 界面"当前方案"已经换成新名字，EC 还在跑旧曲线 | `App\Pages\FanPage.xaml.cs:171-198` |
| P1-6 | 充电上限界面会**凭空改数值**：固件回报的起/停值不在预设列表里时（例如固件给 65%），`SelectValue` 静默选中列表最后一项（95%），用户一点"应用"就把 65% 写成 95% | `App\Pages\BatteryPage.xaml.cs:56-61` |
| P1-7 | 键盘灯页面不判断 `Light.IsRgb` / `Light.Supported`：非 RGB 机型（`KeyboardType` 不为 6/22 且 `caps.Lightbar` 为假）也会显示全套颜色/效果控件，点了既不生效也不报错 | `App\Pages\LightPage.xaml.cs:44-79`、`Core\Apply\Engine.cs:234` |
| P1-8 | 键盘灯"点色块立即写硬件"：`OnPresetClick` 改完本地三元组直接 `ApplyColorOnly()`，没有"应用"这一步，误点就改灯色（且写配置文件） | `App\Pages\LightPage.xaml.cs:101-110`、`:119-127` |
| P1-9 | "全速 3 秒"（Turbo Burst）跨进程锁在 `await Task.Delay(3000)` **之前**就释放了：3 秒内的全速态没有任何保护，期间任何 `ApplyFan` 都可能把它提前打回去，3 秒后的"恢复"也可能覆盖用户在这 3 秒内主动做的模式切换 | `App\Pages\FanPage.xaml.cs:318-343` |
| P1-10 | 性能模式单选框"点即生效"且失败不回滚：`SetPerformanceMode` 抛异常时弹框，但单选按钮仍停在新模式上，界面与 EC 不一致 | `App\Pages\PerformancePage.xaml.cs:64-77` |
| P1-11 | 强冷（`SetTurboFan`）在 DTT 未安装时会**静默把标志写回 0**，不告诉用户"本机不支持强冷/需要 DTT" | `Core\Features\PowerModeService.cs` `SetTurboFan` |
| P1-12 | `OnReload`（设置页"从磁盘重新加载"）只刷新自己这一页；其它已经构造过的页面（若仍在导航历史/缓存里）不会收到通知，用户会看到"重载了但别的页面还是旧值" | `App\Pages\SettingsPage.xaml.cs:185-198` |
| P1-13 | 低刷新率开关只写不读回：`OnLowRefreshChanged` 写完只显示当前 `Display.CurrentHz()`，不会读回 `LowRefreshEnabled` 确认写入成功；且它只在"电池"分支生效，插电时用户看不到任何反馈 | `App\Pages\SettingsPage.xaml.cs:117-131`、`Core\Features\SystemService.cs:116-120` |
| P1-14 | `WakeHook.BeginDisarm()` 依赖 `SynchronizationContext.Current` 非 null；一旦为 null（例如从非 UI 上下文的路径进来），键鼠低级钩子永不卸载，直到进程退出 | `Agent\WakeHook.cs` `BeginDisarm` |
| P1-15 | `App.xaml.cs` 把 `DispatcherUnhandledException` 全部 `e.Handled = true` 后继续运行：一次 UI 异常后界面状态可能已经和硬件脱节，但程序看起来还在正常跑 | `App\App.xaml.cs` 未处理异常钩子 |
| P1-16 | **（已降级：文档描述不准确，不是功能缺陷）** 文档声称"两条通道都已实测注册成功"，但 `DriverProbe` 实测 0 个键码；生产环境里通道 B 根本不会被选用（`CCDCHUService` 属 oem154 驱动包、卸载 Control Center 不会删除、AUTO_START 自启 ⇒ `UseServiceChannel` 恒 true）。**真正的残留风险**：若用户按任何教程手动 `sc delete CCDCHUService`，程序会切到从未收过真实键码的通道 B，**该场景下的热键可用性没有任何验证证据**；且日志里只有一行"切换到驱动通知通道"，用户无从判断 | `Core\Features\HotkeyListener.cs:29-47`、`:54-62`、`:206-233`、`docs\使用说明.md` 通道说明段 |
| P1-17 | `HKClipSvc` 不在 HaseeCtl 的清理清单里：它属于官方包但会自启，卸载 Control Center 后它的 exe 路径失效，每次开机都会写一条 Event 7000 —— 用户会认为"卸载没干净是 HaseeCtl/官方的问题" | 缺少处理（`installer\uninstall.ps1` 只清 HaseeCtl 自己的项）；事实见 `report_B` §卸载残留 |

**P2（24 条）**：见 §11（含日志噪声、死代码、句柄开合、句柄增长口径修正、构建分发链路等）。

### 1.3 对 Lead 四项预判的核实结果

| Lead 的预判 | 核实结论 | 依据 |
| --- | --- | --- |
| "每改一项就 ApplyAll" | **不成立**。全仓库只有两个地方调 `ApplyAll()`：概览页的"重新应用"按钮和设置页的"重新应用"按钮。其余每个 handler 都是"改 → `UpdateSettings` 落盘 → 精确下发那一个功能" | `grep -rn "ApplyAll(" src\` 命中：`Pages\DashboardPage.xaml.cs:71`、`Pages\SettingsPage.xaml.cs:205`、`Agent\TrayApp.cs:262`（菜单）、`Agent\TrayApp.cs:70`（启动）、`TrayApp.cs:434`（唤醒）、`Engine.cs` 内部 |
| "面板未保存即丢" | **成立，而且比预想更彻底**：不是"未保存丢"，是"切页就丢"。`MainWindow.xaml.cs:22-37` 每次导航都 `new` 一个新页面对象 | P1-4 |
| "双进程 Engine 并发" | **成立**，两条独立机理：跨进程锁超时仍继续（P1-1）、整文件覆盖 + stamp 不更新（P1-2） | 见 §4 |
| "日志噪声" | **部分成立**。6 对"已重新加载"是 6 次真实保存（用户 5.3 秒内连改 6 项），不是无限回环；`ReloadSettings` 只赋值不写盘，**不存在自激回环**。真正的噪声源是 `ApplyFan`/`VerifyFan`/`EnableEcTouchpadControl` 每次应用都写日志 | `Engine.cs:117`、`Engine.cs:195`、`Engine.cs:391`、`SystemService.cs:45` |
| "AirplaneOn 读注册表是否与硬件一致" | **完全不一致，而且后果比"显示错"更严重**：连按无法切回（P0-3） | 见 §3.4 |
| "服务被删后是退化还是失效" | **是退化**（退到驱动通知通道），不是失效；且**该分支在生产环境根本不会被走到**（CCDCHUService 不会被卸载删除，见 §5.2）⇒ P1-16 已降级为"文档不准确" | 见 §5.2 |
| **A. Agent 句柄每 30 s +6 的根因**（Lead 补查项） | **不是泄漏，是有界锯齿**。perf-meter 的类型差分证明：每 30 s 只涨 **Thread +6 / Event +12，其余 14 类（含 File）delta = 0** ⇒ 设备句柄根本没堆积。根因是 `RunMaintenance()` 每 30 s 一次**裸 `Task.Run`**（`TrayApp.cs:104`，Task 被丢弃、不观察），线程池按需拉起 worker，每个 worker = 1 Thread 句柄 + 2 EventPair；913→469 的骤降是**线程池 worker 超时退出**（`ThreadTimeoutMs` 默认 15–20 s）释放，**不是 GC**（GC 不回收 OS 句柄）。修法：改用 `System.Threading.Timer` 或 `async + await Task.Delay(30000)` | 见 §4.6 |
| **C. 构建/分发链路** | `Directory.Build.props` **没有** `AnalysisLevel`/`WarningsAsErrors`，`DllImport` 不会让构建失败；风险只存在于**运行时 DLL 缺失**：`InsydeAirplanemodehid.dll` 以 `CopyToOutputDirectory` 不进仓库（`native\README.md` 声明不随仓库分发），源码构建者必然拿不到 ⇒ 命中 P0-4 的静默假成功路径 | 见 §2.3、§6.3 |

---

## 2. 卸载官方软件后会断的路径（重点 a）

### 2.1 结论表

| 能力 | 官方卸载后 | 判定 |
| --- | --- | --- |
| 风扇（模式/曲线/偏移） | 直接走 `0x322400` WMI，与任何官方进程无关 | ✅ 不断 |
| 性能模式 / 强冷 | 同上（`SetWMI(121,25)`、`SetWMI(79)`） | ✅ 不断 |
| 充电上限 | 同上（`Ex4(30/31)`） | ✅ 不断（但有 P0-7） |
| 显卡模式 | 同上（`Ex4(21/22)`） | ✅ 不断（但有 P0-6） |
| 键盘灯 | 同上（`SetWMI(103)`） | ✅ 不断（但有 P1-7/P1-8） |
| 摄像头 / Fn锁 / Win锁 | 同上（`Ex4(25)`、`SetWMI(34)`） | ✅ 不断 |
| Fn 热键（服务存在） | 订阅 `OutLog` 通道，**已实机验证 10/10 全部成功** | ✅ 不断 |
| Fn 热键（服务被手动删除） | 退到"A"驱动等待事件"通道；`RegisterWaitEvent` 三步已验证走通，但**键码接收未验证** | ⚠️ 生产环境不会走到此分支（P1-16 已降级为文档不准确） |
| Fn 热键（键盘过滤器被移除） | 两个通道都收不到，彻底失效 | ❌ 断（但这是用户手动 `HKFltrSetup.bat -u` 才会发生） |
| 飞行模式 | 依赖官方 `InsydeAirplanemodehid.dll`（HaseeCtl 目录里自己带一份） | ⚠️ 自带副本可用，但它是厂商重打包产物，有再分发风险；且 P0-3/P0-4 |
| 触控板 Fn 键 | 不依赖官方进程，但**当前实现本身就没在切换** | ❌ 断（P0-5） |
| 关屏唤醒（Fn+F2 / 键码 215） | 依赖 `WakeHook` 低级键鼠钩子；但 `ArmWakeHook()` 会检查 `OfficialFnKeyRunning()`，官方 FnKey 不在时才会接管 | ✅ 不断（P1-14 有泄漏风险） |
| OLED 关屏键等官方附加功能 | 官方服务实现，HaseeCtl 不实现 | ⚠️ 已知缺失（`docs\使用说明.md` 已声明） |

### 2.2 卸载后会变成"孤儿"但仍在自启的官方组件（必须在文档里讲清楚）

1. **`CCDCHUService`（LocalSystem，AUTO_START）**
   实机证据：exe 落在 `C:\Windows\System32\DriverStore\FileRepository\acpibridge1.inf_amd64_cedafa39846f03cf\DCHUService.exe`，归属 `oem154.inf`（AcpiBridge1 驱动包），**不在 ControlCenter 目录里**，因此卸载 Control Center 它不会被删。
   官方自己的 `uninstallDCHUService.bat` 内容只有一行 `sc detele CCDCHUService` —— **`detele` 是拼写错误**，所以这个服务**从来没有被正确删除过**。
   → 对 HaseeCtl 反而是好事：`App(1,12)=1` 保持为 1，热键继续走已经验证过的 OutLog 通道。

2. **`HKClipSvc`（实测 Running，Auto）**
   exe 位于会被卸载的 `ControlCenter` 目录。卸载后服务还在，但每次开机都会因为找不到 exe 而写一条 **Event 7000**（服务启动失败），用户会看到一个"找不到文件"的怪错误。
   → 当前 `installer\uninstall.ps1` 只清 HaseeCtl 自己的任务/快捷方式/注册表项，**没有处理这个孤儿**（P1-17）。

3. **`HKKbdFltr.sys` / `HKMouFltr.sys`**（键盘/鼠标类 UpperFilters）
   官方 `Driver\HKFltrSetup.bat` 支持 `-u` 卸载它们，但 `clean.ini` **不会自动调用**，所以高概率残留。
   → 这是热键的**唯一输入源**（EC → HKKbdFltr → AcpiBridge）。一旦用户手动跑 `HKFltrSetup.bat -u`，两个热键通道同时归零。
   → 当前 HaseeCtl 的 `docs\使用说明.md` 完全没提这件事。

4. **`AcpiBridge.sys`（oem22.inf, 1.0.0.7）+ `AcpiBridge1.sys`（oem154，DriverStore 残留）**
   HaseeCtl 唯一的硬件依赖，`ACPI\CLV0001` 走 oem22。卸载不删，✅。

### 2.3 `InsydeAirplanemodehid.dll` 的再分发定性（与 Report B 一致）

- `dist\HaseeCtl\InsydeAirplanemodehid.dll`：2,622,976 B，`.text` ≈ 1.79 MB，MFC 静态链接，**导出表只有一个 `SetAirplaneMode`**
- 字符串里能看到编译路径 `C:\Users\NB70TH\Desktop\Insyde\InsydeAirplanemodehid\x64\Release\InsydeAirplanemodehid.pdb`、RTTI `.?AVCInsydeDCHUApp@@`
- 与官方三份同名 DLL 的 SHA-256 **全部不同**，但版本资源包含官方未填充的 `TODO: <公司名稱>`
- 判定：**官方 `InsydeDCHU.dll` 的重打包构建产物**（非逐字节拷贝，也非自写）
- 影响：`native\README.md` 已写"不随仓库分发"，但 `build.ps1` 会把 `native\` 下的文件静默复制进 `dist`；HaseeCtl 的 csproj 用
  `<None Include="../../native/InsydeAirplanemodehid.dll" ... Condition="Exists(...)" />`
  **缺失时不报错、不复制** ⇒ 用户从源码构建时，飞行模式会在运行期静默失效（P0-4 的触发条件）。

**精确的代码路径（Lead 补查项 C）**

```csharp
// Core\Features\SystemService.cs:82-90
public static bool AirplaneOn() { ... }

public bool ToggleAirplane()
{
    bool target = !AirplaneOn();
    Try(() => dchu.SetWmi(121, 26, (uint)(target ? 1 : 0)));
    try { SetAirplaneMode(target ? 1 : 0); }                       // ← DllImport，DLL 缺失时抛 DllNotFoundException / EntryPointNotFoundException
    catch (Exception ex) { Log.Write("InsydeAirplanemodehid 调用失败: " + ex.Message); }   // ← 吞掉
    Log.Write("飞行模式 -> " + (target ? "开" : "关"));              // ← 无论成功失败，日志都写"已切换"
    return target;                                                  // ← 无条件返回目标值
}
```

三条构建/运行事实：

1. **不会编译失败**。`DllImport` 在现代 .NET 里对应分析器诊断 `SYSLIB1054`（"Use LibraryImportAttribute instead of DllImportAttribute..."，见 [Microsoft Learn: SYSLIB1050–1069](https://learn.microsoft.com/en-us/dotnet/fundamentals/syslib-diagnostics/syslib1050-1069)），它**只在 `AnalysisLevel` 显式开启时才提示，既不是默认警告也不是错误**。而 `Directory.Build.props` 只设了 `Version / Product / Authors / RepositoryUrl`，**没有 `<AnalysisLevel>`、也没有 `<WarningsAsErrors>`** ⇒ 缺 DLL 这件事在构建阶段**完全静默**。
2. **运行期才炸，而且炸在错误的地方**。`DllImport("InsydeAirplanemodehid.dll")` 在首次调用时才解析模块（`NativeLibrary.Load`），缺失时抛 `DllNotFoundException: Unable to load DLL 'InsydeAirplanemodehid.dll'`，被 `catch (Exception ex)` 接住后只留下 `Log.Write("InsydeAirplanemodehid 调用失败: ...")` 一行。
3. **界面假成功**。`ToggleAirplane()` 仍 `return target`，于是 `SettingsPage.OnAirplane`（`:135-140`）拿到 `true` 并显示"飞行模式已开启"；热键 133/134 的 OSD 也照样弹出。用户看到的是"成功了"，实际 `SetAirplaneMode` **一次都没被调用过**，硬件没切换，注册表也没变 —— 下次进页面 `AirplaneOn()` 又读到旧值，表现为"飞行模式自己关了"。

**结论**：从源码构建的 HaseeCtl **必然缺这个 DLL**（它不在 Git 里、也没有 NuGet 源），因此"飞行模式静默假成功"不是边缘情况，而是**源码构建者的默认体验**。建议（三选一，推荐第 1 条）：

- 构建时 `Condition="Exists(...)"` 改成 `Error`：`<Error Condition="!Exists('../../native/InsydeAirplanemodehid.dll')" Text="缺少 native\InsydeAirplanemodehid.dll：飞行模式将不可用，请按 native/README.md 自行获取或接受功能降级" />`，让构建者**在构建时就知道**
- 把 `ToggleAirplane` 的 `catch` 改成记录 + `return !target`（对外报告"没成功"），让 UI 至少显示失败
- 运行期首次检测：`ToggleAirplane` 之前先 `NativeLibrary.TryLoad("InsydeAirplanemodehid.dll", out _)`，失败就把该功能在设置页置灰并提示"本机缺少厂商 DLL，飞行模式不可用"

**`build.ps1` 相关（同属 C 项）**

- `dotnet publish ... --self-contained false` ⇒ 目标机必须有 **.NET 10 桌面运行时**；`docs\使用说明.md:7` 有写，但 `installer\install.ps1` **不做任何检测**，缺运行时时双击 exe 只会静默退出（宿主返回 `0x80008096`），用户得不到任何提示（已记 P2-22）。
- App 与 Agent **两次 publish 到同一个 `dist\HaseeCtl` 目录**，靠 `HaseeCtl.Core.dll` 等同名文件互相覆盖 —— 目前两个项目引用同一份 Core，覆盖是无害的，但一旦两边 TargetFramework/RuntimeIdentifier 不一致就会出现"Agent 跑的是 App 编译出来的 Core"。建议改成"同一解决方案一次性 publish 到同一目录"，或给两边加 `<PublishDir>` 校验。

### 2.4 官方卸载后的正确操作顺序（建议写进文档）

1. 卸载前先记下 `settings.json`（HaseeCtl 的配置不依赖官方）
2. 卸载 Control Center（UWP + 主程序）
3. **不要**执行 `HKFltrSetup.bat -u`（会删掉热键输入源）
4. **不要**手动 `sc delete CCDCHUService`（删了以后走未验证的通道 B）
5. 可选：`sc stop HKClipSvc` + `sc delete HKClipSvc`（清掉开机 7000 噪声，HaseeCtl 可在下次安装时自动做，见 P1-17）
6. 跑一次 HaseeCtl 的"重新应用设置"，用 `report_B` 的 `DumpAppStore.ps1` 比对 `appstore_before_uninstall.bin` 的 SHA-256，确认硬件设置零变化

---

## 3. 逐功能"读—改—存—应用"闭环矩阵（重点 c）

图例：**读** = 界面能否读到真实硬件状态；**改** = 交互是否合理；**存** = 是否落盘；**应用** = 是否真正进 EC；**回读** = 写完是否验证；**切页** = 切走再回来是否保留草稿。

### 3.1 风扇页（`App\Pages\FanPage.xaml.cs`）

| 操作 | 读 | 改 | 存 | 应用 | 回读 | 切页 | 判定 |
| --- | --- | --- | --- | --- | --- | --- | --- |
| 拖动节点 2 / 3 | ✅ 从 `_draft` | ✅ | ❌ 需点按钮 | ❌ | ❌ | ❌ 丢草稿 | P1-4 |
| "应用此方案" | — | — | ✅ `UpdateSettings` | ✅ `ApplyFan` | ❌ | — | ✅ 闭环 |
| "应用曲线" | — | — | ✅ | ✅ | ❌ | — | ✅ 闭环 |
| "保存修改" | — | — | ✅ | ❌ **不 ApplyFan** | ❌ | — | **P1-5**（而且它会改 `ActiveProfile`） |
| "另存为…" | — | — | ✅ | ❌ | ❌ | — | **P1-5** |
| "只推送" | — | — | ✅ | ⚠️ 只 `SetWmiPackage(14)`，不 `SetMode(Custom)` | ❌ | — | 语义正确（官方 `Load_FanMode_Info` 行为），但用户若当前是 `auto` 模式会以为"推了就是生效了" |
| "删除方案" | — | — | ✅ | ✅ 仅当 `wasActive && Mode=="custom"` | ❌ | — | ✅ 闭环 |
| 恢复出厂 | ✅ | — | ✅ 只改 `Mode="auto"` | ✅ `RestoreDefaults()` = `SetWMI(121,34,1)` | ❌ | — | ✅ 但"恢复出厂"后 `Profiles` 里的自定义曲线仍留着，文案已说明，可接受 |
| 运行模式 自动/静音/最大 | ✅ `Fan.CurrentMode` | ✅ | ✅ | ✅ `SetFanMode` | ✅ 30 秒后 `VerifyFan` 兜底 | — | ✅ 闭环 |
| 全速 3 秒 | — | — | ❌ 不落盘（设计如此） | ✅ | ❌ | — | **P1-9** 锁提前释放 |
| GPU 抄 CPU | ✅ | ✅ | ❌ | ❌ | — | ❌ | 只改 `_draft`，提示已说明，可接受 |
| **节点 1（T1/D1）** | ⚠️ UI 强制显示**固件值**，不是配置里的值 | ❌ 编辑器锁死 | ⚠️ 配置文件里会存（`CurveSettings.T1/D1`） | ❌ **永不生效** | — | — | **P0-1** |
| 节点 4（T4/D4） | ✅ 硬编码 100/100 | ❌ | — | ✅（`ToWmi14` 固定写 `100,100`） | — | — | ✅ |

**P0-1 细节（这是本次审查最需要修的一条）**

```csharp
// Core\Features\FanCurve.cs:71-91
public byte[] ToWmi14()
{
    var b = new byte[256];
    Put(b, 2, Cpu);  Put(b, 6, Gpu1); Put(b, 10, g2);
    Slopes(b, 14, Cpu); Slopes(b, 20, Gpu1); Slopes(b, 26, g2);
    return b;

    static void Put(byte[] b, int o, FanCurve c)
    {
        b[o] = c.T2;
        b[o + 1] = PercentToDuty(c.D2);
        b[o + 2] = c.T3;
        b[o + 3] = PercentToDuty(c.D3);
    }
}
```

WMI 14 包的每个风扇槽只有 4 个字节（T2/D2/T3/D3），**没有 T1/D1 的位置**。官方 FnKey 也是这个编码，所以这不是协议实现错误，而是**产品语义错误**：

- `Core\Features\FanService.cs:105-113` 的 `SaveAppInfo` 里 `PutCurve` 硬编码了 `b[o+4]=40; b[o+5]=80;`（即起转 = 40 °C / 80%），也就是说 **App 页 4 里存的"起转"永远被写成 40/80，用户设的值根本不会被写进去**
- `Config\Settings.cs:7-12` 的 `CurveSettings` 默认值是 `T1=40, D1=25`，但 `D1` 从未参与任何下发
- `App\Pages\FanPage.xaml.cs:61-80` 的 `LoadCurveIntoEditor()` 里明确写：

```csharp
double t1 = _defaults is null ? c.T1 : (_showGpu ? _defaults.Gpu1.T1 : _defaults.Cpu.T1);
double d1 = _defaults is null ? c.D1 : (_showGpu ? _defaults.Gpu1.D1 : _defaults.Cpu.D1);
```

即**只要固件读到了默认值，编辑器就强制显示固件的 T1/D1，而不是配置里的**；`FanCurveEditor` 还把节点 0 画成"空心的灰点 + LockedHint"不可拖动。

⇒ 用户视角："曲线有四个点，前两个点灰掉了，只有中间两个能拖" + "配置文件里有 t1/d1 但改了没反应"。
⇒ **要么**在 UI 上彻底删掉 T1/D1（连 `CurveSettings.T1/D1` 一起从模型里去掉，避免"存了不用"的误导），**要么**如果官方 App 页 4 的起转字段确实参与 EC 逻辑，就要像 `SaveAppInfo` 那样真正写进去并解释它与 WMI 14 的关系。当前这种"半保留"是最坏的一种状态。

**P0-2 细节（散热安全，见 §8 专章）**

### 3.2 性能模式页（`App\Pages\PerformancePage.xaml.cs`）

| 操作 | 读 | 改 | 存 | 应用 | 回读 | 判定 |
| --- | --- | --- | --- | --- | --- | --- |
| 性能模式单选 | ✅ `Power.Current`（`ReadAppByte(1,1)`） | ⚠️ 点即生效无确认 | ✅ | ✅ `SetPerformanceMode` | ❌ | **P1-10** 失败不回滚 |
| 强冷开关 | ✅ `Power.TurboFanEnabled`（`ReadAppByte(4,8)`） | ✅ | ✅ | ⚠️ DTT 缺失时静默写 0 | ✅ 下次 `OnLoaded` 会读回真实值 | **P1-11** |
| "开机自动应用" | ✅ | ✅ | ✅ 仅 `UpdateSettings` | — | — | ✅ 正确（这是纯配置项） |
| Fn+89 循环 | — | — | ✅ | ✅ `CyclePowerMode` → `SetPerformanceMode` → `ApplyPerformance`（含 `ApplyFan`） | — | ✅ |

附带：`Engine.ApplyPerformance()` 里 `Power.Set(mode)` 之后 `ApplyEnergyStar(mode)`，而 `PowerModeService.Set` **已经**在 `Performance` 分支里构造过 `num` 并 `SetWmi(121,25,num)`；`ApplyEnergyStar` 在 `caps.HardwarePState == true` 时直接 return，所以不会重复写 79（P2 级设计冗余，不影响功能）。

### 3.3 电池充电页（`App\Pages\BatteryPage.xaml.cs`）

| 操作 | 读 | 改 | 存 | 应用 | 回读 | 判定 |
| --- | --- | --- | --- | --- | --- | --- |
| 读起止值 | ⚠️ **有 P1-6**：非预设值被静默改成最后一档 | — | — | — | — | **P1-6** |
| 开关 | ✅ 只改草稿 | ✅ | ✅ | ✅ `SetChargeLimit` → `ApplyCharge`（**关闭也会下发** `Ex4(31,0,start,stop)`） | ✅ `RefreshBattery()` | ✅ 闭环（Lead 原先怀疑的"关不掉"**不成立**，`EngineTests.Disabling_the_charge_limit_is_sent_to_the_ec` 也覆盖了这一点） |
| 起/停下拉 | ✅ | ⚠️ 起 ≥ 停时只"尽力"把停推到下一档，推到最后一档就不管，等点应用才报错 | ✅ | ✅ | ✅ | P2 |
| 70/80 预设 | — | ✅ | ✅ | ✅ | ✅ | ✅ |
| "关闭充电上限"按钮 | — | ✅ | ✅ | ✅ | ✅ | ✅ |
| **开机/唤醒后** | — | — | — | ❌ **`ApplyAll` 在 `Enabled=false` 时跳过 ApplyCharge** | — | **P0-7** |

P0-7 具体代码：

```csharp
// Core\Apply\Engine.cs:149-167
public bool ApplyAll(bool includePerformance = true)
{
    ...
    if (Settings.Charge.Enabled) ok &= Step("充电上限", ApplyCharge);   // ← 关闭态直接跳过
    ...
}
```

`ApplyCharge()` 本身（`:223-230`）是无条件下发的、注释也写了 "including \"disabled\" (status 0)"，但 `ApplyAll` 把它挡住了。触发路径：用户在电池页关掉充电上限（此时 `Ex4(31,0,..)` 已下发）→ 之后 EC 因为断电/BIOS 重载/固件把 App(4) 页复位 → 用户开机或唤醒，`ApplyAll` 不会恢复"关闭"状态 → **电池又被锁在之前的 70–80%**，而界面显示"已禁用（充到 100%）"。

### 3.4 设置与关于页（`App\Pages\SettingsPage.xaml.cs`）

| 操作 | 读 | 改 | 存 | 应用 | 回读 | 判定 |
| --- | --- | --- | --- | --- | --- | --- |
| 唤醒后应用 / OSD / 开机应用 | ✅ | ✅ | ✅ 仅落盘 | — | — | ✅ |
| 触控板开关 | ✅ 读 HKCU 精确触控板 | ✅ | — | ✅ `SetTouchpad`（含 `Ctrl+Win+F24`）| ✅ 回写控件 | ✅ 闭环（注意：`SetTouchpad` 内有最长 1.5 s 的同步轮询，见 P2） |
| 摄像头开关 | ✅ | ✅ | — | ✅ `SetCamera` | ✅ `return sys.CameraOn` | ✅ 闭环（**正确示范**） |
| Fn 锁 / Win 锁 / 互换 | ✅ `ReadKeyboard()` | ✅ | — | ✅ `Ex4(25,...)` + `WriteApp(1,36,...)` | ✅ 回读 | ✅ 闭环（**正确示范**） |
| 低刷新率 | ✅ | ✅ | — | ✅（仅电池态） | ❌ 只显示当前 Hz | **P1-13** |
| **飞行模式** | ⚠️ 读 `RadioManagement\SystemRadioState` | ✅ | — | ✅ `SetWMI(121,26)` + DLL | ❌ 用的是返回值 | **P0-3 / P0-4** |
| 关闭屏幕 | — | ✅ | — | ✅ `PostMessage(WM_SYSCOMMAND, SC_MONITORPOWER, 2)` | — | ✅ |
| 检查更新 | — | — | — | ⚠️ 仓库地址尚不存在 | — | P2 |
| 重载配置 | — | — | — | ⚠️ 只刷新本页 | — | **P1-12** |
| 重新应用 | — | — | — | ✅ `ApplyAll` | — | ✅（但受 P0-7、P1-1 影响） |

**P0-3 / P0-4 细节**

```csharp
// Core\Features\SystemService.cs:75-90
private const string RadioKey = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\RadioManagement\SystemRadioState";

public static bool AirplaneOn()
{
    try { return Registry.GetValue(RadioKey, "", 0) is int v && v != 0; }
    catch { return false; }
}

public bool ToggleAirplane()
{
    bool target = !AirplaneOn();
    Try(() => dchu.SetWmi(121, 26, (uint)(target ? 1 : 0)));
    try { SetAirplaneMode(target ? 1 : 0); }
    catch (Exception ex) { Log.Write("InsydeAirplanemodehid 调用失败: " + ex.Message); }
    Log.Write("飞行模式 -> " + (target ? "开" : "关"));
    return target;          // ← 无论 DLL 是否成功，都把这个"打算"当成"结果"返回
}
```

三个独立问题叠在一起：

1. **写路径与读路径不是同一个东西**。`SetWMI(121,26)` 和厂商 DLL 都会让 EC/网络硬件进入飞行模式，但 `RadioManagement\SystemRadioState` 这个键只有 Windows 自己的无线电管理 UI 才会写。HaseeCtl **从不写它**。
2. 因此 `AirplaneOn()` 永远返回旧值 ⇒ `target = !AirplaneOn()` 每次算出同一个方向 ⇒ **连按两次 = 连发两次"开"**。用户在设置页看到的状态文字（`OnAirplane` 用的是返回值）会说"已开启/已关闭"，但那是它自己算的，不是回读。
3. DLL 抛异常（DllNotFoundException / EntryPointNotFoundException / BadImageFormatException）被 `catch` 成一行 `Log.Write("InsydeAirplanemodehid 调用失败: ...")`，**函数照样 `return target`**，OSD 弹"飞行模式已开启"（`TrayApp.cs:353-359`）。而 EC 那条 `SetWmi(121,26,1)` 走的是 `Try(...)`，异常同样被吞。

⇒ 用户会遇到的三种表现：a) 连按无法切回；b) 从 BIOS 热键或 Windows 托盘切换了飞行模式，HaseeCtl 界面不知道；c) `InsydeAirplanemodehid.dll` 缺失时 UI 报成功、实际大部分无效。

**修法建议**：`ToggleAirplane` 改成"写 → 回读同一个源确认 → 不一致就抛异常"，并且把"读"改成读**自己能验证的那个源**（例如 EC 的 `GetWMI(121,26)` 回读，或 App 页里的飞行模式位；`SystemRadioState` 只能用来跟 Windows 同步显示，不能当真相来源）。DLL 失败必须向上抛，不要 `return target`。

### 3.5 键盘灯页（`App\Pages\LightPage.xaml.cs`）

| 操作 | 读 | 改 | 存 | 应用 | 回读 | 判定 |
| --- | --- | --- | --- | --- | --- | --- |
| 开关 | ✅ `ReadAppByte(2,80)` | ✅ | ✅ | ✅ `Send((0xE0<<24)|val)` | ❌ | ✅ |
| 亮度滑块 | ✅ `ReadAppByte(2,35)` | ✅ 250 ms 去抖 | ✅ | ✅ `Send(0xF4000000 + 47*v)` | ❌ | ✅ 设计良好 |
| 色块 | ⚠️ 初次读 85/89/93 | ⚠️ **点即写** | ✅ | ✅ | ❌ | **P1-8** |
| 灯效 | ✅ `ReadAppByte(2,32)` | ✅ | ✅ | ✅ | ❌ | ✅（但 `CustomZones` 时会连写两次，见 P2） |
| 三区/全键盘单选框 | — | ✅ | — | — | — | ✅ |
| **非 RGB 机型** | ❌ 不判断 | ❌ 全显示 | ❌ 会写 | ❌ 无效 | ❌ | **P1-7** |

P1-7 证据链：
- `Core\Features\KeyboardLightService.cs:31-32`：`IsRgb => KeyboardType is 6 or 22`、`Supported => caps.Lightbar || IsRgb`
- `Core\Apply\Engine.cs:234`：`ApplyLight()` 里有 `if (Light is null || !Light.IsRgb) return;`，说明作者知道非 RGB 要保护
- **但 `LightPage.OnLoaded`（`App\Pages\LightPage.xaml.cs:44-79`）完全没有这层判断**，只在 `light is null` 时回落到配置值；非 RGB 机型会因为 `light is not null` 而尝试 `light.Enabled/Left/...`，那些 `ReadAppByte` 读到的是 App 页 2 里无意义的内容，页面就会显示一堆假颜色，用户一点就写进 EC 页 2。

P1-8：`OnPresetClick`（`:101-110`）→ `ApplyColorOnly()`（`:119-127`）→ `Engine.SetLightColors()` → `UpdateSettings` + `Light.ApplyZones`。**没有确认步骤**，误点 17 个色块中的任意一个就立即改灯并写盘。旁边明明有一个 `OnApplyColors`（"应用颜色"按钮，`:128`），两者行为完全一样。

### 3.6 显卡页（`App\Pages\GpuPage.xaml.cs`）

| 操作 | 读 | 改 | 存 | 应用 | 回读 | 判定 |
| --- | --- | --- | --- | --- | --- | --- |
| 读当前模式 | ✅ `Ex4(21)` | — | — | — | — | ✅ |
| 只显示 BIOS 支持的 | ✅ `_current.Supported` | — | — | — | — | ✅ |
| 切换（不重启） | — | ✅ 有确认框 | ✅ **先落盘** | ✅ `Ex4(22,mode)` | ❌ | **P0-6** |
| 切换并重启 | — | ✅ | ✅ | ✅ | ❌ | **P0-6** |

```csharp
// App\Pages\GpuPage.xaml.cs:78-84
var m = mode.Value;
_state.Update(s => s.Gpu.Mode = (int)m);                       // ① 先写配置
_state.Engine.WithHardware(() => _state.Engine.Gpu?.Switch(m)); // ② 再写硬件
HintText.Text = "已写入切换标志，重启后生效。";
```

两步不在同一个事务里（也不在同一个 `_hw.Enter()` 里）：② 抛异常时 ① 已经落盘，配置说"要切到独显"，硬件没接受；下次 `OnLoaded` 因为 `_current` 还是旧值又会显示旧模式，用户永远不知道哪次生效了。修法：把两步放进同一个 `WithHardware(() => { Gpu.Switch(m); return Gpu.Read(); })`，**先写硬件、读回确认、再 `Update` 落盘**，失败则完全不动配置。

### 3.7 概览页（`App\Pages\DashboardPage.xaml.cs`）

| 操作 | 判定 |
| --- | --- |
| 4 个实时值（3 秒轮询） | ⚠️ 每次 `RefreshTexts` 在 `WithHardware` 内做 **4 次 EC 往返**（`Power.Current`、`Gpu.Read()`、`Charge.Read()`、`Fan.CurrentMode`）+ `UiState` 每秒 1 次 `ReadStatus` ⇒ 常驻 CPU 4.3–4.8%（P2，见 §7） |
| 全速 / 恢复自动 | ✅ |
| 重新应用 | ✅ |
| 切换性能模式 | ✅（`CyclePowerMode` 内部 `SetPerformanceMode` → `ApplyPerformance` → `ApplyFan`，注释也说明了） |

### 3.8 托盘菜单（`Agent\TrayApp.cs`）

| 项 | 判定 |
| --- | --- |
| 风扇模式 5 项 | ✅ |
| 风扇配置 N 项 | ✅ |
| 性能模式 N 项 | ✅ |
| 充电上限（复选） | ✅ |
| 触控板 | ✅（走 `SetTouchpad`，真正切换） |
| 摄像头 | ✅ |
| 关闭屏幕 | ✅ |
| 重新应用设置 | ✅ |
| 退出 | ✅ 清理完整（Timer/Watcher/Hotkey/WakeHook/NotifyIcon/菜单/PowerWindow） |

---

## 4. App 与 Agent 双进程状态同步（重点 d）

### 4.1 现有同步机制盘点

| 机制 | 位置 | 有效性 |
| --- | --- | --- |
| `FileSystemWatcher` + 300 ms 去抖 → `ReloadFromDisk` | `TrayApp.cs:127-153` | ✅ 结构正确 |
| `FileStamp(Ticks, Length)` 变化检测 | `SettingsStore.cs:62-72` | ⚠️ 不可靠（P1-3） |
| 进程内 `lock(SaveSync)` + 每进程 tmp 名 | `SettingsStore.cs:168`、`:184` | ✅ 永不半截文件（`File.Move` 是原子替换） |
| 跨进程命名 Mutex `Local\HaseeCtl.Hardware` | `HardwareLock.cs:15` | ❌ 超时后失效（P1-1） |
| `UpdateSettings` 的 read-modify-write | `Engine.cs:126-143` | ⚠️ Save 失败路径有洞（P1-2） |
| 定时 `VerifyFan` 30 s | `TrayApp.cs:116` | ⚠️ 只校验风扇模式，不校验其它 7 类设置 |

### 4.2 P1-1：跨进程锁超时后仍然继续

```csharp
// Core\Apply\HardwareLock.cs:36-52
public Scope Enter()
{
    Monitor.Enter(_sync);
    bool owned = false;
    if (_mutex is not null)
    {
        try
        {
            owned = _mutex.WaitOne(WaitMs);          // WaitMs = 5000
            if (!owned) Log.Write("等待跨进程硬件锁超时，继续执行");
        }
        catch (AbandonedMutexException) { owned = true; }
        catch (Exception ex) { Log.Write("获取跨进程硬件锁失败: " + ex.Message); }
    }
    return new Scope(this, owned);                    // ← 无论 owned 与否，都进临界区
}
```

`Scope.Dispose()` 里 `if (_mutexOwned) ReleaseMutex()` —— 只放进程内的 `Monitor`。所以只要 App 有一次 `ReadStatus` 之类的操作把 Mutex 卡住超过 5 s（`Dchu.ReadApp` 每次读 1 字节要读整 4 KiB + 一次 IOCTL，密集读的时候很容易），Agent 的 `ApplyFan` 就会**在没有跨进程互斥的情况下**写 EC。

具体的危险序列（`FanService.ApplyTable` 是三步非原子）：
1. Agent：`SetWmiPackage(14, table)` → `SetMode(Custom)` = `SetWmi(121,1,6)` + `WriteAppByte(4,5,6)` → `SaveAppInfo` = 读整页 4 → 改 → 写整页 4
2. App 同时执行 `SetChargeLimit` / `SetLightColors`（都是 `UpdateSettings` + 精确写）
3. 交错点：Agent 的 `SaveAppInfo` 读到的是**写之前的**页 4，写完就把 App 刚写进去的字节覆盖掉；`WriteAppByte(4,5,6)` 与 `SaveAppInfo` 里的 `b[5]=Custom` 又可能与 `PowerModeService.Set` 的 `WriteAppByte(1,1,..)` 交织

⇒ 结果就是"某项设置随机回退"，而日志里只有一行"等待跨进程硬件锁超时，继续执行"，很容易被当成噪声忽略。

**修法建议**（三选一，按推荐度）：
1. 超时后**抛异常**，让调用方失败重试（最正确，但需要每个调用点处理）
2. 超时后进入"降级模式"：只允许读操作，写操作直接抛
3. 提高 `WaitMs` 并把 EC 访问改为批量化（见 P2 的 `ReadApp` 问题），从根上减少持锁时长

### 4.3 P1-2：双进程整文件覆盖 + stamp 不更新

```csharp
// Core\Config\SettingsStore.cs:180-208
private static FileStamp? TrySaveCore(AppSettings settings)
{
    lock (SaveSync)                                  // ← 进程内锁
    {
        var tmp = FilePath + "." + Environment.ProcessId + ".tmp";
        ...
        File.WriteAllText(tmp, JsonSerializer.Serialize(settings, ...));
        File.Move(tmp, FilePath, overwrite: true);   // ← 整文件替换
        ...
    }
}
```

```csharp
// Core\Apply\Engine.cs:126-143
public void UpdateSettings(Action<AppSettings> mutate)
{
    lock (_settingsSync)
    {
        if (SettingsStore.Stamp() != _stamp) { var r = SettingsStore.Load(); ... }  // 尽力追平
        mutate(_settings);
        SettingsStore.Normalize(_settings);
        if (SettingsStore.Save(_settings) is { } stamp) _stamp = stamp;             // ← 失败时 _stamp 不变
    }
}
```

两个洞：

- **窗 1（跨进程覆盖）**：`Stamp() != _stamp` 的检查与 `Save` 之间没有跨进程互斥。A 进程在"检查完、还没 Save"的窗口内，B 进程完成了一次 Save，A 随后用自己内存里的旧快照整文件覆盖 ⇒ B 的改动消失。窗口只有几十微秒级，但托盘菜单（Agent）和界面（App）同时操作时触发概率不低；`EngineTests.UpdateSettings_keeps_changes_written_by_the_other_process` 只覆盖了"两次调用之间有 `Thread.Sleep(20)`"这种**不重叠**的情形，**没有测并发**。
- **窗 2（保存失败）**：`SettingsStore.Save` 返回 null（磁盘满、权限、被杀毒锁）时，`_settings` 里的改动**已经生效在内存**，而 `_stamp` 保持旧值 ⇒ 下一次 `UpdateSettings` 发现 `Stamp() != _stamp` 会重新 `Load`，**用磁盘内容把内存改动冲掉**。用户看到的是"刚改的设置过一会儿自己变回去了"。

**修法建议**：`Save` 失败时要么回滚内存（`_settings` 恢复为改前快照），要么记一个 `_dirty` 标志并在下次保存时用"磁盘内容 + 我的 diff"合并。真正的根治是把 upsert 改成"先加跨进程文件锁（`FileShare.None` 打开的 lock 文件）→ Load → mutate → Save"。

### 4.4 P1-3：`FileStamp` 判定"文件没变"

```csharp
public readonly record struct FileStamp(long Ticks, long Length);

public static FileStamp Stamp()
{
    var fi = new FileInfo(FilePath);
    return fi.Exists ? new FileStamp(fi.LastWriteTimeUtc.Ticks, fi.Length) : default;
}
```

`File.Move(tmp, dest, overwrite:true)` 在 NTFS 上是替换目录项，dest 的 `LastWriteTime` 来自 tmp 文件。两次保存如果在同一个 100 ns 刻度内完成且内容长度相同，`(Ticks, Length)` 完全相同 ⇒ `TrayApp.ReloadFromDisk` 判定"没变化"，直接 `return`（`TrayApp.cs:162`），**托盘菜单继续显示旧值**。间歇且极难复现——正是那种"偶发，用户以为是玄学"的 bug。

**修法建议**：`FileStamp` 加内容哈希（例如 `System.IO.Hashing.XxHash64` 或简单的 FNV-1a over 前 64 字节 + 长度），或者干脆改成"每次 Save 后把内容哈希写进文件本身的一个字段"。

### 4.5 界面显示与实际不符的汇总（重点 d 的结论）

| 显示 | 实际 | 根因 |
| --- | --- | --- |
| 风扇曲线"起转 40 °C / 25%（固件固定）" | 配置里存的 D1 可能是 25，EC 里是 80 | P0-1 |
| 风扇页"当前方案：X" | `OnSaveProfile`/`OnSaveAs` 只改了 `ActiveProfile`，EC 还在跑旧曲线 | P1-5 |
| 充电上限"起 95% / 停 100%" | 固件里是 65% | P1-6 |
| 飞行模式"已开启/已关闭" | 只是它自己算出来的期望值 | P0-3/P0-4 |
| 设置页"飞行模式开/关" | 读的是另一个源（注册表），与上面那个显示也可能互相矛盾 | P0-3 |
| 显卡模式"已写入切换标志" | EC 可能没接受，配置却已经改了 | P0-6 |
| 低刷新率勾选状态 | 写没写进去不知道（不读回）；且插电时它本来就不生效 | P1-13 |
| 键盘灯颜色/灯效（非 RGB 机型） | EC 里没有对应的东西 | P1-7 |
| 性能模式单选 | 切换失败但按钮还停在新模式 | P1-10 |
| 强冷开关 | DTT 未安装时被静默写回 0 | P1-11 |
| 风扇方案编辑中的草稿 | 一切页就没了 | P1-4 |

### 4.6 Agent 句柄增长的真实根因（Lead 补查项 A —— 本节已按实测证据重写）

> **修订说明**：本节初版把根因判为"`HotkeyListener.UseServiceChannel` 每 5 秒开 3 个句柄"，并称"913→469 骤降是 GC 回收"。**该结论已被 perf-meter 的句柄类型差分实测证伪**（详见下表），初版判断错误，现按实测重写。

#### 4.6.1 实测证据（perf-meter 按 ObjectTypeIndex 的句柄类型差分）

| 观测 | 数值 |
| --- | --- |
| 每 30 s 句柄净增 | **+6**（Thread 类 **+6**、Event 对象 **+12**） |
| **其余 14 类 TypeIndex 增量** | **全部 delta = 0**（含 **File**，即驱动设备句柄） |
| Lead 独立实测 | 09:58:37 579 → 10:01:38 615（36 个 / 180 s = **+6/30 s**） |
| 线程数同步摆动 | 10:05:02→10:09:47 采 20 次：句柄 649→709，**线程 12↔15 往复** |
| 一次性大回落 | 913 → 469 |

**这一条直接否掉初版结论**：`CreateFileW` 开出的设备句柄在 `NtQuerySystemInformation` 里属于 **File** 类型。如果真的是"每 5 秒反复开设备句柄"，File 类型的 delta 绝不可能是 0。**增量为零 = 设备句柄根本没有堆积。**

#### 4.6.2 真实根因：`RunMaintenance` 的裸 `Task.Run`

```csharp
// Agent\TrayApp.cs:65-67   —— 维护定时器本身是 WinForms Timer（UI 线程，不产生线程池线程）
_maintenance = new System.Windows.Forms.Timer { Interval = (int)MaintenanceInterval.TotalMilliseconds };
_maintenance.Tick += (_, _) => RunMaintenance();
_maintenance.Start();

// Agent\TrayApp.cs:101-121
private void RunMaintenance()
{
    if (Interlocked.Exchange(ref _maintenanceBusy, 1) == 1) return;   // :103 只防并发
    Task.Run(() =>                                                    // :104 裸 Task.Run，不保存 Task、不 await、不观察
    {
        try
        {
            if (!_engine.Ready)
            {
                if (!_engine.Connect()) return;
                Log.Write("驱动已可用，启动热键并应用设置");
                _ui.BeginInvoke(TryStartHotkeys);                     // :112
                _engine.ApplyAll();                                   // :113
                return;                                               // ← 走这条时 try 结束，仍会进 finally
            }
            _engine.VerifyFan();                                      // :116
        }
        catch (Exception ex) { Log.Error("定时校验失败", ex); }
        finally { Volatile.Write(ref _maintenanceBusy, 0); }          // :119
    });
}
```

`Task.Run` 返回的 `Task` 被**直接丢弃**（没有赋值、没有 `await`、没有 `ContinueWith`、没进 `TaskScheduler.UnobservedTaskException` 之外的任何观察路径），所以：

1. 线程池在每次 `Task.Run` 时**按需唤醒一个 worker**。线程池维护"最小线程数"（本机 `MinThreads` 默认 = CPU 核数），持续 30 s 一次的唤醒会让 worker 数量在 **12↔15** 之间摆动（与实测吻合）。
2. 每个池线程对象持有 **1 个 Thread 句柄**；线程池给每个 worker 配的**唤醒事件（EventPair）是 2 个 Event 对象**。所以"新增一个 worker = +1 Thread + 2 Event"。实测 **+6 Thread + 12 Event** 正好是 **6 个 worker 被拉起**，比例 1:2 完全对得上。
3. 线程池有 `ThreadTimeoutMs`（默认空闲约 15–20 秒）的**饥饿回收**机制：worker 空闲超过阈值就退出，退出时 Thread 句柄与 EventPair **一并释放**。这就是 **913 → 469** 那次一次性大批释放的真因——**不是 GC**。GC 从不回收 OS 句柄；`SafeFileHandle` 之类才有 finalizer，而 Thread/Event 句柄由线程池自己管，跟 GC 无关。初版把它写成"GC 回收"是错的。

#### 4.6.3 结论

- 性质：**不是泄漏，是有界的锯齿**（约 +720 句柄/小时，但峰值到 ~913 后随 worker 回收回落；线程数稳定在 12~15 的窄带内往复）
- 初版已排除的部分**保留且仍然成立**：`AcpiBridge`（`:120-138/140-159/173-190` 全部 `using var h = Open()`）、`ServiceCheck`（`finally` 里 `CloseServiceHandle`）、`HardwareLock`（`SessionLock` 是进程内单例，Mutex 每进程只 1 个）、`Osd`（`Dispose(bool)` 里释放 `_timer`/`_fade`）、`Program`（`using var mutex`）—— **这五处都没有句柄泄漏**
- `HotkeyListener` 的 5 秒复检确实每轮开 1 设备 + 2 SCM 句柄（初版分析本身没错），但**它作用在 File/SCM 类型上，实测 delta = 0**，所以**不是句柄增长的来源**；它只是一个效率问题（见下）

#### 4.6.4 两条独立建议

| # | 建议 | 性质 |
| --- | --- | --- |
| 1 | `RunMaintenance` 改用 **`System.Threading.Timer`**（回调天然在线程池上，池会复用同一个 worker，不再每次拉新线程）；或改成 **`async` 循环 + `await Task.Delay(30000)`**。**这是消除 +720 句柄/小时的直接修法** | 建议采纳 |
| 2 | `HotkeyListener.UseServiceChannel` 复检间隔 5 s → 30 s；`Dchu.ReadAppByte` 不要为 1 字节读整 4 KiB（`Dchu.cs:65-71`），并复用设备句柄 | 效率优化（与句柄增长无关） |

---

## 5. HotkeyListener 双通道切换时序（重点 b）

### 5.1 设计（原样引用）

```csharp
// Core\Features\HotkeyListener.cs:54-62
public bool UseServiceChannel
{
    get
    {
        if (Environment.GetEnvironmentVariable("HASEECTL_FORCE_DRIVER_HOTKEYS") == "1") return false;
        try { return ServiceOwnsEvents && ServiceCheck.IsRunning("CCDCHUService"); }
        catch { return false; }
    }
}
```

```csharp
// :84-115
private void Run(CancellationToken token)
{
    int failures = 0;
    while (!token.IsCancellationRequested)
    {
        try
        {
            bool service = UseServiceChannel;
            if (service != _lastSource) { _lastSource = service; Config.Log.Write(service ? "热键来源: 服务日志 OutLog (CCDCHUService)" : "热键来源: ACPI 驱动通知 (驱动等待事件)"); }
            // both loops return when the channel choice changes, so we switch over automatically
            if (service) FollowEventLog(token);
            else WaitDriverEvent(token);
            failures = 0;
        }
        catch (Exception ex) { ... _lastSource = null; token.WaitHandle.WaitOne(TimeSpan.FromSeconds(Math.Min(failures, 5))); }
    }
}
```

`FollowEventLog`（`:117-144`）与 `WaitDriverEvent`（`:206-233`）内部都是 500 ms 一轮 `WaitForSingleObject`，每 5 s 复检一次 `UseServiceChannel`，方向相反就 `return` 回主循环。

### 5.2 切换到驱动通道后**不会失效**，是退化

`return` 回到 `Run` 的 `while` 顶部，重新读 `UseServiceChannel`（此时服务已停，返回 false），进 `WaitDriverEvent`。所以：

| 场景 | 行为 | 证据 |
| --- | --- | --- |
| 现状（服务在跑） | OutLog 通道 | 实机日志 10/10 |
| 卸载 Control Center | 服务仍自启（`sc detele` 拼写错误 + exe 在 DriverStore）⇒ **仍是 OutLog 通道** | `report_B` |
| 手动 `sc delete CCDCHUService` | 5 s 内切到驱动通道 | 代码路径确定 |
| 手动 `HKFltrSetup.bat -u` | 两个通道都收不到 —— **彻底失效** | HKKbdFltr 是唯一输入源 |

**定量论证（Lead 补查项 B：通道 B 到底会不会被用到）**

```csharp
// Core\Features\HotkeyListener.cs:54-62
private bool UseServiceChannel
{
    get
    {
        if (Environment.GetEnvironmentVariable("HASEECTL_FORCE_DRIVER_HOTKEYS") == "1") return false;
        try { return ServiceOwnsEvents && ServiceCheck.IsRunning("CCDCHUService"); }
        catch { return false; }
    }
}

// Core\Features\HotkeyListener.cs:19-26
private bool ServiceOwnsEvents => _dchu.ReadAppByte(1, 12) == 1;
```

在"正常卸载官方 Control Center"这条路线上：

```
ServiceOwnsEvents      = _dchu.ReadAppByte(1,12) == 1  → 恒 true（AcpiBridge 驱动在，App 页 1 的字节 12 恒为 1）
ServiceCheck.IsRunning = CCDCHUService 状态查询         → 恒 true（属 oem154 驱动包，LocalSystem，AUTO_START，卸载不删）
⇒ UseServiceChannel    = (FORCE_DRIVER != "1") && true && true = 恒 true
```

⇒ **通道 B（`WaitDriverEvent`）在生产环境永远不会被选中。** 它只在这三种人工干预下才会走：

1. 设了环境变量 `HASEECTL_FORCE_DRIVER_HOTKEYS=1`（只有开发者会这么做）
2. 用户手动 `sc delete CCDCHUService` / `sc config CCDCHUService start= disabled`
3. `ReadAppByte(1,12)` 因故返回非 1（驱动版本不匹配等）

⇒ **P1-16 因此降级为"文档描述不准确"**：`docs\使用说明.md` 写的"两条通道都已实测注册成功"与 `DriverProbe` 的 0 键码事实不符，属于文档措辞问题，**不是功能缺陷**。

**但降级后仍有残余风险，必须在报告里保留**：如果用户按网上教程手动 `sc delete CCDCHUService`（这是很多人"把官方残留清干净"的常规操作），程序会在 5 秒内自动切到通道 B，而**通道 B 从未收到过任何真实键码**——这条路径的可用性没有任何证据。一旦不通，用户看到的就是"热键全部失效"，而日志里只有一行"热键来源: ACPI 驱动通知 (驱动等待事件)"。建议（见 §13）：

- 切到通道 B 时打 WARN 级日志 + 托盘气泡提示一次"当前使用驱动通知通道，如热键无效请勿删除 CCDCHUService"
- 在 `docs\使用说明.md` 里明确写"不要卸载/删除 CCDCHUService"（它是驱动包的一部分，不是 Control Center 的）

**驱动通道的实际验证程度**（`report_B` 第 12 章）：

- `HotkeyListener.DriverProbe`（`:29-47`）在真机上**成功走完了** `bridge.OpenEventHandle()` → `CreateEventW` → `bridge.RegisterWaitEvent(handle, hsa)`，说明设备能开、`IOCTL 0x322404` 能注册
- 但**按 Fn 组合键时收到 0 个键码**。原因不是 bug：卸载前 `App(1,12) = 1`，驱动处于"服务独占事件"模式，EC 把键码路由给 CCDCHUService → OutLog，不会走驱动通知通道
- 因此"驱动通道能不能真正拿到键码"**至今没有证据**

### 5.3 时序上的真实脆弱点

1. **切换窗口内的键码丢失（P2 级，可接受）**
   `FollowEventLog` 在 `EvtSubscribe` 之后只收 future events；从 `return` 到 `WaitDriverEvent` 再 `RegisterWaitEvent` 成功之间有一个几十毫秒的空窗，期间按下的 Fn 键会丢。5 s 一次复检、每次切换都是有意的，概率极低。

2. **两个通道可能同时活跃（P2 级）**
   `FollowEventLog` 里 5 s 复检发现要切走，是在 `finally` 里 `EvtClose(subscription)` 之后才返回；但 `WaitDriverEvent` 里如果发现要切回服务通道，是直接 `return`，`finally` 里只关 `hsa`。看起来对称，但存在一个方向：服务在 5 s 检查点**刚恢复**时，`WaitDriverEvent` 返回，`FollowEventLog` 重新订阅 —— 从"服务恢复"到"我们重新订阅"这段时间内，如果服务端也做了 `EvtSubscribe(Future)`，两边的 cursor 都是新的，不会重复。反之如果服务端用 `EvtSubscribeToFutureEvents` 但我们在它之前订阅了，可能两边都收到一条 ⇒ **重复触发一次热键**（例如性能模式连跳两级）。属于低频 P2。

3. **服务不存在时 `ServiceCheck.IsRunning` 的开销（P2）**
   每 5 s 走一次 `OpenSCManagerW` + `OpenServiceW` + `QueryServiceStatus`，失败路径全部 `return false` 并 `CloseServiceHandle`，无句柄泄漏。✅

4. **`TrayApp.TryStartHotkeys()` 只建一次（P1 关联）**

```csharp
// Agent\TrayApp.cs:91-98
private void TryStartHotkeys()
{
    if (_hotkeys is not null || _engine.Dchu is not { } dchu) return;
    _hotkeys = new HotkeyListener(dchu);
    _hotkeys.HotkeyPressed += OnHotkey;
    _hotkeys.Start();
    Log.Write("热键监听已启动");
}
```

- 创建依赖 `_engine.Dchu`，而 `Dchu` 只在 `Engine.Connect()` 成功后才有值。所以：**如果 Agent 启动时驱动还没就绪，`_hotkeys` 永远是 null**，直到 30 s 一次的 `RunMaintenance` 里 `_engine.Ready == false` 分支重新 `Connect()` 并 `_ui.BeginInvoke(TryStartHotkeys)`。这条路径是通的 ✅。
- **但 `HotkeyListener` 的线程如果异常退出，`_hotkeys` 不是 null，`TryStartHotkeys` 再也不会重建它。** `Run` 的 catch 会无限重试（`failures` 封顶 5 s 间隔），所以线程本身不会死 —— 除非 `_cts` 被 cancel 或 Dispose。`Dispose` 只在 `ExitApp` 调用。⇒ 这一条**实际风险低**，但是代码里没有任何"线程活着吗"的检查，属于健壮性缺口（P2）。

5. **通道选择的日志提示不足（P1-16）**
   当前只在"通道发生变化"时写一次日志（`if (service != _lastSource)`）。用户如果一开机就是服务通道，日志里永远只有那一行；出问题时用户/客服无法从界面判断"我现在到底走的哪条通道、驱动通道能不能用"。建议：托盘菜单加一项"热键诊断"，显示当前通道 + 最近一次热键的键码与时间。

### 5.4 键码表与规划书的核对（全部实现）

| 规划书承诺 | 实现位置 | 结论 |
| --- | --- | --- |
| 89 性能模式循环 | `TrayApp.cs:274-278` | ✅ |
| 143 风扇全速 | `:280-283` | ✅ |
| 112 风扇自动 | `:284-287` | ✅ |
| 246/247 摄像头 | `:305-307` | ✅ |
| 93 触控板 | `:308-310`（+252） | ⚠️ 实现了但**功能是错的**（P0-5） |
| 133/134 飞行模式 | `:301-303` | ⚠️ 实现了但**连按切不回**（P0-3） |
| 129/130 键盘灯亮度 | `:289-297`（+32/33） | ✅ |
| 158/159 键盘灯开关 | `:298-300` | ✅ |
| 149 打开主界面 | `:288` | ✅ |
| （额外）199-204 锁键提示 | `:313-326` | ✅ 只回显 |
| （额外）250/251 音量 | `:327-330` | ✅ 只回显 |
| （额外）215 关屏唤醒钩子 | `:331-333` | ✅ |

**没有遗漏任何承诺的键码**，这是做得很好的一点。

---

## 6. P0 详细分析

### 6.1 P0-1 风扇曲线节点 1 只影响斜率，不能独立生效

**精确结论（避免误读）**：T1/D1 **不是"完全无效的字段"**，它是 R12 斜率的输入之一：

```csharp
// Core\Features\FanCurve.cs:21-23
public int R12 => Slope(T1, D1, T2, D2);
public int R23 => Slope(T2, D2, T3, D3);
public int R34 => Slope(T3, D3, T4, D4);

// Core\Features\FanCurve.cs:14-19
private static int Slope(byte tA, byte dA, byte tB, byte dB)
{
    if (tB <= tA) return 0;
    return (int)Math.Round((dB - dA) / (double)(tB - tA) * 2.55 * 16);
}
```

`ToWmi14()` 把 R12/R23/R34 三条斜率都写进 WMI 14 包（`FanCurve.cs:71-91` 的 `Slopes`），而 `Put()` 只写 T2/D2/T3/D3。所以：

| 用户改了什么 | 实际会发生什么 |
| --- | --- |
| 改 **T1**（起转温度） | **什么都不发生**。EC 的起点温度是固件常量（`FanService.cs:105-113` 里 `b[o+4]=40` 就是它写回 App 页的"起转 40 °C"），T1 只在 `tB > tA` 时参与 R12 计算，改 T1 只是改一个分子分母同时变的斜率，**没法把"风扇从多少度开始转"改掉** |
| 改 **D1**（起转转速） | **只改变 40 °C→T2 这一段的斜率**（R12 变大/变小），**不是"40 °C 时的转速"**。EC 在 40 °C 时用什么转速由它自己的固件表决定，HaseeCtl 只能通过斜率间接影响"升温过程中加得多快" |
| 改 T2/D2/T3/D3 | **完整生效**（`Put()` 直接写这 4 个字节） |

因此 P0-1 的性质是**"产品语义承诺了硬件协议做不到的事"**，不是"数据被丢弃"：

- `App\Pages\FanPage.xaml.cs:82-88` 的文案把 T1/D1 写成"起转 {T1}°C/{D1}%（固件固定）"——既叫它"起转转速"，又说是"固件固定"，**自相矛盾**；而且它读的是 `_defaults`（固件值），配置里用户写的 `d1=25` 在这条文案里根本不出现。
- `Config\Settings.cs:7-12` 的 `D1` 默认 25，而 EC 里固定被写成 80（`FanService.cs:105-113` 的 `b[o+4]=40; b[o+5]=80;`）。**两个数字差 3.2 倍**，说明作者自己也在两套语义之间摇摆。
- 配置文件里 `t1/d1` 的在场会误导后续的自动化/迁移脚本，也让"我设了 25% 起转，怎么 40 度就吵起来了"变成必然的困惑。
- **最危险的不是字段本身，而是叠加 P0-2**：用户以为自己已经把 40 °C 的转速压到 25%（安静），实际上 40 °C 时 EC 可能跑在 80%，而 78–95 °C 的加速段又被拉平到 25% —— **两头都跟用户想的不一样**。
- **决策记录**：Lead 已拍板 **删掉 T1/D1**（UI 上不再暴露、配置不再存、`FanCurve` 只保留 T2/D2/T3/D3 与 T4/D4），理由是"产品语义不该承诺做不到的事"。删除时注意：`FanCurve` 是 `record` 且被 `Settings` 持久化，删字段要同时处理旧配置的反序列化兼容（缺字段用默认值即可，`System.Text.Json` 对缺失属性是容忍的）。**如果保留 T1/D1，则必须把 UI 文案改成"起转 40 °C（固件固定，不可调）；D1 仅表示 40 °C→节点 2 的加速斜率"**，二者选一，不能像现在这样两头都不说清。
- `FanPage` 的 `RefreshInfo`（`:82-88`）文案"起转 {T1}°C/{D1}%（固件固定）"里的 T1/D1 来自 `_defaults`（固件），不是配置；但同一句话旁边又有"节点 2：{c.T2}°C → {c.D2}%"来自配置。**UI 内部就混用了两个数据源**。

### 6.2 P0-2 无过温兜底（专章见 §8）

### 6.3 P0-3 / P0-4 飞行模式

见 §3.4。补充：

- `SettingsPage.LoadSystem()`（`:71`）的 `SystemStatus.Text` 里"飞行模式开/关"是**整个程序里唯一显示飞行模式状态的地方**，而它与 `OnAirplane` 写完之后的文案（`:139`）来自不同的源。
- 托盘里**没有**飞行模式菜单项（只有热键 133/134），所以用户在托盘上根本看不到飞行模式状态。
- `SystemService.Try(...)`（`:129-132`）是所有系统级写操作的统一包装：

```csharp
private static void Try(Action a, [CallerMemberName] string caller = "")
{
    try { a(); }
    catch (Exception ex) { Log.Error("系统功能调用失败 (" + caller + ")", ex); }
}
```

`SyncTouchpadToEc`、`SetCamera`（前半）、`LowRefreshEnabled` 的 setter、`WakePanel` 全部走它 ⇒ **这些操作的失败全部只记日志，调用方一律不知道**。`SetCamera` 因为后面还 `return sys.CameraOn` 才侥幸正确（回读救了一切）；`SyncTouchpadToEc` 和 `WakePanel` 就没有这个保护。

### 6.4 P0-5 触控板 Fn 键不切换

```csharp
// Agent\TrayApp.cs:362-372
private void TouchpadKey()
{
    Thread.Sleep(150); // hotkey thread only; the UI thread is never blocked
    bool? on = SystemService.TouchpadEnabled();
    if (on is bool v)
    {
        if (_engine.Sys is { } sys) _engine.WithHardware(() => sys.SyncTouchpadToEc(v));
        ShowOsd("触控板", v ? "已开启" : "已关闭");
    }
    else ShowOsd("触控板");
}
```

对比 `SystemService.SetTouchpad`（`:26-36`）：

```csharp
public bool SetTouchpad(bool enable)
{
    if (TouchpadEnabled() != enable)
    {
        SendCtrlWinF24();                          // ← 真正切换触控板的那一步
        for (int i = 0; i < 15 && TouchpadEnabled() != enable; i++) Thread.Sleep(100);
    }
    bool now = TouchpadEnabled() ?? enable;
    SyncTouchpadToEc(now);
    return now;
}
```

`TouchpadKey()` 的语义是"**读 Windows 的状态，再把它写回 EC**"，**没有任何一次状态翻转**。用户按 Fn+F1（或热键 93/252）时，触控板不会关也不会开，只会弹一个"触控板 已开启"的 OSD。

`docs\使用说明.md` 里已经诚实地写成"93 触控板开关（读系统状态并提示）"，说明作者知道；但**用户的目标是"每个选项都能正常读取/调整/修改"**，这个功能现在是不可用的。

**修法**：`TouchpadKey()` 应改为 `Thread.Sleep(150); var cur = SystemService.TouchpadEnabled(); ... sys.SetTouchpad(cur != true);`（注意 `SetTouchpad` 内有最长 1.5 s 的同步轮询，不能在热键线程里无限制阻塞，建议改成异步 + OSD 延后）。

### 6.5 P0-6 显卡切换非原子

见 §3.6。

### 6.6 P0-7 关闭态充电上限不再下发

见 §3.3。

### 6.7 关于"Agent 锁序死锁"的复核与撤回（原 P0-8）

> **本节结论：原 P0-8 不成立，已撤回。** 初版把 `RunMaintenance` + `HardwareLock.Monitor.Enter` 判为可导致进程假死的死锁，经 Lead 复核与本节二次核实，**因果链有一环不成立**，现降级为 P2 健壮性建议。保留本节是为了记录"为什么它不成立"，避免后续复审重复踩。

**初版的（错误）推断**：UI 线程持 `_hw` 锁 → 后台线程 `ApplyAll()` 阻塞在 `Monitor.Enter(_sync)` → UI 线程在模态消息泵里又被投递的 `BeginInvoke` 委托唤醒并去抢同一把锁 → 两线程互等。

**三条反驳（全部成立）**

| # | 反驳 | 证据 |
| --- | --- | --- |
| 1 | **`Monitor` 是可重入锁**，同线程重入不会自锁。`RunMaintenance` 的后台线程在 `Connect()`（`Engine.cs:60` 附近拿锁）→ 释放 → `_ui.BeginInvoke` → `ApplyAll()`（`:151` 再拿锁）这条链上**始终是同一个线程**，不构成"自己等自己" | `HardwareLock.cs:33 Monitor.Enter(_sync)`；`Engine.cs:151` |
| 2 | **`Control.BeginInvoke` 是单向异步投递，不阻塞调用方**。UI 线程处理这条消息时**不需要等待后台线程做任何事**，所以不存在"UI 等后台"这条边 | `TrayApp.cs:112` `_ui.BeginInvoke(TryStartHotkeys);` 后面没有任何 `Task.Wait`/`EndInvoke` |
| 3 | **真要死锁，必须存在"UI 线程同步等待后台任务"**（`.Wait()` / `.Result` / `Task.WaitAll`）。全仓库**没有一处**这样做：`RunBackground`（`:84-89`）是 `Task.Run(...)` 丢弃返回值，`RunMaintenance`（`:104`）同样丢弃 | 全仓 `grep Task.Run` 只有 `TrayApp.cs:85`、`:104` 两处，均不等待 |

⇒ **没有循环等待的边，就没有死锁。** 原 P0-8 撤回。

**那还剩什么真问题？**（降级后的 P2 建议，两条都保留）

1. **`_maintenanceBusy` 的复位路径已核实是完备的**：`:103` 置位后，`:119` 的 `finally` 一定执行（`try` 内所有 `return` 都经过 `finally`，`catch` 之后也进 `finally`；`Task.Run` 的委托体本身没有能让 `finally` 跳过的路径）。**因此不存在"卡在 1 永久跳过维护"的缺陷** —— 初版关于"后续维护被永久跳过"的说法同样撤回。
2. **`Monitor.Enter(_sync)` 无超时仍然是健壮性缺陷**：一旦有线程持锁期间异常终止或卡死（例如 `AcpiBridge` 的 `DeviceIoControl` 在驱动响应异常时永久阻塞），其它线程会**无限期阻塞，没有超时兜底**。虽然 .NET 在异常退出时会走 `using` 释放锁，使概率极低，但**驱动层 IOCTL 永久 hang 是真实存在的场景**，这时无超时的 `Monitor` 会让 App 与 Agent 双双僵在锁上。
3. **建议（P2）**：把 `Monitor.Enter(_sync)` 改成 `Monitor.TryEnter(_sync, WaitMs)`，超时抛 `TimeoutException` —— 与 P1-1（跨进程 Mutex 超时后照常执行）**用同一个修法一起解决**：**超时就明确失败，绝不静默进临界区**。

---

## 7. P1 详细分析

### 7.1 P1-4 切页丢草稿（Lead 关心的"面板未保存即丢"）

```csharp
// App\MainWindow.xaml.cs:22-37
private void OnNavChanged(object sender, SelectionChangedEventArgs e)
{
    if (NavList.SelectedItem is not ListBoxItem item || item.Tag is not string tag) return;
    FrameworkElement page = tag switch
    {
        "fan" => new FanPage(),
        "performance" => new PerformancePage(),
        ...
    };
    page.DataContext = _state;
    PageHost.Content = page;
}
```

每次切换**新建**页面对象。而各页的 `OnLoaded` 都是：

```csharp
// App\Pages\FanPage.xaml.cs:27-35
private void OnLoaded(object sender, RoutedEventArgs e)
{
    if (_state is not null) return;   // ← 新对象的 _state 一定是 null，这行永远不 return
    _state = DataContext as UiState;
    ...
    ReloadProfiles();                 // ← _draft 被重建，之前的编辑全丢
}
```

丢的东西：

| 页面 | 丢失的字段 | 用户可见后果 |
| --- | --- | --- |
| FanPage | `_draft`（编辑中的曲线副本）、`_showGpu`（CPU/GPU 标签页选择） | 拖了半天曲线，去设置页看一眼回来就没了 |
| GpuPage | `_current`（当前模式缓存，也是"已经是该模式"判断的依据） | 重新读一次（可接受） |
| BatteryPage | `_limit`（固件支持列表） | 重新读一次（可接受，但叠加 P1-6 会重新"猜错"一次） |
| LightPage | `_left/_mid/_right` | 刚选的色块丢了 |
| PerformancePage / SettingsPage | 无关键草稿 | 只是多做几次 EC 往返 |

`FanPage._draft` 的注释本身就写着 *"Edits stay here until a save/apply button commits them through UiState.Update, so a reload triggered by the Agent can never drop them"* —— 作者考虑到了 Agent 触发的 reload，**但没考虑到自己切页会 new 一个新对象**。

**修法**：`MainWindow` 缓存页面实例（`Dictionary<string, FrameworkElement>`），只在第一次导航时创建；或者把草稿提升到 `UiState` 里。

### 7.2 P1-5 保存头像"当前方案"但不下发

```csharp
// App\Pages\FanPage.xaml.cs:171-198
private void OnSaveProfile(object sender, RoutedEventArgs e)
{
    ...
    SaveDraft(p, activate: true);              // ← 会改 s.Fan.ActiveProfile，并落盘
    ProfileHint.Text = $"已保存方案：{p.Name}（下次开机/唤醒会自动应用）";
}
```

`SaveDraft(..., activate:true)` 把 `ActiveProfile` 改成了这个方案，但**没有 `ApplyFan()`**。此时：

- 配置文件：`activeProfile = 新方案`、`mode = 原值`（可能是 `custom`）
- EC：还在跑**旧**方案
- 托盘菜单的"风扇配置"子项：`Checked = s.Fan.Mode == "custom" && s.Fan.ActiveProfile == name` ⇒ 显示"新方案"被选中（**假**）
- 30 s 后的 `VerifyFan` 只比较 `Fan.CurrentMode == expected`，两者都是 Custom，所以**不会纠正**

⇒ 界面/托盘说"当前是方案 X"，机器在跑方案 Y，可能一直持续到下次开机/唤醒。这是一个典型的"显示与实际不符"。

**修法**：`OnSaveProfile`/`OnSaveAs` 若要改 `ActiveProfile`，就必须 `ApplyFan()`；否则应 `activate:false`，只保存不选中。

### 7.3 P1-6 充电档位猜错

```csharp
// App\Pages\BatteryPage.xaml.cs:56-61
private static void SelectValue(ComboBox box, int[] values, int value)
{
    int idx = Array.IndexOf(values, value);
    if (idx < 0) idx = values.Length > 0 ? values.Length - 1 : 0;   // ← 找不到就选最后一项
    box.SelectedIndex = idx;
}
```

`values` 来自 `Charge.Read()` 的 `o[16..31]` / `o[32..47]`（`ChargeService.cs`），是固件报的"允许的起止值"。如果固件报的当前值不在这个列表里（例如固件升级过、或者 `o[1]/o[2]` 是厂商写入的实际值而列表是可选值），就会选中最后一档。叠加 `OnApply`：

```csharp
private void OnApply(object sender, RoutedEventArgs e) =>
    Apply(EnableCheck.IsChecked == true, SelectedValue(StartBox), SelectedValue(StopBox));
```

⇒ 用户什么都没改，只是"打开页面 → 点应用"，就把 65% 改成了 95%。**违反规划书 R3"读出 BIOS 允许的起止值"**。

**修法**：找不到匹配时，把真实值作为一个额外项动态插进 ComboBox（`"65%（固件当前值）"`），而不是静默替换。

### 7.4 P1-7 / P1-8 键盘灯

见 §3.5。

### 7.5 P1-9 Turbo Burst 锁时序

```csharp
// App\Pages\FanPage.xaml.cs:318-343
private async void OnModeTurboBurst(object sender, RoutedEventArgs e)
{
    ...
    engine.WithHardware(() => fan.SetMode(FanMode.Max));   // ← 锁只覆盖这一行
    ProfileHint.Text = "全速运行中… 3 秒后恢复。";
    await Task.Delay(3000);                                 // ← 3 秒内无锁
    engine.ApplyFan();                                      // ← 3 秒后恢复
    ...
}
```

三个问题：

1. 3 秒全速期间，Agent 的任意 `ApplyFan`（例如电源切换 `OnPowerStatusChange`、唤醒 `OnResume`、用户点托盘）会把模式打回保存值，全速被提前中止，用户以为按了没反应。
2. 3 秒后 `ApplyFan()` 无条件执行，会覆盖用户在这 3 秒内主动做的模式切换（例如点了"静音"）。
3. `async void` + `Warn(...)` 的异常处理，如果 `button.IsEnabled = true` 之前抛异常，按钮永久禁用（`finally` 里有，✅）。

**修法**：把"进入全速 + 记下原模式 + 3 秒 + 恢复原模式"做成 `Engine` 的一个原子方法，用 `CancellationToken` 支持"再按一次取消"。

### 7.6 P1-10 性能模式不rollback

```csharp
// App\Pages\PerformancePage.xaml.cs:64-77
private void OnModeChecked(object sender, RoutedEventArgs e)
{
    if (_loading || _state is null) return;
    if (sender is not RadioButton { Tag: PowerModeKind mode }) return;
    try { _state.Engine.SetPerformanceMode(mode); CurrentText.Text = PowerModes.DisplayName(mode); }
    catch (Exception ex) { MessageBox.Show(ex.Message, "切换失败", ...); }   // ← 不恢复 IsChecked
}
```

失败后 `rb.IsChecked` 仍为 true，`CurrentText` 还是旧的 ⇒ 界面自相矛盾。修法：catch 里 `_loading = true; 恢复旧模式的 IsChecked; _loading = false;`。

### 7.11 P1-11 强冷静默降级

```csharp
public void SetTurboFan(bool on)
{
    dchu.WriteAppByte(4, 8, (byte)(on ? 1 : 0));
    if (!on) return;
    if (caps.Dtt && !DttCheck.IsInstalled) dchu.WriteAppByte(4, 8, 0);   // ← 想开却写回 0
}
```

用户勾上"强冷"，界面显示勾选，EC 里是 0，没有任何提示。下次 `OnLoaded` 读回 `TurboFanEnabled = false`，开关自己弹回去 —— 用户会以为是 bug。修法：这种情况下 `throw new InvalidOperationException("本机强冷需要 Dynamic Tuning 驱动（DTT），当前未安装")`，或至少在 UI 上置灰并给出提示。

### 7.12 P1-12 / P1-13 / P1-14 / P1-15

- **P1-12**：`SettingsPage.OnReload`（`:185-198`）里 `OnLoaded(this, e)` 只刷新自己。由于 `MainWindow` 每次导航都 new 页面，其它页面在**下次导航时**会重新读，所以实际影响小于表面 —— 但界面上如果用户先打开风扇页、再去设置页重载、再切回风扇页，看到的是新值，**行为上是"碰巧正确"**。属"代码没有通知机制，靠页面重建兜底"，一旦将来改成缓存页面就会立刻暴露。
- **P1-13**：`OnLowRefreshChanged` 写完后只更新 `SystemStatus.Text = $"当前刷新率 {Display.CurrentHz()} Hz"`，把"飞行模式"那段丢了（**顺手把界面信息减半**），且不读回 `LowRefreshEnabled`。另外 `ApplyRefreshRate()` 只在电池态才切换刷新率（`onBattery` 判断），用户插着电勾选，看不到任何变化，会认为功能坏了。
- **P1-14**：`WakeHook.BeginDisarm() => SynchronizationContext.Current?.Post(_ => Disarm(), null);` —— 钩子回调在系统线程上执行，`SynchronizationContext.Current` 在那里通常是 null。`OnMouse`/`OnKey` 里调用 `BeginDisarm()`，如果 null，`Disarm()` 永远不会被调用，`_mouse`/`_key` 两个低级钩子一直挂着（`_fired` 已为 true，不会重复触发，所以只是句柄泄漏 + 全局键鼠钩子常驻，性能影响）。修法：改用 `_ui.BeginInvoke(...)`（TrayApp 已有 `_ui` 控件可作 marshal 目标），或在 `Arm()` 时捕获并保存 `SynchronizationContext`。
- **P1-15**：`App.xaml.cs` 的 `DispatcherUnhandledException` 设置 `e.Handled = true` 后弹框继续运行。WPF 里 UI 异常后视觉树/绑定状态可能已经损坏，继续跑会出现"界面卡住但进程还在"、"按钮点了没反应"之类的症状。建议改成：记录 + 弹框 + `Environment.Exit(1)`（或至少给用户一个"重启程序"的按钮）。

### 7.16 P1-16（已降级）/ P1-17

**P1-16 降级说明（Lead 复核后）**：原表述"驱动通道从未在真机验证过，而卸载 Control Center 场景恰好依赖它"**前提有误**。定量看：

```
CCDCHUService 属 oem154 驱动包（DriverStore，LocalSystem，AUTO_START），卸载 Control Center 不删
  ⇒ ServiceCheck.IsRunning("CCDCHUService") 恒 true
  ⇒ _dchu.ReadAppByte(1,12) == 1 恒 true
  ⇒ UseServiceChannel = FORCE_DRIVER!="1" && true && true = 恒 true
  ⇒ 通道 B（WaitDriverEvent）在生产环境永不被选用
```

所以"卸载后热键会退到未验证的通道 B"这个风险**在正常卸载路线上不存在**。P1-16 的实际内容是**文档不准确**：`docs\使用说明.md` 的通道说明段写"两条通道都已实测注册成功"，与 `DriverProbe` 实测 0 键码不符，应改成"服务通道已实测；驱动通知通道已能注册但未收到过真实键码，仅在手动删除 CCDCHUService 时启用"。

**降级后仍保留的残余风险**（不是 P1，但要在文档里讲）：用户若手动 `sc delete CCDCHUService`，程序 5 秒内切到通道 B，而通道 B 的键码接收从未验证。建议切通道时打 WARN 日志 + 托盘气泡提示一次。

**P1-17**：见 §2.2（`HKClipSvc` 孤儿）。

---

## 8. 风扇曲线散热安全性专项（重点：用户提出的第 3 个实机现象）

### 8.1 实机配置

`%APPDATA%\HaseeCtl\settings.json`：

```json
"fan": {
  "mode": "custom",
  "activeProfile": "均衡",
  "profiles": [
    {
      "name": "均衡",
      "cpu": { "t1": 40, "d1": 25, "t2": 78, "d2": 25, "t3": 95, "d3": 86 },
      "gpu1": { "t1": 40, "d1": 25, "t2": 78, "d2": 25, "t3": 95, "d3": 86 }
    }
  ]
}
```

### 8.2 这条曲线实际意味着什么

`FanCurve.DutyAt(t)` 是线性插值（`Core\Features\FanCurve.cs:56-64`）。按这组数：

| CPU 温度 | 曲线给出的占空比 | 说明 |
| --- | --- | --- |
| ≤ 40 °C | 25% | 起转点 |
| 40 → 78 °C | **25% 恒定** | D1 = D2 = 25，斜率 0 |
| 78 → 95 °C | 25% → 86% | 这一段才真正开始加速（约 3.6%/°C） |
| 95 → 100 °C | 86% → 100% | 最后 5 度拉满 |

**关键问题：40 °C 到 95 °C 这 55 度的区间，风扇一直是 25% 占空比。** 而笔记本 SoC 在持续负载（编译、游戏、视频导出）下的稳态温度通常就落在 **80–95 °C** —— 也就是说，**在绝大多数真实负载下，这条曲线输出的都是 25%**。

这跟"均衡"这个名字是矛盾的。对比代码里的默认值（`Config\Settings.cs:89-100`）：

```csharp
new FanProfile { Name = "均衡",
    Cpu  = new() { T1 = 40, D1 = 25, T2 = 60, D2 = 40, T3 = 80, D3 = 80 },
    Gpu1 = new() { T1 = 40, D1 = 25, T2 = 60, D2 = 40, T3 = 85, D3 = 80 } }
```

默认"均衡"是 **60 °C 就上 40%、80 °C 就上 80%**。用户当前的实测配置把节点 2 从 `60/40` 拖成了 `78/25`，把节点 3 从 `80/80` 拖成了 `95/86` —— 整体曲线**向右下平移**，等于把"该加速的温度"推后了 15–18 度，同时把中段占空比从 40% 降到 25%。

历史记录（`F:\vibecode\REA_CON\HaseeCtl_审查报告.md:72`）写着："风扇曲线加最低转速保护（例如 85 °C 以上不低于 50%）→ **按你的要求已取消**"。所以这是**用户主动要求的"不加保护"**，程序照做了。审查意见是：

- 允许用户关闭软件保护是**合理的产品决策**（有人加了外置散热、有人就是要安静）
- **但当前实现把"没有软件保护"和"没有任何提示/兜底"混为一谈了**，这才是问题

### 8.3 程序里有没有任何兜底？

逐条检查：

| 可能的兜底 | 是否存在 | 位置 |
| --- | --- | --- |
| 占空比下限 | ❌ | `FanCurve.Normalize()` `d1 = Math.Clamp((int)D1, 0, 100)` —— 下限是 **0** |
| 曲线形状校验 | ✅ 但只管单调性 | `Validate()`：T 递增、D 非降、T1 ≥ 20 |
| 软件最低转速保护 | ❌ **明确不存在** | `Validate()` 注释：*"Deliberately no software minimum duty: the EC and the BIOS table already enforce whatever the hardware requires"* |
| 温度监控 + 强制全速 | ❌ | 全仓库搜不到任何"温度 → 动作"的回调；`VerifyFan()`（`Engine.cs:384-394`）只比对 `Fan.CurrentMode`，**不看温度**；唯一的曲线求值函数 `FanCurve.DutyAt(double t)`（`FanCurve.cs:56-64`）**全仓库零调用点**，是死代码 —— 也就是说程序连"算一下这条曲线在 90 °C 是多少"这个动作都从没做过 |
| 过温告警（OSD/托盘/日志） | ❌ | 无 |
| 曲线越界提示（"这条曲线在 90 °C 时只有 25%"） | ❌ | `CommitEditor` 只校验 `Validate()` 和 T2<T3 |
| EC 自身的保护 | ⚠️ 未知/不可依赖 | 注释声称"EC 和 BIOS 表已经强制了硬件所需的转速"，但**没有任何证据**；而且 `Validate()` 允许 D=0，如果 EC 真的有硬件下限，作者也没必要留这句注释 |

⇒ **答案是：没有任何兜底。** 一旦 EC 真的按 25% 跑，软件侧完全不会介入。

### 8.4 风险评估

| 风险 | 评估 |
| --- | --- |
| 长期高温运行（85–95 °C 区间长时间 25% 占空比） | **中高**。笔记本 SoC 的 Tjmax 通常是 100 °C，85–95 °C 长期运行会加速电解电容/供电模块老化，且会频繁触发 EC 自身的降频（Performance 断崖） |
| 温度冲击（跑编译瞬间从 60 °C 冲到 90 °C） | 25% 的风扇对 90 °C 的散热能力极低，温升速率高于默认曲线，会更快顶到降频点 |
| 噪音/体验 | 25% 确实安静，这正是用户想要的效果，属可接受的 trade-off |
| 用户误操作（拖坏了曲线但没意识到） | **高**。曲线编辑器只显示"节点 2：78 °C → 25%"，没有一个"这条曲线在 90 °C 时只有 25%"的直观告警 |
| **叠加 P0-1（T1/D1 只能改斜率）** | 用户以为"我把起转点设在 40 °C / 25%，那 40 度以上就是 25%"，实际是：40 °C 起的**起点转速是固件常量**（`FanService.cs:105-113` 写回的 `b[o+5]=80`），他改的 D1=25 只是把 40→78 °C 这一段的**斜率**从"80% 降到 25%"变成"80% 降到 25% 但起点仍是 80"的另一种算法 —— **用户在 UI 上看到的"起转 25%"和 EC 实际在 40 °C 时的转速不是一个量**。这是"用户对曲线的心智模型和实际行为不一致"的具体形态，也是最危险的部分 |

### 8.5 建议（按优先级）

**Lead 决策已锁定：P0-2 只做"非阻塞提示 + 只读过温指示"，绝不加任何强制转速下限，不强加温度兜底动作。** 下面 1–3 条即为该决策的落地清单；第 4–5 条标为"需用户明确同意"，默认不做。

**必做（不违背"不加软件最低转速"的要求）**

1. **曲线编辑器加"安全提示"而不加"强制"**：当曲线在 85–100 °C 区间的占空比低于某个阈值（例如 50%）时，`CommitEditor` 弹一个**非阻塞**的黄色提示："这条曲线在 90 °C 时只有 25% 转速，长时间高负载可能导致过热降频。确定要保存吗？" —— 用户仍然可以点"确定"，只是知情。
2. **增加一个只读的过温指示**：`UiState` 已经在每秒读 `CpuTempC`，加一个"上次 > 95 °C 的时刻"并在概览页/托盘变红/弹 OSD 一次即可。**不改变风扇行为**，只是让用户知道"你的曲线在过热"。
3. **把 P0-1 修掉或彻底删掉 T1/D1**，让用户对自己曲线的理解与 EC 实际行为一致。

**可选（需要用户明确同意）**

4. 提供一个**默认关闭**的开关"过温保护（> 95 °C 强制全速，持续 10 秒）"。这并不违反"取消最低转速保护"的要求——那是"常态强制"，这是"应急兜底"，两者可以共存。
5. 在"恢复出厂"按钮旁边加一个"载入安全的均衡预设"，把默认三档预设（`Settings.cs:89-100`）做成可一键还原。

**不建议**

- 直接把 `Normalize` 的下限从 0 改成 30 —— 那是用户明确拒绝过的做法，且会破坏"静音"方案的存在意义。

### 8.6 对当前实机配置的直接建议

当前"均衡"方案在 40–95 °C 恒 25%，**建议至少把节点 2 拉回 `75 °C → 45%`、节点 3 拉到 `90 °C → 85%`**，这样在 85–90 °C 的真实高负载区间能拿到 60–80% 的转速，同时 75 °C 以下仍然是 25%（安静）。这是用户自己在 UI 上能做的调整，不需要改代码。

> **注意（P0-1 的直接后果）**：调完节点 2 / 节点 3 后，**节点 1 那一栏无论显示什么都改变不了"起转温度 = 40 °C"这个固件常量**（见 §6.1）。不要指望通过 D1 把 40 °C 时的转速压下去 —— 想更安静只能靠限功耗/降压/外置散热，或者等 P0-1 的"删掉 T1/D1"方案落地，让 UI 不再承诺做不到的事。

---

## 9. 日志噪声专章（Lead 关心的现象 1）

### 9.1 事实校正

`%LOCALAPPDATA%\HaseeCtl\logs\haseectl-20261101.log` 里 08:53:35.611 ~ 08:53:40.900 出现 6 对：

```
[08:53:35.611] [24464] 设置已重新加载
[08:53:35.612] [24464] 配置文件已变化，已重新加载
...
```

**判定：不是无限回环。**

理由：
1. `Engine.ReloadSettings()`（`Engine.cs:105-120`）**只做 `_settings = r.Settings; _stamp = r.Stamp;`，从不调用 `Save`**，所以不会再次触发 `FileSystemWatcher`
2. 6 对之后日志彻底停止，没有第 7 次
3. 时间跨度 5.3 秒，符合"用户在界面上连续改了 6 项，每项各保存一次"的操作节奏

`FileSystemWatcher` 的 `Filter` 精确设为 `settings.json`（`TrayApp.cs:128-141`），所以 `<pid>.tmp` 的写入不会触发，只有 `File.Move` 的最终重命名触发一次 —— **设计是对的**。

### 9.2 真正的噪声源（P2）

按出现频率排序：

| 位置 | 日志内容 | 频率 |
| --- | --- | --- |
| `Engine.cs:195` | `应用风扇: mode=... profile=... offset=...` | 每次 `ApplyFan`（含每次性能模式切换、每次唤醒的两次、每次电源切换） |
| `Engine.cs:391` | `风扇模式被改动 (EC=..., 期望=...)，重新应用` | `VerifyFan` 每 30 s 一次（只在真被改动时） |
| `SystemService.cs:45` | `已启用 EC 控制 I2C 触控板 (H2RAM 219)` | 每次 `ApplySystem` ⇒ 每次 `ApplyAll` ⇒ 启动/唤醒/手动重应用 |
| `Engine.cs:117` | `设置已重新加载` | 每次 `ReloadSettings` |
| `TrayApp.cs:162` | `配置文件已变化，已重新加载` | 每次重载（与上一条成对） |
| `SettingsStore.cs:176` | `保存设置失败: ...` | 失败时（不算噪声，必须保留） |
| `SystemService.cs:66` | `键盘设置: Fn锁=... Win锁=... 互换=...` | 每次改键盘锁 |
| `Engine.cs:219` / `:227` | `应用性能模式: ...` / `应用充电上限 ...` | 每次对应操作 |

**日志风格问题**：所有消息都是"我打算做什么"，**没有一条是"我读回来确认是不是这样"**。所以日志无法区分"下发成功"和"下发被 EC 忽略"。

### 9.3 建议

1. **`EnableEcTouchpadControl` 的日志改成只在状态变化时写**（现在每 30 分钟的唤醒/重应用都会重复写一条）
2. `ApplyFan`/`ApplyPerformance`/`ApplyCharge` 的日志降级为 `Debug`（如果引入日志级别），或者合并成一条"应用设置: 风扇=X 性能=Y 充电=Z"
3. **`UpdateSettings` 里加"实际写入了什么字段"的审计日志**（例如 `设置变更: Charge.Enabled=false, Charge.Start=70`），才能真正回答"谁把值改回去了"
4. 恢复 `SettingsStore.FileStamp` 的内容哈希（P1-3），减少假重载

---

## 10. 安装 / 自启动 / 分发（Lead 关心的现象 2）

### 10.1 计划任务没注册成——根因不是 bug

`install.ps1` 的自启逻辑：

```powershell
try {
    $action = "`"$agentExe`""
    schtasks /Delete /TN "$taskName" /F *> $null
    schtasks /Create /TN "$taskName" /TR $action /SC ONLOGON /RL LIMITED /F *> $null
    $taskOk = ($LASTEXITCODE -eq 0)
} catch { $taskOk = $false }

if ($taskOk) {
    Write-Host "  [3/5] 已注册登录自启任务" -ForegroundColor Green
} else {
    # 退路：启动文件夹里的快捷方式，权限永远够
    ...
    Write-Host "  [3/5] 已放入启动文件夹（登录后自动运行）" -ForegroundColor Green
}
```

实机情况：当前会话 `IsAdmin: False`，`schtasks /Create /SC ONLOGON /RL LIMITED` 返回**拒绝访问**（实测 `__HaseeCtlTest` 同样失败），于是走退路，创建了
`%APPDATA%\Microsoft\Windows\Start Menu\Programs\Startup\HaseeCtl Agent.lnk`。

**判定：这是设计好的退路，不是 bug。** 而且代码里有注释"退路：启动文件夹里的快捷方式，权限永远够"，说明作者预料到了。`docs\使用说明.md:16` 也写的是"放入启动文件夹"。

**但有两个真实差距**：

| 差距 | 影响 | 建议 |
| --- | --- | --- |
| 计划任务 vs 启动文件夹 | 启动文件夹**晚 5–15 秒**拉起来（要等 Explorer 处理完整个启动项列表），且**没有失败重试**；任务计划可以配"失败后重启"和"延迟 N 秒" | 文档里说明差异即可；或用 `HKCU\...\Run`（比启动文件夹早，不需要管理员） |
| 没有任何"降级告知" | 用户以为自己拿到了计划任务 | `Write-Host` 已经区分了文案（[3/5] 两句话不同），够了 |

### 10.2 安装目录不完整（新发现，P1）

实机 `%LOCALAPPDATA%\Programs\HaseeCtl` 内容：

```
HaseeCtl.Agent.exe/.dll/.deps.json/.runtimeconfig.json
HaseeCtl.App.exe/.dll/.deps.json/.runtimeconfig.json
HaseeCtl.Core.dll
InsydeAirplanemodehid.dll
Microsoft.Windows.SDK.NET.dll / WinRT.Runtime.dll / Wpf.Ui*.dll
uninstall.ps1
使用说明.md
卸载.cmd
+HaseeCtl.Agent.pdb / HaseeCtl.App.pdb / HaseeCtl.Core.pdb (2026-10-31 22:35 的残留)
```

对比 `dist\HaseeCtl\`：**有 `install.ps1` 和 `安装.cmd`**。

原因：`install.ps1:32-33`

```powershell
Get-ChildItem -Path $source -File |
    Where-Object { $_.Name -notin @('install.ps1','安装.cmd','uninstall.ps1.bak') } |
    ForEach-Object { Copy-Item $_.FullName -Destination $InstallDir -Force }
```

**故意排除**了安装脚本本身。后果：

- 装完之后**无法用安装目录重装/修复**（既没有 `install.ps1` 也没有 `安装.cmd`）
- P1-17 的"清理 HKClipSvc"如果要做，也只能放在 uninstall 里
- 目标机如果 `dist` 被删（用户常见操作），就只剩"卸载再下新版"一条路

**建议**：改成复制 `install.ps1`（它自己已经处理了 `$source` 与 `$InstallDir` 的差异），或者生成一个最小化的"修复.cmd"只做复制+注册。

### 10.3 构建与分发

| 项 | 现状 | 风险 |
| --- | --- | --- |
| `--self-contained false` | 依赖 .NET 10 桌面运行时 | 实机已具备；分发到别的机器需先装运行时。文档已声明（`使用说明.md:7`） |
| App 与 Agent 两次 publish 到同一 `dist` | `.deps.json`/`.runtimeconfig.json` 各一份，`.dll` 各一份，不冲突（实测两个 exe 都能跑） | ⚠️ 如果将来 Agent 加一个 App 没有的包引用，后 publish 的会覆盖同名的 `deps.json` 里的依赖清单 —— 现在是巧合正确 |
| `.pdb` 删除 | `build.ps1` 最后 `Get-ChildItem $out -Filter *.pdb | Remove-Item` | ✅ 但**旧安装目录里的 pdb 不会被清理**（实测有 3 个残留，共 111 KB） |
| `native\InsydeAirplanemodehid.dll` | `Condition="Exists(...)"` | 源码构建者缺失时静默跳过 ⇒ P0-4 |
| `UpdateChecker` 仓库地址 | `SSRYLJRSS/HaseeCtl`（尚不存在） | 点"检查更新"会得到"还没有发布任何版本。"（404 分支处理正确）✅ |

### 10.4 卸载脚本

`uninstall.ps1` 覆盖完整：停进程 → 删计划任务 → **删启动文件夹 lnk** → 删开始菜单/桌面快捷方式 → 删 HKCU 卸载项 → 删 `%APPDATA%\HaseeCtl` 与 `%LOCALAPPDATA%\HaseeCtl`（可用 `-KeepSettings` 保留）→ 自删除。

**Lead 原先担心的"不清理启动文件夹"不成立。**

小问题（P2）：
- `Remove-Item 'HKCU:...\Uninstall\HaseeCtl' -Recurse` 没有 `-ErrorAction SilentlyContinue` 之后又写 `Write-Host "已清除卸载登记"` —— 失败也会打印成功（`$ErrorActionPreference = 'Continue'`，不会抛，但消息是假的）
- `-KeepSettings` 分支保留 `%APPDATA%\HaseeCtl` 但**日志目录**也被保留（在 `%LOCALAPPDATA%`，同一条 `if` 里一起删/留），语义正确
- 自删除逻辑里 `$tmp` 复制了但**从未使用**（死代码）

---

## 11. P2 清单（24 条）

| # | 问题 | 位置 |
| --- | --- | --- |
| P2-1 | 日志噪声（见 §9.2），共 8 个高频点 | 多处 |
| P2-2 | `AcpiBridge.Invoke` 每次调用 `CreateFileW` + `using` 关闭句柄；`Dchu.ReadApp` 为了读 1 字节 `ReadAppStore()` 整 4 KiB（一次 IOCTL + 一次句柄开合）。`Capabilities.Read` 一次连发 7 个命令 ⇒ 7 次句柄开合 | `Hw\AcpiBridge.cs:120-138`、`Hw\Dchu.cs:65-69` |
| P2-3 | `UiState` 每秒 1 次 `ReadStatus`（1 次 IOCTL+句柄开合）+ Dashboard 每 3 s 4 次 EC 往返 ⇒ **App 常驻 4.3–4.8% CPU**（实测） | `App\UiState.cs:39-49`、`Pages\DashboardPage.xaml.cs:33-53` |
| P2-4 | `NativeMethods.cs` 的三个 overlapped P/Invoke（`DeviceIoControl(NativeOverlapped)`、`GetOverlappedResult`、`CancelIoEx`）全项目**未使用** = 死代码 | `Hw\NativeMethods.cs` |
| P2-5 | `TrayApp.DrawFallback()` 的 `Icon.FromHandle(bmp.GetHicon())` **从不 `DestroyIcon`**（HICON 泄漏）。但 `TrayAssets.Create()` 优先用内嵌 `HaseeCtl.Agent.app.ico`（csproj 已 `LogicalName` 正确），**只在 ico 缺失时触发**，因此**不是句柄增长的主因** | `Agent\TrayApp.cs:531-552` |
| P2-6 | **句柄增长的根因与口径**（已按 perf-meter 类型差分重写，初版判断有误）：实测每 30 s 只涨 **Thread +6 / Event +12，其余 14 类含 File delta = 0** ⇒ 设备句柄零堆积。根因是 `RunMaintenance()` 每 30 s 一次**裸 `Task.Run`**（`TrayApp.cs:104`，返回的 Task 被丢弃、不 await、不观察）⇒ 线程池按需拉起 worker，每个 worker = 1 Thread 句柄 + 2 EventPair，故 1:2 比例吻合；913→469 骤降是**线程池 worker 超时退出**（`ThreadTimeoutMs` 默认 15–20 s）一并释放，**不是 GC**（GC 不回收 OS 句柄）。性质：**不是泄漏，是有界锯齿**（约 +720 句柄/小时，峰值 ~913 后回落）。**修法：改用 `System.Threading.Timer` 或 `async + await Task.Delay(30000)`** | `Agent\TrayApp.cs:101-121`、`Agent\TrayApp.cs:65-67`；详见 §4.6 |
| P2-7 | `MainWindow.xaml` 只有 `<ui:TitleBar ShowMinimize="True"/>`，**没有 Closing 拦截** ⇒ 点 X 真的退出进程；App 关掉后 `ShowOsd` 因 `!_ui.IsHandleCreated` 静默 return ⇒ **Fn 热键的 OSD 气泡消失但动作仍执行**（用户会以为"热键坏了"） | `App\MainWindow.xaml`、`Agent\TrayApp.cs:76-81` |
| P2-8 | `App.xaml.cs` 的 `--shot <dir>` 自检参数**发布版未移除** | `App\App.xaml.cs` |
| P2-9 | `BatteryPage.OnSelectionChanged` 在 Stop 已在最后一档时不做任何处理，非法组合留到点应用才报错 | `Pages\BatteryPage.xaml.cs:86-94` |
| P2-10 | `LightPage.OnEffectChanged` 在 `CustomZones` 时 `SetLightColors` + `SetLightEffect` = **两次 `UpdateSettings` + 两次 Save + 两次整页读 EC** | `Pages\LightPage.xaml.cs:157-167` |
| P2-11 | `LightPage` 的 `BrightnessSlider` 在 `OnLoaded` 里赋值会触发 `ValueChanged`（有 `_loading` 保护 ✅），但 `Unloaded` 里 `CommitBrightness()` 会在**切页时**再写一次 EC + 存盘（多一次无谓写入） | `Pages\LightPage.xaml.cs:40`、`:149-155` |
| P2-12 | `PerformancePage.OnBootApplyChanged` 直接 `_state.Update(...)` 没有 try/catch，保存失败会抛到全局 handler | `Pages\PerformancePage.xaml.cs:92-97` |
| P2-13 | `SettingsPage.OnLoaded` 每次都 `LoadSystem()`（含 `WithHardware(() => (sys.CameraOn, sys.ReadKeyboard(), sys.LowRefreshEnabled))` 3 次 EC 往返 + `Display.CurrentHz()`），**且第一行 `TouchpadCheck.IsChecked = SystemService.TouchpadEnabled() == true` 在 `_loading = true` 窗口内赋值**（✅ 正确，不会触发 handler） | `Pages\SettingsPage.xaml.cs:55-76` |
| P2-14 | `SystemService.SetTouchpad` 内同步 `for (int i = 0; i < 15; ...) Thread.Sleep(100)` ⇒ **UI 线程最长阻塞 1.5 s**（`SettingsPage.OnTouchpadChanged` 在 WPF UI 线程上调用） | `Core\Features\SystemService.cs:26-36`、`Pages\SettingsPage.xaml.cs:78-86` |
| P2-15 | `TrayApp.TouchpadKey` / `SetTouchpad` / 热键回调在**热键线程**上直接调 EC，`Thread.Sleep(150)`/`Sleep(100×15)` 会阻塞整个热键线程（不影响 UI，但会丢热键） | `Agent\TrayApp.cs:362-372` |
| P2-16 | `Engine.UpdateSettings` 在持有 `_settingsSync` 时调用 `SettingsStore.Load/Save`（磁盘 IO），而 `SetFanMode` 等又先持有 `_hw.Enter()` ⇒ 锁序 `HardwareLock → _settingsSync`，持锁期间做文件 IO | `Core\Apply\Engine.cs:126-143`、`:251-257` |
| P2-17 | `ApplyAll` 与各 `ApplyXxx` 各自 `_hw.Enter()`，构成嵌套两层加锁（同线程可重入，不死锁，但冗余） | `Core\Apply\Engine.cs:151`、`:172`、`:192` |
| P2-18 | `BatteryInfo.OnAc()` 在 P/Invoke 失败时 `return true`（把"未知"当成"插电"） | `Core\Features\BatteryInfo.cs` |
| P2-19 | `UpdateChecker` 硬编码 `https://api.github.com/repos/SSRYLJRSS/HaseeCtl/releases/latest`，仓库尚不存在（404 分支处理正确） | `Core\Config\UpdateChecker.cs:10-14` |
| P2-20 | `uninstall.ps1` 里 `$tmp = Join-Path $env:TEMP 'haseectl_uninstall.ps1'; Copy-Item ... $tmp` 复制后**从未使用** = 死代码 | `installer\uninstall.ps1` |
| P2-24 | `HotkeyListener.UseServiceChannel` 每 5 s 复检每次开 1 个设备句柄（`ReadAppByte(1,12)`→`ReadAppStore` 整读 4 KiB） + 2 个 SCM 句柄；`Dchu.ReadAppByte` 为读 1 字节读整 4 KiB。**实测 File 类型句柄 delta = 0，不构成增长**（见 P2-6），纯效率问题：建议复检 5 s→30 s、`ReadAppByte` 复用设备句柄并按需长度读 | `Core\Features\HotkeyListener.cs:59`、`:19-26`、`Core\Hw\Dchu.cs:65-71` |
| P2-22 | **`build.ps1` 与 csproj 的自包含性**（Lead 补查项 C）：`--self-contained false` ⇒ 目标机必须装 .NET 10 桌面运行时，`docs\使用说明.md:7` 有写，但 `install.ps1` **不检测**，缺运行时时 Agent/App 双击无任何提示（`dotnet` 宿主直接退 0x80008096） | `build.ps1`（两处 `dotnet publish --self-contained false`）、`installer\install.ps1` |
| P2-23 | **源码构建者拿不到 `InsydeAirplanemodehid.dll`**：该 DLL 不进仓库（`native\README.md` 声明"不随仓库分发"），`HaseeCtl.Core.csproj` 无 `CopyToOutputDirectory` 兜底 ⇒ 从源码构建的产物**必然缺这个 DLL** ⇒ 飞行模式命中 P0-4 的静默假成功路径（`DllNotFoundException` 被 `catch` 吞成一行日志，界面仍显示成功）。不是编译错误（`Directory.Build.props` 无 `AnalysisLevel`/`WarningsAsErrors`，SYSLIB1054 只是可选分析器提示，不影响构建） | `Hw\NativeMethods.cs` 的 `DllImport`、`Core\Features\SystemService.cs:82-90`、`:129-132`、`native\README.md`；详见 §2.3 |
| P2-21 | 测试覆盖缺口：`EngineTests.Concurrent_actions_do_not_corrupt_settings` 用 `Parallel.For(0,40)` 但全部打到**同一个 `_engine` 实例**（进程内）⇒ 只验证了进程内 `lock(_settingsSync)`，**没有覆盖跨进程、没有覆盖 `HardwareLock` 超时、没有覆盖 `Save` 失败回滚**；`UpdateSettings_keeps_changes_written_by_the_other_process` 用 `Thread.Sleep(20)` 串行模拟，**不构成并发测试** | `tests\HaseeCtl.Tests\EngineTests.cs` |

---

## 12. 做得好的地方（应保留，避免修改时破坏）

1. **协议层**：4 个 IOCTL 的输入布局、`_DSM` GUID 的 4 个 DWORD 字节序、App Store 4 KiB 的读写语义都对，并且 `AcpiBridge.FallbackPath` 已经是正确的 `\\?\` 单反斜杠形式
2. **`FanService.SaveAppInfo` 的读-改-写**：为了保留 `b[4]/b[6]/b[7]/b[8]`（尤其 `b[8]` 是 `PowerModeService` 读的 turbo 标志），先 `ReadAppPage(4)` 再写整页 —— 这个细节容易被漏，做了
3. **`SettingsStore.Load` 的损坏恢复**：JSON 解析失败先 `File.Copy` 成 `settings.json.corrupt-yyyyMMdd-HHmmss.bak` 再写默认值，**不丢用户数据**；`IOException` 重试 5×50 ms
4. **数据目录可写性探测**：`HASEECTL_DATA_DIR → %APPDATA% → %LOCALAPPDATA% → %TEMP% → BaseDir\data`，每级真写一个 `.write-probe` 再删
5. **`UpdateSettings` 的"先追平磁盘再改"**设计方向正确（只是有 P1-2 的洞）
6. **`HardwareLock` 的双层设计**（进程内 `Monitor` + 跨进程命名 Mutex）方向正确（只是 P1-1 让它失效）
7. **`TrayApp.RebuildMenu` 每次右键重建并 `Dispose` 旧项** —— 没有菜单项泄漏
8. **`ExitApp` 的清理完整度**：Timer / Watcher / HotkeyListener / WakeHook / NotifyIcon / 菜单 / PowerWindow 全部释放
9. **`HotkeyListener` 的通道切换**设计（两个循环各自 `return` 回主循环重选）是正确且优雅的
10. **`TryParseCode` 的健壮性**：`Arg001^` + 连续数字，有单测覆盖空串、缺数字、错 key、超长数字
11. **键码表**：规划书承诺的 12 组键码**全部实现**，还额外覆盖了锁键提示（199-204）、音量（250-251）、关屏唤醒（215）
12. **单实例**：Agent 用 `Local\HaseeCtl.Agent.Singleton`，App 用 `Local\HaseeCtl.App.Singleton` + `ActivateExistingWindow`
13. **测试 33 个用例全绿**，其中 `FanCurveTests.Normalize_never_adds_a_software_minimum_duty` 用 4 组 InlineData 明确把"不加软件最低转速"锁成了**契约**（注释写了 "The user has extra external cooling"），这个测试本身是好的，**但它也把 P0-2 的隐患固化成了不可回归的契约**，修改时需要同步改测试
14. **体积**：官方全栈 444.31 MB → HaseeCtl 34.58 MB（12.8×），关掉界面后常驻仅 ≈17 MB（官方 76 MB），达成规划书 R8

---

## 13. 修复优先级建议（给 Lead 排期参考）

### 第一优先（P0，必须先修，否则"没有 bug"不成立）

| 顺序 | 缺陷 | 预估工作量 | 依赖 |
| --- | --- | --- | --- |
| 1 | P0-7 `ApplyAll` 无条件下发 `ApplyCharge` | 1 行改动 + 1 个测试 | 无 |
| 2 | P0-5 `TouchpadKey` 改为真正切换 | 10 行 + 真机验证 | 无 |
| 3 | P0-3/P0-4 飞行模式"写→回读→不一致就抛" | 30 行 + 确定可验证的回读源 | 需要确认 `GetWMI(121,26)` 是否可回读 |
| 4 | P0-6 显卡切换改成"先硬件、再落盘" | 15 行 | 无 |
| 5 | P0-1 T1/D1：**删掉**或**真正下发**（二选一，推荐删） | 删：`CurveSettings` 去掉 T1/D1 + UI 去掉节点 0 + `FanPage` 简化（约 60 行） | 需用户确认产品语义 |
| 6 | P0-2 过温提示（不加强制、只加告警）+ 曲线编辑器安全提示 | 60 行 | 需用户确认"只提示不强制" |

### 第二优先（P1，卸载场景 + 显示一致性相关）

1. P1-1 硬件锁超时抛异常 —— **同时把 `HardwareLock.cs:33` 的 `Monitor.Enter(_sync)` 改成 `Monitor.TryEnter(_sync, WaitMs)`**，让进程内锁与跨进程 Mutex 用同一个"超时就明确失败"的策略（原 P0-8 撤回后剩下的那条健壮性建议并入此处，见 §6.7）
2. P1-2/P1-3 配置同步（文件锁 + 内容哈希）
3. P1-4 页面缓存
4. P1-5 保存方案时的 Apply 语义
5. P1-6 充电档位不再猜
6. P1-7/P1-8 键盘灯能力判断 + 颜色确认
7. P1-9 Turbo Burst 原子化
8. P1-17 `HKClipSvc` 清理（加进 uninstall + 文档）

### 第三优先（P2）

日志分级、`ReadApp` 批量化、UI 轮询降频、死代码清理、测试补跨进程用例。

---

## 14. 测试建议（当前 33 个用例覆盖不到的地方）

| 需要新增的测试 | 覆盖的缺陷 |
| --- | --- |
| `ApplyAll_when_charge_disabled_still_sends_disable` | P0-7 |
| `ToggleAirplane_twice_returns_to_original_state`（用 FakeDchu 模拟注册表不更新） | P0-3 |
| `ToggleAirplane_throws_when_dll_fails` | P0-4 |
| `TouchpadKey_flips_the_state` | P0-5 |
| `GpuSwitch_failure_does_not_change_settings` | P0-6 |
| `HardwareLock_throws_instead_of_proceeding_when_busy`：一个线程持锁，另一个线程 `Enter()` 断言在 `WaitMs` 内抛 `TimeoutException`（而非静默进入临界区） | P1-1 / §6.7 |
| `RunMaintenance_does_not_leak_thread_pool_workers`：连续触发 60 次 `RunMaintenance`，断言进程 Thread 数不单调增长（验证改用 `System.Threading.Timer` 后的行为） | P2-6 |
| `ToWmi14` 的 round-trip 测试：`ReadDefaults` → 编辑 → `ToWmi14` → 用同一份字节 `ReadCurve` 读回，断言 T1/D1 相等的**预期行为**（现在是做不到的，测试会直接暴露 P0-1） | P0-1 |
| `SelectValue_keeps_the_firmware_value_when_not_in_preset` | P1-6 |
| `FileStamp_detects_a_same_length_same_tick_rewrite` | P1-3 |
| `UpdateSettings_rolls_back_when_save_fails` | P1-2 |
| **跨进程并发测试**：两个进程（或两个独立的 `SettingsStore` 实例 + 真文件）同时 `UpdateSettings`，断言两边的改动都在 | P1-2 |
| `HardwareLock_times_out_and_throws` | P1-1 |
| `LightPage_hides_when_not_rgb`（需要把页面逻辑抽到可测的 VM） | P1-7 |

---

## 15. 附录 A：实机现象与源码的对应关系

| 实机现象 | 源码对应 | 判定 |
| --- | --- | --- |
| 08:53:35–08:53:40 连续 6 次"设置已重新加载" | `Engine.cs:117` + `TrayApp.cs:162` 成对输出，6 次 = 6 次保存 | **不是回环**，是 6 次真实保存（P2 日志噪声） |
| 计划任务没注册，落到启动文件夹 | `install.ps1` 的 `schtasks /Create` 在非管理员下返回"拒绝访问"→ 走退路 | **设计如此**，非 bug（但有 P1-17 相关的清理缺口） |
| 当前设置曲线 40→95 °C 恒 25% | `settings.json` 的 `t2=78,d2=25,t3=95,d3=86` | **真实风险**，程序**无任何兜底**（§8） |

## 16. 附录 B：关键文件 → 关键行索引

| 缺陷 | 文件:行 |
| --- | --- |
| P0-1 T1/D1 不下发 | `src\HaseeCtl.Core\Features\FanCurve.cs:71-91`；`src\HaseeCtl.Core\Features\FanService.cs:105-113`；`src\HaseeCtl.App\Pages\FanPage.xaml.cs:61-80` |
| P0-2 无过温兜底 | `src\HaseeCtl.Core\Features\FanCurve.cs:32-53`；`src\HaseeCtl.Core\Config\Settings.cs:89-100` |
| P0-3 飞行模式读/写源不一致 | `src\HaseeCtl.Core\Features\SystemService.cs:15`、`:75-79`、`:82-90` |
| P0-4 飞行模式假成功 | `src\HaseeCtl.Core\Features\SystemService.cs:86-88`、`:129-132`；`src\HaseeCtl.Agent\TrayApp.cs:353-359` |
| P0-5 触控板 Fn 键不切换 | `src\HaseeCtl.Agent\TrayApp.cs:362-372`；对比 `src\HaseeCtl.Core\Features\SystemService.cs:26-36` |
| P0-6 显卡切换非原子 | `src\HaseeCtl.App\Pages\GpuPage.xaml.cs:63-88` |
| P0-7 关闭态充电不再下发 | `src\HaseeCtl.Core\Apply\Engine.cs:157`、`:223-230` |
| P1-1 硬件锁超时仍继续 | `src\HaseeCtl.Core\Apply\HardwareLock.cs:36-52` |
| P1-2 双进程整文件覆盖 | `src\HaseeCtl.Core\Config\SettingsStore.cs:180-208`；`src\HaseeCtl.Core\Apply\Engine.cs:126-143` |
| P1-3 FileStamp 不可靠 | `src\HaseeCtl.Core\Config\SettingsStore.cs:62-72`；`src\HaseeCtl.Agent\TrayApp.cs:162` |
| P1-4 切页丢草稿 | `src\HaseeCtl.App\MainWindow.xaml.cs:22-37`；`src\HaseeCtl.App\Pages\FanPage.xaml.cs:12-14`、`:27-48` |
| P1-5 保存方案不 Apply | `src\HaseeCtl.App\Pages\FanPage.xaml.cs:171-198`、`:145-156` |
| P1-6 充电档位猜错 | `src\HaseeCtl.App\Pages\BatteryPage.xaml.cs:56-61` |
| P1-7 键盘灯不判能力 | `src\HaseeCtl.App\Pages\LightPage.xaml.cs:44-79`；`src\HaseeCtl.Core\Apply\Engine.cs:234` |
| P1-8 色块点即生效 | `src\HaseeCtl.App\Pages\LightPage.xaml.cs:101-110`、`:119-127` |
| P1-9 Turbo Burst 锁提前释放 | `src\HaseeCtl.App\Pages\FanPage.xaml.cs:318-343` |
| P1-10 性能模式失败不回滚 | `src\HaseeCtl.App\Pages\PerformancePage.xaml.cs:64-77` |
| P1-11 强冷静默降级 | `src\HaseeCtl.Core\Features\PowerModeService.cs`（`SetTurboFan`） |
| P1-12 重载不广播 | `src\HaseeCtl.App\Pages\SettingsPage.xaml.cs:185-198` |
| P1-13 低刷新率不读回 | `src\HaseeCtl.App\Pages\SettingsPage.xaml.cs:117-131`；`src\HaseeCtl.Core\Features\SystemService.cs:116-120` |
| P1-14 WakeHook 钩子泄漏 | `src\HaseeCtl.Agent\WakeHook.cs`（`BeginDisarm`） |
| P1-15 UI 异常吞掉 | `src\HaseeCtl.App\App.xaml.cs`（`DispatcherUnhandledException`） |
| P1-16 驱动通道未验证（已降级为文档不准确） | `src\HaseeCtl.Core\Features\HotkeyListener.cs:29-47`、`:54-62`、`:206-233` |
| P2-22 运行时自包含性未检测 | `build.ps1`、`installer\install.ps1` |
| P2-23 DLL 不进仓库 | `native\README.md`、`src\HaseeCtl.Core\Features\SystemService.cs:82-90` |
| P1-17 HKClipSvc 孤儿 | `installer\uninstall.ps1`（缺失） |

---

*报告完。本次审查严格遵守约束：未修改 `F:\vibecode\REA_CON\HaseeCtl` 下任何文件，唯一写入为本文件。*
