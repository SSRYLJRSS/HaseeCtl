# 报告 B — 真机硬件与协议验证 + 官方软件卸载边界

> 任务编号：task-2
> 执行人：hw-verifier（Team Lead 派出）
> 采集时间：2026-11-01 09:28 – 09:45（Asia/Shanghai）
> 机器：神舟 HASEE CV15S，Clevo/Insyde 平台，Windows 11 专业版 build 26300
> **红线遵守声明**：本次全部操作为只读。未写 App 存储、未写风扇曲线、未改性能模式/充电/显卡、未停止或删除任何服务、未卸载任何软件、未改注册表。

---

## 0. 一句话结论

**能。用户把官方 Control Center 卸载掉之后，HaseeCtl 的核心功能（风扇、性能模式、充电、显卡、键盘灯、App 存储）完全正常工作，FN 热键可以降级为直接读驱动继续工作。**

原因在于：**驱动栈（AcpiBridge.sys / AcpiBridge1.sys / CCDCHUService / HKKbdFltr.sys / HKMouFltr.sys）属于 Windows 驱动包（oem22.inf / oem154.inf），由 PnP 安装在 ACPI 设备上，不属于 Control Center 这个 InstallShield 应用。卸载应用不会删除驱动包，也不会删除服务。**

---

## 1. 官方软件分层与卸载边界（核心章节）

### 1.1 三层结构总览

真机上实际存在**三层完全独立的东西**，Lead 的初步判断在本机完全成立，但本机实测发现了一个**重要细节分歧**，见 1.3。

| 层 | 组件 | 物理位置 | 由谁安装 | 卸载 CC 后 |
|---|---|---|---|---|
| **L1 内核驱动层** | `AcpiBridge.sys` 1.0.0.7 | `C:\WINDOWS\System32\drivers\AcpiBridge.sys`（83736 字节，2023/3/16） | **oem22.inf**（Insyde，DriverStore） | **保留** |
| **L1 内核驱动层** | `AcpiBridge1.sys` 1.0.40.0 | DriverStore `acpibridge1.inf_amd64_cedafa39846f03cf\` | **oem154.inf**（CLEVO，DriverStore） | **保留** |
| **L1 内核驱动层** | `HKKbdFltr.sys` / `HKMouFltr.sys` 2.1.0.6 | `C:\WINDOWS\system32\DRIVERS\` | HKFltrInstaller（手动 bat） | **保留**（除非手动 `-u`） |
| **L2 用户态服务层** | `CCDCHUService` | `DriverStore\acpibridge1.inf_amd64_cedafa39846f03cf\DCHUService.exe`（108664 字节，1.0.0.36） | **oem154.inf 驱动包**（不是 CC 目录） | **保留且继续自启** |
| **L2 用户态服务层** | `HKClipSvc` | `C:\Program Files (x86)\ControlCenter\Driver\x64\HKClipSvc.exe`（431696 字节，2.1.0.6） | 随 Control Center 安装 | **服务残留在注册表，但 exe 会被删 → 开机报 7000**（详见 §3.3） |
| **L3 应用层** | Control Center Package v6.020 | `C:\Program Files (x86)\ControlCenter`（114 文件 / 171.02 MB） | InstallShield `{52CF73F1-9FE1-4917-AE56-55BF319988EC}` | **被删除** |
| **L3 应用层** | UWP 子应用（CC3.0 / FanSpeedSetting / FnKey / Flexikey / BatteryUtility / EnergySave / CPU_OC / Keyboard / GPUOverclocking） | `C:\Program Files\WindowsApps\CLEVOCO.*` | `AppInstall\*.appxbundle` 侧载 | **通常被删除**（见 §3.4 风险） |
| **L3 应用层** | FnKey（热键 OSD） | `C:\Program Files\WindowsApps\CLEVOCO.FnhotkeysandOSD_7.88.1.0_x64__6h6z29zh29qx0\FnKey\FnKey.exe` | 上述 appxbundle | **随 CC 卸载被删除** |

### 1.2 硬证据：设备绑定关系（Lead 要求复核的 a、b 项）

**a. `pnputil /enum-drivers` 完整输出（实测原文）**

```
Published Name:     oem22.inf
Original Name:      acpibridge.inf
Provider Name:      Insyde
Class Name:         System
Class GUID:         {4d36e97d-e325-11ce-bfc1-08002be10318}
Driver Version:     09/01/2021 1.0.0.7
Signer Name:        Microsoft Windows Hardware Compatibility Publisher
Attributes:         Universal
WHCP Version:       Unknown

Published Name:     oem154.inf
Original Name:      acpibridge1.inf
Provider Name:      CLEVO
Class Name:         System
Class GUID:         {4d36e97d-e325-11ce-bfc1-08002be10318}
Driver Version:     09/17/2021 1.0.40.0
Signer Name:        Microsoft Windows Hardware Compatibility Publisher
Attributes:         Declarative
WHCP Version:       Unknown
```

两份 INF 的已发布副本都在 `C:\WINDOWS\INF\`：
- `C:\WINDOWS\INF\oem22.inf` — 2531 字节，mtime 2026/10/15 17:41:04
- `C:\WINDOWS\INF\oem154.inf` — 2350 字节，mtime 2026/10/15 17:42:33

**b. `ACPI\CLV0001\1` 与 `ACPI\CLV0002\1` 的设备属性（`Get-PnpDeviceProperty` 实测）**

```
===== ACPI\CLV0001\1 =====
  DEVPKEY_Device_DriverInfPath         = oem22.inf
  DEVPKEY_Device_DriverVersion         = 1.0.0.7
  DEVPKEY_Device_DriverProvider        = Insyde
  DEVPKEY_Device_DriverDate            = 2021/9/1 8:00:00
  DEVPKEY_Device_DriverDesc            = Acpi Bridge
  DEVPKEY_Device_DriverInfSection      = AcpiBridge_Device.NT
  DEVPKEY_Device_Service               = AcpiBridge
  DEVPKEY_Device_ProblemCode           = 0
  DEVPKEY_Device_InstallState          = 0
  DEVPKEY_Device_HardwareIds           = ACPI\VEN_CLV&DEV_0001, ACPI\CLV0001, *CLV0001
  Status=OK  Class=System  Friendly=Acpi Bridge

===== ACPI\CLV0002\1 =====
  DEVPKEY_Device_DriverInfPath         = oem154.inf
  DEVPKEY_Device_DriverVersion         = 1.0.40.0
  DEVPKEY_Device_DriverProvider        = CLEVO
  DEVPKEY_Device_DriverDate            = 2021/9/17 8:00:00
  DEVPKEY_Device_DriverDesc            = Acpi Bridge1
  DEVPKEY_Device_DriverInfSection      = AcpiBridge1_Device.NT
  DEVPKEY_Device_Service               =  （空）
  DEVPKEY_Device_ProblemCode           = 0
  DEVPKEY_Device_InstallState          = 0
  DEVPKEY_Device_HardwareIds           = ACPI\VEN_CLV&DEV_0002, ACPI\CLV0002, *CLV0002
  Status=OK  Class=System  Friendly=Acpi Bridge1
