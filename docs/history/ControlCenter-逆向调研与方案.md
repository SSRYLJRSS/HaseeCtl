# 神舟 CV15S Control Center 逆向调研与替代方案

调研日期：2026-10-31。只做了只读调用，没有向 EC 写入任何值。

## 1. 软件构成

| 层 | 组件 | 说明 |
|---|---|---|
| 界面 | 9 个 UWP 子应用（FanSpeedSetting、CC40 主界面、FnKey、KB_Perkey、GPUOverclocking、CPU_OC、BatteryUtility、EnergySave、Flexikey） | 全部是 .NET WPF/WinForms x64，ilspycmd 可完整反编译成 C#（`cc_re/src/`） |
| 常驻 | `FnKey.exe`（托盘、Fn 热键、OSD、模式切换）、`DCHUService.exe`（LocalSystem 服务） | FnKey 每次切换性能模式都会改风扇模式 |
| 原生库 | `InsydeDCHU.dll`（6 个导出）、`NVGPU_DLL`（NVAPI 封装）、`perkey_api.dll`/`LineLEDAPI.dll`（键盘灯 HID）、Intel XTU SDK（CPU 超频） | Ghidra 已反编译 InsydeDCHU.dll |
| 驱动 | `AcpiBridge`（KMDF，`ACPI\CLV0001`），HKKbdFltr/HKMouFltr | 驱动把 IOCTL 转成 ACPI WMI `_DSM` 调用，交给 BIOS/EC 处理 |

## 2. 底层协议（已验证可读）

- 设备接口 GUID `{86994c74-ad43-4812-b7e7-0c420b5c5fd7}`，`CreateFileW` 打开。
- `IOCTL 0x322400`：WMI 方法 GUID `93F224E4-FBDC-4BBF-ADD6-DB71BDC0AFAD`，命令号 + 256 字节参数。
- `IOCTL 0x32240c`：16 页 × 256 字节的 App 设置存储（BIOS/驱动侧，不在注册表）。
- 普通用户权限下用 Python ctypes 调原 DLL，读出了转速、占空比、温度和默认风扇表。

## 3. 风扇控制

- 读：WMI 12（实时转速/占空比/温度），WMI 13（默认曲线、风扇数、能力位）。
- 写曲线：WMI 14，每个风扇 4 个点，T1/D1 来自默认值，T4/D4 固定 100，**只有中间 2 个点可调**，外加 3 段斜率。
- 模式：`SetWMI(121,1,m)`，0 自动 / 1 全速 / 3 静音 / 5 MaxQ / 6 自定义 / 8 慢速；偏移 `121,14`；除尘 `121,41`；恢复默认 `121,34`。

### 自定义不能保存的原因（代码证据）

1. `FnKey/Form1.cs:2738` 每次切换性能模式都调用 `Load_FanMode_forFn_3`，把 App(4,5) 写成 0（自动）或 5（MaxQ），自定义模式被覆盖。只有开机/唤醒后的第一次会走 `KeepFanMode` 恢复 6。
2. `FanSpeedSetting/Page_system_monitor.cs:860`：打开风扇界面时，如果 `HKCU\Software\ControlCenter3.0\Fan\First_run` 不为 1、App(0,12) 为 0 或 EC 版本变化，就重写默认曲线并设回自动。本机这个注册表键不存在，所以每次打开都会重置。
3. 只有一套曲线槽位，没有“多套配置”的概念。

## 4. 其他功能与命令

| 功能 | 接口 |
|---|---|
| 性能/情景模式 | `SetWMI(121,25)`，App(1,0) |
| 充电上下限 FlexiCharger | `SetWMIPackageEx(4, {30,...})` 读，`{..., status, start, stop}` 写 |
| 独显直连/混合切换 | `SetWMIPackageEx(4, {21/22,...})` |
| 飞行模式、Win 键锁、触控板等 | `SetWMI(19)`、`121,26` 等，集中在 FnKey |
| 键盘灯 | `perkey_api.dll` / `LineLEDAPI.dll`（HID），可参考 silent-ram/ClevoLEDKeyboardControl |
| 显卡超频 | NVAPI（`NVGPU_DLL` 51 个导出） |
| CPU 超频 | Intel XTU SDK，依赖重，建议后期再考虑 |

## 5. 路线对比

| 路线 | 做法 | 优点 | 缺点 |
|---|---|---|---|
| A. 打补丁 | 改 FnKey/FanSpeedSetting 的 IL | 改动最小 | UWP 包签名，改了要重签名侧载；官方一更新就失效；原软件的臃肿问题还在 |
| B. 守护进程 | 原软件保留，另写一个小程序：保存多套曲线，检测到风扇模式被改回就重新写入 | 1～2 天能用；热键和 OSD 继续用原版 | 和 FnKey 互相覆盖，属于“抢写” |
| C. 完整重写 | 直接调 IOCTL，自己实现风扇、模式、充电、显卡切换、键盘灯，最后替换 FnKey | 最轻、完全可控 | Fn 热键事件和 OSD 要额外逆向，工作量最大 |

**建议：C 为目标，分阶段走，B 是第一阶段的产物。** 协议很小，反编译代码可以直接当规格书，重写的风险主要在热键部分。

## 6. 分阶段计划

1. **验证写入**（半天）：Python 原型，在自定义模式下写一组保守曲线，确认是否生效、写入是否需要管理员、睡眠/重启后 EC 是否保持。同时测试“软件曲线”：每 1～2 秒读温度、动态改写 WMI 14，看能否突破 2 点限制。
2. **风扇工具**（1～3 天）：托盘程序 + 多套配置（JSON）+ 开机自启，监听电源事件和模式变化后重新应用；温度超过阈值时强制回自动，作为安全兜底。
3. **其他功能**：性能模式、充电上限、显卡切换、键盘灯，逐个移植。
4. **替换 FnKey**：逆向 Fn 热键的 WMI 事件和 OSD，之后可以卸载原版。

技术栈：Rust（`windows` crate 直接 DeviceIoControl，单文件几 MB）或 C# .NET 8（可以直接搬反编译代码，开发快，体积大一些）。

## 7. 待确认的风险

- 还没有做过任何写入测试，写入的权限要求和持久性未知。
- 错误的曲线可能导致过热；需要温度兜底，EC 本身应有过热保护但未验证。
- `SetWMIPackageEx(4, ...)` 系列命令（充电、显卡切换）参数细节还没逐个读完。