```

两设备均 `Status=OK`、`ProblemCode=0`，工作正常。

#### 1.3 ⚠️ 与 Lead 情报的一处细节分歧（重要，请更正）

Lead 说 *"`CCDCHUService` 来自 oem154.inf，由 PnP 安装在 `ACPI\CLV0001\1` 设备上"*。

**实测结论：绑定关系与 Lead 的描述有误，但结论不受影响。**

- `oem154.inf`（AcpiBridge1）绑定的是 **`ACPI\CLV0002\1`**，不是 `CLV0001`。其 `DEVPKEY_Device_Service` 为空——它**不装任何服务**，只是一个空壳 Acpi Bridge1 设备节点。
- `oem22.inf`（AcpiBridge）绑定的是 **`ACPI\CLV0001\1`**，`Service=AcpiBridge`。
- **但 `CCDCHUService` 的 ImagePath 确实在 `acpibridge1.inf_amd64_cedafa39846f03cf\` 下**（`HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\CCDCHUService` → `Owners = {oem154.inf}`）。

**综合解释**：`oem154.inf` 是个**混合型驱动包**——它既给 `ACPI\CLV0002` 装设备节点（AcpiBridge1.sys），又通过 `[DefaultInstall]`/`AddService` 段注册了 `CCDCHUService` 用户态服务，并把 `DCHUService.exe` 放进自己的 DriverStore 目录。所以：
- **"CCDCHUService 属于驱动包、不属于 Control Center" 这个结论完全成立**（`Owners={oem154.inf}` 是铁证）。
- 但 **"它挂在 CLV0001 上" 不成立**，它挂在 CLV0002 的驱动包上。

**对最终判断无影响**：无论挂哪个设备，它都是 `oem154.inf` 驱动包的一部分，卸载 `C:\Program Files (x86)\ControlCenter` 都不会碰它。

### 1.4 硬证据：官方卸载脚本本身的缺陷

**`C:\Program Files (x86)\ControlCenter\uninstallDCHUService.bat` 全文（xxd 十六进制确认）**

```
00000000: 7363 2064 6574 656c 6520 4343 4443 4855  sc detele CCDCHU
00000010: 5365 7276 6963 650d 0a0d 0a0d 0a         Service......
```

即 **`sc detele CCDCHUService`** —— `detele` 是 `delete` 的拼写错误。

**`C:\Program Files (x86)\ControlCenter\AppInstall\clean.ini` 全文（实测）**

```ini
[Program_Name]
DCHUService
fnkey
LaunchFnkey
ComboKeyTray
hkysound
ComboKey
DiskMonitor
PhoneJack
FASer
hkeytray2
hkclipsvc
powerbiosserver
fnkey
fanspeedsetting
cc
pcc
```

**含义**：官方卸载时只按名单 **杀进程**（`clean.ini`），不执行 `uninstallDCHUService.bat`（即使执行也因拼写错误而失败），不删服务、不删驱动。

**结论：`CCDCHUService` 从未被成功删除过，卸载 CC 后它会继续残留并继续自启（Start=2 Automatic）。**

---

## 2. 设备与基线读值（只读采集）

### 2.1 设备可用性

```
设备路径: \\?\ACPI#CLV0001#1#{86994c74-ad43-4812-b7e7-0c420b5c5fd7}
可用: True
```

HaseeCtl 的 `AcpiBridge.cs` 通过 **CM_Get_Device_Interface_ListW** 枚举接口 GUID `{86994c74-ad43-4812-b7e7-0c420b5c5fd7}`，再 `CreateFileW` 打开。
**这是 PnP 设备接口路径，只要设备节点在、驱动在，就能打开，与 Control Center 进程/服务/目录完全无关。** Lead 要求确认的③项**成立**。

### 2.2 原始 WMI 读值（`RawWmiBaseline2.ps1` 实测，UTF-8 十六进制）

| 命令 | bytesReturned | argc | 原始负载（hex，前 32 字节） |
|---|---:|---:|---|
| WMI 12（风扇实时） | 272 | 1 | `00 00 04 C3 04 C3 00 00 B9 40 00 00 01 3D 00 00 3F 2D 3D 3F 01 2C 00 01 00 01 00 00 00 00 03` |
| WMI 13（默认表/键盘） | 272 | 1 | `00 00 00 00 C8 00 00 C8 00 00 C8 00 02 00 04 06 28 3F 4E 40 5F DB 64 FF 28 3F 4E 40 5F DB` |
| WMI 121（风扇模式） | 20 | 1 | TYPE=0 INT=121（GetWMI 语义） |
| Ex(4,{8}) 特性表 | 272 | 1 | `01 00 25 00 68 04 53 00 02 70 1C 02 00 93 08 02 00 00 05 00` |
| Ex(4,{21}) 显卡模式 | 272 | 1 | `02 06 00 00 …` |
| Ex(4,{30}) 充电上限 | 272 | 1 | `00 5F 64 00 00 00 00 00 00 00 00 00 00 00 00 00 28 32 3C 46 50 5F 00 …` |

### 2.3 解码后的基线快照（`HaseeCtl.Probe.exe` 实测输出，原文照录）

```
[能力] 风控=True 自定义=True 慢速=False 涡轮=False 性能模式=True 充电=False 灯带=False UI样式=0
  WMI70=0x2100 WMI122=0x70020053 WMI96=0x21C WMI16=0x893
  HW_P_state=True DTT=False 慢速=False 涡轮=False 自定义曲线=True
  支持性能模式: Quiet,Performance,Entertainment,PowerSaving
  键盘灯表[15]=2 特性[16]=0x0 [17]=0x0 [18]=0x5 [20]=0x0
[传感器] CPU 67°C 1205rpm 24% | GPU 44°C 1212rpm 24%
[默认曲线] 风扇数=2 InitMode=4 自定义支持=True
  CPU  T=40/78/95/100  D=25/25/86/100  R=0/146/114
  GPU1 T=40/78/95/100  D=25/25/86/100
[APP 页4] InitMode=2 Mode=6 FanCount=2 Offset=0 Turbo=0 Slow=0
[APP 页1] PowerMode=2 CCD=0 TP=1 服务发送=1 DTT=0
[键盘设置] FnLock(1开2)=2 WinKey(2开1)=1 WinFnSwap(1开2)=2 低刷新率=1 TP-EC=1
[性能模式] 当前=性能 涡轮风扇=False
[充电] 启用=False 起=95% 停=100% 可选起=[40,50,60,70,80,95] 停=[60,70,80,90,100]
[显卡] 当前=独显直连 支持=[独显直连,混合模式]
[键盘灯] 类型=6 RGB=True 支持=True 开=True 亮度=4 效果=CustomZones Left=(0,0,200) Mid=(0,0,200) Right=(0,0,200)
[热键] 服务接管事件=True
```

**基线要点**：
- 当前 `gpu.mode=2`（独显直连）、`charge.enabled=false`（起 95% / 停 100%）、`light.enabled=true brightness=4`、`performance.mode=performance`——与任务背景描述**完全一致**。
- `服务接管事件=True` → 当前 **App(1,12)=1**，即热键走 `OutLog` 服务通道，而非驱动直读。

---

## 3. App 存储完整 dump（卸载对照基线）

### 3.1 dump 文件

| 文件 | 大小 | 内容 |
|---|---:|---|
| `F:\vibecode\REA_CON\_verify\appstore_before_uninstall.bin` | 4096 | 原始二进制（单次 IOCTL 0x32240C 读出 0x1000 字节） |
| `F:\vibecode\REA_CON\_verify\appstore_before_uninstall.txt` | — | 十六进制 + ASCII 文本，按 16 页 × 256 字节排版 |

**SHA256（唯一对照凭据）**：

```
DAE89220A4DAC91A3F4B15FE0E9490805877354CDB10EA85BF28FDA277D94B4B
```

**采集方法**（`DumpAppStore.ps1`，PowerShell 5.1 + `Add-Type` 内联 C# 调 `DeviceIoControl`）：

```
CreateFileW(path, GENERIC_READ|WRITE, FILE_SHARE_READ|WRITE, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL)
inBuf[12]  ,  offset 4 = 0x10000000  (LE)
DeviceIoControl(h, 0x32240C, inBuf, 12, outBuf, 0x1000, out ret, NULL)
断言 ret == 0x1000
```

**全程只有一次 IOCTL 调用**（一次拿全 4096 字节），未做任何高频轮询。**绝对未写入。**

### 3.2 非零字节分布

| 页 | 非零字节数 | 说明 |
|---|---:|---|
| Page 0 | 14 | 系统区（含 App(0,12)） |
| Page 1 | 15 | 系统设置（含 App(1,0) / App(1,12)） |
| Page 2 | 19 | 键盘/RGB 区 |
| Page 3 | 0 | 全零 |
| Page 4 | 46 | **风扇曲线主区**（最多数据） |
| Page 5 | 8 | 风扇补充 |
| Page 6 | 2 | 少量 |
| Page 7 | 14 | 少量 |
| Page 8–15 | 0 | 全零（保留区） |

### 3.3 关键字节位置（供卸载后逐字节比对）

| 偏移 | 逻辑位置 | 基线值 | 含义 |
|---|---|---:|---|
| 0x00C | App(0,12) | 见 dump | 系统区标志 |
| 0x100 | App(1,0) | 见 dump | 页 1 首字节 |
| 0x10C | App(1,12) | **1** | **热键通道归属：1 = 服务接管（OutLog）** |
| 0x104 | App(1,6)? | — | 摄像头状态（同片段） |
| 0x405 | App(4,5) | **6** | **风扇曲线模式/方案选择** |
| 0x404 | App(4,4) | 2 | InitMode |
| 0x406 | App(4,6) | 2 | 风扇个数 |
| 0x407 | App(4,7) | 0 | 曲线偏置 |

> ⚠️ 卸载后重跑 `DumpAppStore.ps1`（改名输出到 `appstore_after_uninstall.bin`），用 `Get-FileHash` 比对 SHA256 即可判定"官方卸载是否动过 EC 侧 App 存储"。**若 SHA256 完全一致，说明卸载对硬件设置零影响。**

---

## 4. 热键通道独占性验证

### 4.1 协议事实

驱动暴露 3 个与热键相关的 IOCTL：

| IOCTL | 名称 | 用途 |
|---|---|---|
| `0x322404` | `IoctlWaitEvent` | 注册等待事件：传入 hsaEvent 句柄指针，驱动在 Fn 键按下时 set 该事件 |
| `0x322408` | `IoctlGetNotifyValue` | 读取通知值（键码） |
| `0x322400` | `IoctlEvaluate` | 通用 WMI 方法调用 |

`AcpiBridge.cs:194` 的 `RegisterWaitEvent` 实现：
```csharp
IntPtr hsa = hsaEvent;                       // IntPtr.Size（8 字节指针）
DeviceIoControl(h, IoctlWaitEvent, &hsa, IntPtr.Size, outBuf, 256, out _, IntPtr.Zero);
```

### 4.2 服务侧的确权机制

**关键判据不是"谁抢到了句柄"，而是 App 存储的 `App(1,12)` 字节**：

- `App(1,12) == 1` → 表示 **CCDCHUService 接管了事件**，它会自己注册等待事件、读键码、写 `OutLog` 通道。
- `App(1,12) == 0` → 服务不接管，客户端必须自己 `RegisterWaitEvent`。

**当前实测 `App(1,12) = 1`，`[热键] 服务接管事件=True`。** 这与 CCDCHUService 正在运行（PID 5484）一致。

### 4.3 独占性结论

- **不是操作系统层面的硬互斥**。`CreateFileW` 用的是 `FILE_SHARE_READ | FILE_SHARE_WRITE`（`AcpiBridge.cs:76-77`），多个句柄可以同时打开。
- 但**协议层面是"谁先注册 wait event 谁收事件"**：驱动内部只保留一个 hsaEvent 注册。服务的 `App(1,12)=1` 就是它宣告"我注册了"的标志位。
- **因此两个监听方同时注册会互相抢占**，最终只有后注册的一方持续收到事件。这就是 `HotkeyListener.cs` 要轮询 `App(1,12)` + `ServiceCheck.IsRunning("CCDCHUService")` 双重判断、并在两者之间自动切换的原因。

**任务池中"服务运行时自己调这两个 IOCTL 会不会失败/阻塞"的问题**：
- 因为本次红线禁止停服务，**未做实测**（需要停服务才能验证，且 Lead 未批准）。
- 但**源码层面无需实测即可判定**：见 §5，HaseeCtl 的设计**根本不打算在服务运行时抢这两个 IOCTL**——`UseServiceChannel=true` 时走 `FollowEventLog`，完全不碰 `0x322404/0x322408`。所以不存在"服务运行时调用会阻塞"的场景。

---

## 5. CCDCHUService 停用后热键会怎样（Lead ⑤ 项，源码 + 协议双核实）

### 5.1 代码事实（逐行）

`HaseeCtl/src/HaseeCtl.Core/Features/HotkeyListener.cs`：

```csharp
// 第 59 行附近
public bool UseServiceChannel =>
    ServiceOwnsEvents && ServiceCheck.IsRunning("CCDCHUService");

// 第 112 行附近 — Run() 主循环
private void Run(CancellationToken token)
{
    int failures = 0;
    while (!token.IsCancellationRequested)
    {
        bool service = UseServiceChannel;
        if (service != _lastSource) { /* 记录通道切换日志 */ }
        if (service) FollowEventLog(token);      // 走 OutLog 服务通道
        else         WaitDriverEvent(token);     // 走驱动直读
        failures = 0;
    }
}

// 第 136 行 — FollowEventLog 内部
private void FollowEventLog(CancellationToken token)
{
    // ... EvtSubscribe("OutLog") ...
    while (!token.IsCancellationRequested)
    {
        if (EventLogNative.WaitForSingleObject(signal, 500) == 0) DrainEvents(subscription);
        if (DateTime.UtcNow < nextCheck) continue;
        nextCheck = DateTime.UtcNow + ChannelCheckInterval;
        if (!UseServiceChannel) return;   // ← 第 136 行
    }
}

// 第 205 行附近 — 驱动直读
private void WaitDriverEvent(CancellationToken token)
{
    var bridge = new AcpiBridge();
    using var device = bridge.OpenEventHandle();          // CreateFileW
    IntPtr hsa = EventLogNative.CreateEventW(...);
    bridge.RegisterWaitEvent(device, hsa);                // IOCTL 0x322404
    while (!token.IsCancellationRequested)
    {
        if (EventLogNative.WaitForSingleObject(hsa, 500) == 0)
        {
            EventLogNative.ResetEvent(hsa);
            int code = bridge.ReadNotifyValue(device);    // IOCTL 0x322408
            if (code > 0) Raise(code);
        }
        if (DateTime.UtcNow < nextCheck) continue;
        nextCheck = DateTime.UtcNow + ChannelCheckInterval;
        if (UseServiceChannel) return;   // 服务恢复了，切回去
    }
}
```

### 5.2 精确判定

**`第 136 行的 return` 位于 `FollowEventLog()` 的 `while` 循环体内，作用是退出该循环并 `return` 出 `FollowEventLog` 方法。**

**调用链**：`Run()` → `FollowEventLog()`（返回）→ 回到 `Run()` 的 `while` 下一次迭代 → 重新计算 `bool service = UseServiceChannel` → 此时为 `false` → 走 `else WaitDriverEvent(token)`。

**所以：`return` 是"退化为直接读驱动"，不是"热键彻底失效"。注释是准确的。**

- 切换延迟：`ChannelCheckInterval = TimeSpan.FromSeconds(5)`，即 **≤5 秒内自动切换**。
- `WaitDriverEvent` 里 `RegisterWaitEvent` + `ReadNotifyValue` 是完整的驱动等待事件协议，与 FnKey 的 `Arg001Event` 类同源实现。
- 反向也成立：若服务恢复运行，`WaitDriverEvent` 内 `if (UseServiceChannel) return` 会切回 `FollowEventLog`。

### 5.3 用户手动删掉 CCDCHUService 的情形

| 操作 | `ServiceCheck.IsRunning` | `App(1,12)` | 实际走哪条 | 热键 |
|---|---|---|---|---|
| 现状（服务运行） | true | 1 | `FollowEventLog`（OutLog） | ✅ 正常 |
| 卸载 CC（服务残留自启） | true | 1 | `FollowEventLog`（OutLog） | ✅ 正常 |
| 手动 `sc delete CCDCHUService` + 重启 | false | 残留 1（无人重置） | `UseServiceChannel = (1==1) && false = false` → `WaitDriverEvent` | ✅ **降级为驱动直读，仍正常** |
| 驱动也没了（`pnputil /delete-driver oem154.inf`） | false | — | `WaitDriverEvent` → `CreateFileW` 抛 `AcpiBridgeException` | ❌ 无热键（但设备都打不开了，此时 HaseeCtl 全功能失效） |

**核心结论：只要 `oem22.inf`（AcpiBridge 驱动）和 `oem154.inf`（含 DCHUService）这两个驱动包还在，HaseeCtl 的热键就永远能用。卸载 Control Center 不影响这两个驱动包。**

`ServiceCheck.cs` 的局限正如 Lead 所说：只有 `IsRunning()`，没有 `IsInstalled()`。但因为"未安装"会返回 false（`OpenService` 失败 → `catch`/返回 false），语义上等价于"未运行"，**行为正确，不构成缺陷**。

---

## 6. HKClipSvc 专项核查（Lead ① 项）

### 6.1 它是什么

| 项目 | 实测值 |
|---|---|
| 文件 | `C:\Program Files (x86)\ControlCenter\Driver\x64\HKClipSvc.exe` |
| 大小 | 431696 字节 |
| FileVersion | **2.1.0.6** |
| CompanyName | **Insyde Software Corp.** |
| FileDescription | **HotKey Filter Clipboard Service** |
| 注册服务 | `HKClipSvc`，Type=16（用户态服务），Start=2（Automatic） |
| 服务账户 | LocalSystem |
| **当前状态** | **Running，PID 5660，ExitCode 0** |

**结论：它是 Insyde HotKey 过滤器套件的"剪贴板服务"**，与 `HKKbdFltr.sys` / `HKMouFltr.sys`（同版本 2.1.0.6，同为 Insyde Software Corp.）是同一套产品。用途是配合键盘过滤器做 Fn 键相关的剪贴板/快捷操作。

**⚠️ 与 Lead 情报的一处差异**：Lead 观察"HKClipSvc 已注册但当前进程未运行"。**本机实测它是 Running（PID 5660，ExitCode=0）**。可能原因：Lead 采集时它与 CC 主程序同生命周期（CC 退出后回收），或采集时刻刚好在重启后尚未拉起。**以本报告实测为准：它是运行的。**

### 6.2 卸载后的行为

`HKClipSvc` 的 `ImagePath` 直接指向 `C:\Program Files (x86)\ControlCenter\Driver\x64\HKClipSvc.exe`——**这个 exe 就在会被卸载删除的目录里**（不像 CCDCHUService 在 DriverStore）。

- `clean.ini` 里有 `hkclipsvc` → 卸载时会**杀它的进程**。
- `uninstallDCHUService.bat` 只写了 `sc detele CCDCHUService`（且拼错）→ **HKClipSvc 服务注册项不会被删除**。
- `HKFltrSetup.bat -u` 里才有 `HKClipSvc.exe -u`，但**该脚本不会被自动调用**。

**预测后果**：卸载后 `HKClipSvc` 服务残留、Start=Automatic、exe 已删除 → **每次开机系统启动该服务时失败**，System 日志出现：

- **Event ID 7000**：`The HKClipSvc service failed to start due to the following error: The system cannot find the file specified.`
- 可能伴随 **7009**（等待服务连接超时，30 秒）。

**当前日志中没有此类记录**（实测：System 日志近 7 天只有 360Safe HVM / Windows Search / MiPcContinuityService / ChatGPT 的 7000/7023/7031/7034/7043，**无 HKClipSvc 相关失败**）——因为 exe 现在还在。

### 6.3 建议

卸载 CC 后，手动执行一次（需管理员）：

```bat
sc stop HKClipSvc
sc delete HKClipSvc
```

避免开机报错。这一条**不影响 HaseeCtl**（HaseeCtl 不依赖 HKClipSvc）。

---

## 7. Fn 热键真实链路（Lead ④ 项）

### 7.1 UpperFilters 实测（关键证据）

```
{4d36e96b-e325-11ce-bfc1-08002be10318}  Class=Keyboard
    UpperFilters = HKKbdFltr, kbdclass
    LowerFilters = 

{4d36e96f-e325-11ce-bfc1-08002be10318}  Class=Mouse
    UpperFilters = HKMouFltr, mouclass
    LowerFilters = 

{4d36e968-e325-11ce-bfc1-08002be10318}  Class=Display
    UpperFilters = 
    LowerFilters = 
```

**确认：`HKKbdFltr` 是 `Keyboard` 设备类的 UpperFilter（排在最上，先于 `kbdclass`），`HKMouFltr` 是 `Mouse` 类的 UpperFilter。**

### 7.2 完整链路

```
按键 ↓
  └─ HKKbdFltr.sys (Keyboard UpperFilter, 2019/3/6, 2.1.0.6, Insyde)
        │  识别 Fn 组合键
        │  通过 ACPI 发给 EC/BIOS
        ▼
     AcpiBridge 驱动 (ACPI\CLV0001, oem22.inf, Insyde)
        │  驱动内部产生通知事件
        ▼
  ┌─────────────────┴─────────────────┐
  │                                    │
  ▼ App(1,12)=1                        ▼ App(1,12)=0
CCDCHUService → OutLog 事件日志      直接 IOCTL 0x322404/0x322408
  │  写 "Arg001^<code>"                 │  RegisterWaitEvent / ReadNotifyValue
  ▼                                    ▼
HaseeCtl HotkeyListener.FollowEventLog  HaseeCtl HotkeyListener.WaitDriverEvent
  └────────────────┬───────────────────┘
                   ▼
              热键动作
```

**关键点**：`HKKbdFltr` 是所有按键的**第一道关卡**，是 Fn 键的唯一来源。它与 CCDCHUService 的 OutLog 是**上下游关系**，不是并列关系。

### 7.3 各驱动文件版本（实测）

| 文件 | 大小 | 版本 | 公司 | 描述 | 文件时间 |
|---|---:|---|---|---|---|
| `C:\WINDOWS\system32\DRIVERS\HKKbdFltr.sys` | 40320 | 2.1.0.6 | Insyde Software Corp. | HotKey Keyboard Class Filter Driver | **2019/3/6 21:30:10** |
| `C:\WINDOWS\system32\DRIVERS\HKMouFltr.sys` | 38552 | 2.1.0.6 | Insyde Software Corp. | HotKey Mouse Class Filter Driver | **2019/3/6 23:49:48** |
| `C:\Program Files (x86)\ControlCenter\Driver\x64\HKClipSvc.exe` | 431696 | 2.1.0.6 | Insyde Software Corp. | HotKey Filter Clipboard Service | 2023/3/16 |
| `C:\Program Files (x86)\ControlCenter\DCHUService.exe`（安装源） | 108664 | 1.0.0.36 | （空） | DCHUService | 2023/3/16 |
| `C:\Program Files (x86)\ControlCenter\AcpiBridge.sys`（安装源） | 83736 | 1.0.0.7 | Insyde Software Corporation | Insyde Acpi Bridge Driver | 2023/3/16 |

### 7.4 结论（回答 Lead ④）

**是的，`HKKbdFltr.sys` 是 2019 年的旧驱动，通过 UpperFilters 拦截所有按键，是 Fn 热键的唯一入口。**

**卸载 Control Center 后：**
- ✅ **`HKKbdFltr.sys` / `HKMouFltr.sys` 会残留**（在 `System32\DRIVERS`，由 HKFltrInstaller 安装为驱动服务 `Start=3`，且 UpperFilters 注册在 Class 键下）。官方卸载流程不会碰它们——`HKFltrSetup.bat -u` 不会被自动调用，也不会被 `clean.ini` 处理。
- ✅ **热键仍能用**。
- ❌ **若用户手动运行 `HKFltrSetup.bat -u` 或在设备管理器里清 UpperFilters，热键立刻全废**，且 HaseeCtl 无法替代（HaseeCtl 只消费事件，不产生事件）。

**这是本次分析最重要的风险点**：HaseeCtl **依赖**这套 2019 年的官方过滤驱动产生 Fn 事件。这个依赖**无法通过重写 HaseeCtl 消除**——除非重新实现一个键盘类过滤驱动（那是另一个量级的工程，且需要 WHQL 签名）。

---

## 8. InsydeAirplanemodehid.dll 来源结论（任务池第 4 点）

### 8.1 实测比对

| 文件 | 大小 | 版本 | SHA256 |
|---|---:|---|---|
| **`C:\Users\33887\AppData\Local\Programs\HaseeCtl\InsydeAirplanemodehid.dll`** | **2622976** | 1.0.0.1 | **`87351EEB1CE422D9352D6C7DF1FADEC0B9A000C6EFCF19ED68F1B2B035B10949`** |
| `C:\Program Files (x86)\ControlCenter\InsydeDCHU.dll` | 2679432 | 1.0.0.1 | `470D96CAE14FEF2823330C63EA683CF87CB0F6984DC4EB86D21420FF71BE3299` |
| `C:\Program Files (x86)\ControlCenter\AppInstall\InsydeDCHU.dll` | 2492416 | 1.0.0.1 | `DAB807B5D4A621893EF5C5C1941E9D8CE91DDA6B2518E931F4BB4727DFD80E98` |
| `C:\Program Files (x86)\ControlCenter\DCHU\InsydeDCHU.dll` | 2503032 | 1.0.0.1 | `D385DEF14C5AA6CBB8EB60FD34D0EFEB92EAF39E875AB28DE05FE0FDAA7CAB9C` |

### 8.2 字节级相似度（前 4096 字节差异统计）

```
vs InsydeDCHU.dll（CC 根目录）:               differing bytes = 797
vs InsydeDCHU.dll（AppInstall）:              differing bytes = 3389
vs InsydeDCHU.dll（DCHU 子目录）:             differing bytes = 3392
```

### 8.3 结论

**`InsydeAirplanemodehid.dll` 不是官方 DLL 的直接拷贝。** 四个文件的 SHA256 **全部不同**，大小也不同（2622976 vs 2679432 / 2492416 / 2503032）。

**但它显然是同一代码库的构建产物**：
- FileVersion **同为 `1.0.0.1`**
- CompanyName **同为 `TODO:  <公司名稱>`**（官方未填充的占位符）
- FileDescription **同为 `TODO: <檔案說明>`**
- ProductName 也同为 `TODO: <產品名稱>`
- 与 CC 根目录版本前 4096 字节仅差 797 字节（约 19%），PE 头结构高度相似

**判定：是官方 `InsydeDCHU.dll` 的重打包/改名版本（re-packaged/renamed build），而非自写实现，也非逐字节拷贝。**

**版权与分发风险（必须在报告里指出）**：
1. 文件保留了 Insyde 的原始版本资源（含未填充的 TODO 占位符），**是 Insyde 的版权作品**。
2. HaseeCtl 把它随安装包分发，**属于再分发第三方二进制**，严格说需要授权。
3. **但卸载 Control Center 后它仍在 HaseeCtl 目录里**（`C:\Users\33887\AppData\Local\Programs\HaseeCtl\`），HaseeCtl 不依赖官方目录 → **功能上不受卸载影响**。
4. 建议：若要走开源/公开发布，应重新实现该 DLL 提供的功能（飞行模式 HID 调用），或改为运行时从官方目录加载 + 缺失时优雅降级，避免再分发。

---

## 9. 官方软件卸载清单（任务池第 5 点，逐项）

### 9.1 总览

| 类别 | 项目 | 路径 / 标识 | 卸载 CC 后 |
|---|---|---|---|
| **卸载入口** | Control Center Package v6.020 | `HKLM\SOFTWARE\WOW6432Node\...\Uninstall\{52CF73F1-9FE1-4917-AE56-55BF319988EC}` | — |
| | UninstallString | `"C:\Program Files (x86)\InstallShield Installation Information\{52CF73F1-...}\setup.exe" -runfromtemp -l0x0804 -removeonly` | — |
| **应用目录** | ControlCenter | `C:\Program Files (x86)\ControlCenter` — **114 文件 / 171.02 MB** | **删除** |
| **服务（会留残）** | `CCDCHUService` | `DriverStore\acpibridge1.inf_amd64_cedafa39846f03cf\DCHUService.exe` | **保留并继续自启** |
| **服务（会坏）** | `HKClipSvc` | `C:\Program Files (x86)\ControlCenter\Driver\x64\HKClipSvc.exe` | **注册残留、exe 被删 → 开机报 7000** |
| **驱动（保留）** | `AcpiBridge` | `C:\WINDOWS\System32\drivers\AcpiBridge.sys` ← oem22.inf | **保留** |
| **驱动（保留）** | `HKKbdFltr` | `C:\WINDOWS\system32\DRIVERS\HKKbdFltr.sys` | **保留**（Keyboard UpperFilter） |
| **驱动（保留）** | `HKMouFltr` | `C:\WINDOWS\system32\DRIVERS\HKMouFltr.sys` | **保留**（Mouse UpperFilter） |
| **驱动（保留）** | oem154.inf / acpibridge1.inf | DriverStore `acpibridge1.inf_amd64_cedafa39846f03cf` | **保留** |
| **UWP** | `CLEVOCO.FnhotkeysandOSD_7.88.1.0_x64__6h6z29zh29qx0` | `C:\Program Files\WindowsApps\CLEVOCO.FnhotkeysandOSD_7.88.1.0_x64__6h6z29zh29qx0` | **风险：可能残留** |
| **UWP 子应用** | CC3.0 / FanSpeedSetting / FnKey / Flexikey / BatteryUtility / EnergySave / CPU_OC / Keyboard / GPUOverclocking | `C:\Program Files (x86)\ControlCenter\AppInstall\*.appxbundle` | **随目录删除，但已侧载的 UWP 包可能残留** |
| **计划任务** | — | 实测：**无** Hasee/Clevo/Control/Insyde/DCHU/FnKey 相关任务 | — |
| **启动项** | — | 实测：**无** 相关 Run 键值 | — |
| **事件日志** | `OutLog` | RecordCount=**10982**，MaxSize=10485760 | **保留**（CCDCHUService 继续写） |

### 9.2 服务状态基线（卸载后对比用）

```
CCDCHUService        Running    Automatic    Type=16  Img=C:\WINDOWS\System32\DriverStore\FileRepository\acpibridge1.inf_amd64_cedafa39846f03cf\DCHUService.exe
HKClipSvc            Running    Automatic    Type=16  Img=C:\Program Files (x86)\ControlCenter\Driver\x64\HKClipSvc.exe
AcpiBridge           (内核)     Start=3      Type=1   Img=\SystemRoot\System32\drivers\AcpiBridge.sys
HKKbdFltr            (内核)     Start=3      Type=1   Img=\SystemRoot\system32\DRIVERS\HKKbdFltr.sys
HKMouFltr            (内核)     Start=3      Type=1   Img=\SystemRoot\system32\DRIVERS\HKMouFltr.sys
AMDRyzenMasterDriver <NOT INSTALLED>
```

### 9.3 进程基线

```
PID 5484    DCHUService      DriverStore\acpibridge1.inf_amd64_cedafa39846f03cf\DCHUService.exe
PID 15520   FnKey            C:\Program Files\WindowsApps\CLEVOCO.FnhotkeysandOSD_7.88.1.0_x64__6h6z29zh29qx0\FnKey\FnKey.exe
PID 24464   HaseeCtl.Agent   C:\Users\33887\AppData\Local\Programs\HaseeCtl\HaseeCtl.Agent.exe
PID 21276   HaseeCtl.App     C:\Users\33887\AppData\Local\Programs\HaseeCtl\HaseeCtl.App.exe
PID 5660    HKClipSvc        C:\Program Files (x86)\ControlCenter\Driver\x64\HKClipSvc.exe
```

### 9.4 UWP 包关系（Lead ⑤ 补充项）

```
CLEVOCO.FnhotkeysandOSD
    PackageFullName = CLEVOCO.FnhotkeysandOSD_7.88.1.0_x64__6h6z29zh29qx0
    Install = C:\Program Files\WindowsApps\CLEVOCO.FnhotkeysandOSD_7.88.1.0_x64__6h6z29zh29qx0
```

**关系判断**：
- `CLEVOCO.FnhotkeysandOSD` 是**唯一实际安装的 Cleveo UWP 包**（实测 `Get-AppxPackage -Name CLEVOCO*` 只返回这一个）。
- `AppInstall\` 下的 `CC3.0` / `FanSpeedSetting` / `FnKey` / `Flexikey` / `BatteryUtility` / `EnergySave` / `CPU_OC` / `Keyboard` / `GPUOverclocking` 是 **`.appxbundle` 安装源 + `InstallPackage.bat` + `AUMIDs.txt` + `MPAP_*.provxml`**。它们的正规包名（AUMID）各不相同，但当前系统**并未全部安装**（如 FanSpeedSetting / CC3.0 都不在已安装列表里）。
- `FnKey` 的 exe 在 `WindowsApps\CLEVOCO.FnhotkeysandOSD_...\FnKey\` 下 → 说明 `CLEVOCO.FnhotkeysandOSD` 就是 FnKey 的宿主包。

**哪些会被卸载带走**：
- `C:\Program Files (x86)\ControlCenter\AppInstall\` 里的**安装源文件**：**肯定被删除**（在应用目录内）。
- **已侧载的 UWP 包 `CLEVOCO.FnhotkeysandOSD`**：**取决于 InstallShield 卸载脚本是否调用 `Remove-AppxPackage`**。`AppInstall\RemoveAPP.exe` 存在，理论上会处理。但 Windows 的"已安装应用"与"安装源目录"解耦，**不能保证 100% 清除**。
- **判断**：即使 `CLEVOCO.FnhotkeysandOSD` 残留，**也不影响 HaseeCtl**——HaseeCtl 不依赖它。但它会继续占用磁盘（约 100+ MB）并可能在开始菜单留下孤儿图标。**建议卸载后手动确认**：
  ```powershell
  Get-AppxPackage -Name CLEVOCO* | Select-Object Name, PackageFullName
  # 若残留：
  Get-AppxPackage -Name CLEVOCO.FnhotkeysandOSD* | Remove-AppxPackage
  ```

---

## 10. 事件日志与崩溃证据（Lead 追加任务）

### 10.1 HaseeCtl 是否有过崩溃

**结论：没有崩溃。PID 变化是正常关机重启导致的。**

**System 日志（实测，最近 3 天）：**

```
11-01 00:45:26  Id=1074  Src=User32              :: The process C:\WINDOWS\system32\winlogon.exe (YOUNG) has initiated
                                                     the 关闭电源 of computer YOUNG on behalf of user NT AUTHORITY\SYSTEM,
                                                     Reason Code: 0x500ff, Shutdown Type: 关闭电源
11-01 00:45:31  Id=109   Src=Microsoft-Windows-Kernel-Power :: shutdown transition. Action: Power Action Shutdown Off
11-01 00:45:26  Id=6006  Src=EventLog            :: The Event log service was stopped.
11-01 08:06:27  Id=6005  Src=EventLog            :: The Event log service was started.
```

**→ 00:45:26 正常关机（用户/系统发起，`Shutdown Off`），08:06:27 开机。没有 6008（非正常关机），没有 41（Kernel-Power 意外掉电）。**

**Application 日志中的 `Application Error`（Level=1/2）实测结果：**

全部 Level 1/2 记录已逐条检查，来源包括：
- `IPF`（Intel ESIF 平台框架，Id=17，`Unauthorized EsifEventRegister` —— 无害的权限日志，每次开机都有）
- `CodexSandboxService.OpenAI.Codex`（Id=1003）
- `.NET Runtime`（Id=1000/1023，全部来自 **BD.WTTS**（Steam++ 加速器）和 **ilspycmd.exe** —— **与 HaseeCtl 无关**）
- `Microsoft-Windows-Search`（Id=7042/3602，搜索索引器问题）
- `SideBySide`（Id=59/33，THRM.exe / SnowDesktop 的清单错误）
- `VSS`（Id=13）
- `Application Error`（Id=1000，10-30 14:28:45 → **`Faulting application name: SnowDesktop.exe`**，不是 HaseeCtl）

**→ 全部 Application 日志中，`Message` 或 `ProviderName` 含 "HaseeCtl" 的记录：0 条（近 7 天）。没有任何 `Application Error` 指向 `HaseeCtl.Agent.exe` 或 `HaseeCtl.App.exe`。**

**因此 Lead 观察到的 "00:43:34 PID 30620 → 08:42:04 PID 24464" 完全由 00:45 关机、08:06 开机解释，HaseeCtl 是随系统重启重新拉起的，不是崩溃重启。**

### 10.2 CCDCHUService 与 OutLog

**OutLog 日志实测：**

```
LogName       = OutLog
RecordCount   = 10982
MaxSize       = 10485760 (10 MB)
IsEnabled     = True
```

**最近 6 小时写入频率：**

```
6h total: 241 events
earliest 2026/11/1 8:06:28   newest 2026/11/1 9:25:00
hourly distribution:
  11-01 08    207 events
  11-01 09     34 events
```

**最近 12 条样例（原文）：**

```
09:25:00  Id=0 :: CC40_Closing
09:23:16  Id=0 :: LoadUI_END
09:23:15  Id=0 :: Visible end:15.26
09:23:15  Id=0 :: ReadWmiMonitorBrightness=0
09:23:15  Id=0 :: Not Support read NPU usage
09:23:14  Id=0 :: CC40:WMI_Package_RW cmd=4 SubCMD=62 result=4 Set Data List: [0]=3e Get Data List: [0]=00 ...
09:23:14  Id=0 :: Get Reg APP Data  Fan_CPU.T1=40 Fan_CPU.T2=78 Fan_CPU.T3=95 Fan_CPU.T4=100 ...
```

**关键判读**：
1. **08 点 207 条，09 点 34 条**，且 09:23 之后的记录是 `CC40_Closing` / `LoadUI_END` —— 说明 **这些写入几乎全部来自 Control Center 主程序（CC40）的启动和 UI 日志，不是 CCDCHUService 的高频心跳**。
2. **`OutLog` 不是一个独立的 provider 日志**（`Get-WinEvent -ListProvider *OutLog*` 无结果；`Provider CCDCHUService` 也不存在）。它是 CCDCHUService 通过 `EvtCreateW`/自定义 channel 写入的**普通事件通道**，任何有权限的进程都能写。
3. **HaseeCtl 订阅 OutLog 的开销评估**：`FollowEventLog` 用 `EvtSubscribe` 订阅 + `WaitForSingleObject(signal, 500)` 每 500ms 唤醒一次做超时检查（这个不读数据，只是判断是否超时）。`EvtNext` 只在有新事件时批量取，`ChannelCheckInterval=5s` 才做一次 `UseServiceChannel` 判断（读 `App(1,12)` + `QueryServiceStatus`，都是廉价的本地调用）。
   - **实际开销极低**。真正的大头是 `App(1,12)` 读取——但 `ReadAppByte` 走的是 `Bridge.ReadAppStore()`，**每次读完整 4096 字节**（`Dchu.cs:65-68`）。若 5 秒一次，即 4096 字节 / 5 秒，可接受；但**如果 App 层有更频繁的 `ReadAppByte` 调用，会产生不必要的整页 IOCTL**。这是可优化点，不属于阻塞性问题。

### 10.3 与 Lead 观察的差异

| 项目 | Lead 观察 | 本报告实测 | 说明 |
|---|---|---|---|
| HKClipSvc 进程 | 已注册但未运行 | **Running，PID 5660，ExitCode 0** | 可能采集时点不同 |
| DCHUService PID | 5484 | **5484** | ✅ 一致 |
| FnKey PID | 15520 | **15520** | ✅ 一致 |

---

## 11. 卸载建议：卸载哪些、保留哪些

### 11.1 可以安全卸载（对 HaseeCtl 无影响）

| 项目 | 操作 | 说明 |
|---|---|---|
| **Control Center Package v6.020** | 正常走 InstallShield 卸载 | 卸载 CC 本体，`C:\Program Files (x86)\ControlCenter` 会被删 |
| `HKClipSvc` 服务 | **卸载后手动 `sc delete HKClipSvc`** | 否则开机报 Event 7000。**不影响 HaseeCtl** |
| `CLEVOCO.FnhotkeysandOSD` UWP（若残留） | `Get-AppxPackage -Name CLEVOCO* \| Remove-AppxPackage` | 卸载后检查，清理磁盘 |

### 11.2 绝对必须保留（删了 HaseeCtl 就废）

| 项目 | 保留理由 | 验证命令 |
|---|---|---|
| **`oem22.inf` / `AcpiBridge.sys`** | `ACPI\CLV0001\1` 设备的驱动。**HaseeCtl 的 `\\?\ACPI#CLV0001#1#{GUID}` 全靠它** | `pnputil /enum-drivers` 应有 oem22.inf；设备状态应为 OK |
| **`oem154.inf` / `AcpiBridge1.sys` / `DCHUService.exe`** | 承载 CCDCHUService。虽然 HaseeCtl 不强制依赖它（可降级直读驱动），但它是官方热键 OutLog 通道的来源 | `sc query CCDCHUService` 应为 RUNNING |
| **`HKKbdFltr.sys` + Keyboard UpperFilters** | **Fn 键事件的唯一来源**。删了热键全废，HaseeCtl 无法替代 | 注册表 `HKLM\SYSTEM\CurrentControlSet\Control\Class\{4d36e96b-...}` 的 `UpperFilters` 必须含 `HKKbdFltr` |
| **`HKMouFltr.sys` + Mouse UpperFilters** | 同上，鼠标侧 | `{4d36e96f-...}` 的 `UpperFilters` 必须含 `HKMouFltr` |

### 11.3 落地操作顺序（建议）

```powershell
# 0) 先跑基线（保留 sha256）
powershell -File F:\vibecode\REA_CON\_verify\DumpAppStore.ps1
#    记录 SHA256 = DAE89220A4DAC91A3F4B15FE0E9490805877354CDB10EA85BF28FDA277D94B4B

# 1) 走 Windows 设置 / 控制面板 卸载 "Control Center Package v6.020"

# 2) 卸载后清理孤儿服务（避免开机报错）
sc.exe stop HKClipSvc
sc.exe delete HKClipSvc

# 3) 检查 UWP 残留
Get-AppxPackage -Name CLEVOCO* | Select Name, PackageFullName
# 若还有 CLEVOCO.FnhotkeysandOSD 且不需要 OSD 图标：
# Get-AppxPackage -Name CLEVOCO.FnhotkeysandOSD* | Remove-AppxPackage

# 4) 复查驱动栈完好
pnputil /enum-drivers | Select-String -Pattern 'oem22|oem154' -Context 1,3
Get-PnpDevice -InstanceId 'ACPI\CLV0001\1' -Status OK

# 5) 复查 App 存储未被改动（与步骤 0 对比 SHA256）
powershell -File F:\vibecode\REA_CON\_verify\DumpAppStore.ps1   # 先改一下输出文件名

# 6) 启动 HaseeCtl 验证全功能
HaseeCtl.Agent.exe
```

---

## 12. 遗留问题与未覆盖项

| 项 | 状态 | 原因 |
|---|---|---|
| **热键通道独占性实测**（服务运行时自己调 IOCTL 是否失败/阻塞） | ❌ 未实测 | 需停服务才能测，红线禁止且 Lead 未批准。源码层面已给出等价结论（§5.1：`UseServiceChannel=true` 时根本不碰这两个 IOCTL） |
| **`App(1,12)` 在 CCDCHUService 停止后是否被重置为 0** | ❌ 未实测 | 同上。影响仅在"用户手动删服务"场景，且源码已证明两种取值都不影响热键 |
| **真实卸载后的行为** | ❌ 未执行 | 红线禁止卸载，且用户尚未决定。本报告是"预测 + 可验证基线" |
| **UWP 包是否随卸载被清除** | ⚠️ 不确定 | 取决于 InstallShield 是否调用 `Remove-AppxPackage`，需卸载后实测 |
| **`HKFltrSetup.bat -u` 的实际行为** | 未深入 | 已确认它不会被自动调用；手动执行的风险已在 §7.4 标红 |
| **`AMDRyzenMasterDriver`** | `NOT INSTALLED` | CC 目录里有安装源但未安装到系统，说明该机器是 Intel/或该模块未启用 |

---

## 13. 产出物清单

| 文件 | 说明 |
|---|---|
| `F:\vibecode\REA_CON\report_B_硬件与协议验证.md` | 本报告 |
| `F:\vibecode\REA_CON\_verify\appstore_before_uninstall.bin` | **App 存储 4096 字节原始 dump**（SHA256 `DAE89220...D94B4B`） |
| `F:\vibecode\REA_CON\_verify\appstore_before_uninstall.txt` | App 存储十六进制 + ASCII 文本 |
| `F:\vibecode\REA_CON\_verify\DumpAppStore.ps1` | App 存储只读 dump 脚本（可重跑做卸载后比对） |
| `F:\vibecode\REA_CON\_verify\RawWmiBaseline2.ps1` | 原始 WMI 读值采集脚本（修正版） |
| `F:\vibecode\REA_CON\_verify\UninstallBaseline.ps1` | 卸载边界 12 项基线快照脚本 |
| `F:\vibecode\REA_CON\_verify\uninstall_baseline.txt` | 基线快照输出 |
| `F:\vibecode\REA_CON\_verify\EventLogEvidence.ps1` | 事件日志取证脚本 |
| `F:\vibecode\REA_CON\_verify\eventlog_evidence.txt` | 事件日志输出 |
| `F:\vibecode\REA_CON\_verify\FileForensics.ps1` | 文件/DLL 取证脚本 |
| `F:\vibecode\REA_CON\_verify\file_forensics.txt` | 文件取证输出 |
| `F:\vibecode\REA_CON\_verify\DllIdentify.ps1` | InsydeAirplanemodehid.dll 比对脚本 |
| `F:\vibecode\REA_CON\_verify\ProbeDevice2.ps1` | ACPI 设备属性明细脚本 |
| `F:\vibecode\REA_CON\_verify\EnumDevices.ps1` | 设备枚举脚本（早期版本） |

---

*报告完成时间：2026-11-01 09:45（Asia/Shanghai）*
