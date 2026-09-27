# 🌉 ClipBridge

> **Bridge macOS & Windows screenshot clipboard directly to file paths in Terminal/CLI without losing image pasting in chat apps.**
>
> 专为 macOS 与 Windows 双平台打造的极致轻量级原生工具：截图后在终端/CLI/Anti Gravity CLI中直接粘贴**图片文件路径**，而在微信/飞书/Slack/Discord等聊天软件中依然正常粘贴**图片本身**。

---

## 🌟 核心特性 (Features)

- 📸 **无缝截图转存**：自动捕获剪贴板中的截图或图片（支持 Windows 自带截图 `Win + Shift + S`、Snipaste、微信截图、QQ截图；macOS 系统快捷键、Shottr 等），并自动保存为高质量 PNG。
- 🪄 **多类型剪贴板 (Multi-type Pasteboard / Multi-Format Clipboard)**：
  - **在终端 / CLI / PowerShell / CMD / VS Code / Cursor / Anti Gravity CLI 中按 `Ctrl + V` (`Cmd + V`)** ➡️ 自动粘贴图片的**本地绝对路径**。
  - **在微信 / 飞书 / 网页 / Slack / Discord 中按 `Ctrl + V` (`Cmd + V`)** ➡️ 依然正常粘贴**图片本身**。
  - **在文件管理器中** ➡️ 支持作为文件实体粘贴。
- ⚡️ **极致轻量 (Ultra Lightweight & Zero Dependency)**：
  - **Windows 版本**：纯原生 Win32 C# 实现，利用 Windows 10/11 自带编译器 `csc.exe` 零依赖秒级编译，可执行文件仅 **~20 KB**。基于 Win32 `AddClipboardFormatListener` 原生事件驱动，**空闲 CPU 严格为 0.00%**，常驻内存仅 **~2-4 MB**！
  - **macOS 版本**：纯原生 Swift 实现，无 Python/Node/Rust 依赖，常驻内存 < 10MB，CPU 占用接近 0%。
- 🔄 **自动防爆盘**：内置自动清理机制，自动清除 7 天前的历史截图，且最多保留 200 张，保持磁盘轻盈。
- 🚀 **开机自启**：
  - **Windows**：一键配置当前用户注册表启动项 (`HKCU Run`)，免管理员权限，后台静默静音运行。
  - **macOS**：一键配置原生 `LaunchAgent` 守护进程。

---

## 📦 安装与快速开始 (Installation)

### 🪟 Windows 安装

克隆仓库后进入目录：

```bash
git clone https://github.com/DuMaChen/clip-bridge.git
cd clip-bridge
```

#### 方式 1：PowerShell 一键安装（推荐）

在 PowerShell 中执行：

```powershell
powershell -ExecutionPolicy Bypass -File .\install.ps1
```

#### 方式 2：双击或在 CMD 中运行批处理

```cmd
install.bat
```

#### 方式 3：使用 Make

```cmd
make install
```

> 脚本会自动完成以下动作：
> 1. 使用 Windows 自带的 `csc.exe` 编译生成原生单文件可执行程序 `clipbridge.exe`（无需安装任何 SDK）。
> 2. 安装至用户目录 `~/.local/bin/clipbridge.exe`。
> 3. 自动将 `~/.local/bin` 追加至当前用户的 `PATH` 环境变量。
> 4. 自动注册开机自启并启动后台静默守护服务。

---

### 🍎 macOS 安装

```bash
# 方式 1：安装脚本
./install.sh

# 方式 2：使用 Make
make install
```

> **注意**：请确保 `~/.local/bin` 在你的 `PATH` 环境变量中。

---

## 🛠️ 命令参考 (CLI Usage)

双平台通用命令（Windows 下 `clipbridge` 或 `clipbridge.exe`，macOS 下 `clipbridge`）：

```bash
# 查看当前运行状态、自启状态、内存占用与截图统计
clipbridge status

# 启动后台守护进程（并配置开机自启）
clipbridge start

# 停止后台守护进程（并注销开机自启）
clipbridge stop

# 在前台控制台运行（用于实时观察日志与调试）
clipbridge run

# 手动执行一次过期截图清理
clipbridge clean

# 查看帮助
clipbridge --help
```

---

## 🧭 工作原理 (How It Works)

现代操作系统的剪贴板（macOS 的 `NSPasteboard` 与 Windows 的 `Win32 Clipboard DataObject`）均支持在同一个剪贴条目上**同时挂载多种数据类型 (Multi-Format / Multi-Type)**：

```text
                       [截图工具 / 复制图片]
                                │
                                ▼
         [ClipBridge 自动捕获并存盘为 ~/.agy_screenshots/screenshot_xxx.png]
                                │
       ┌────────────────────────┴────────────────────────┐
       ▼                                                 ▼
[图像格式: PNG / DIB / Bitmap]             [纯文本格式: CF_UNICODETEXT / String]
       │                                                 │
       ▼                                                 ▼
微信 / 飞书 / Slack / 网页                     Terminal / PowerShell / CMD / CLI
       │                                                 │
[粘贴为图像]                                      [粘贴为绝对文件路径]
```

### 为什么在 Terminal 和 微信 中能同时完美工作？
- **终端 / CLI（如 Windows Terminal, PowerShell, CMD, macOS Terminal, Anti Gravity CLI）**：
  在用户按下 `Ctrl+V` 或 `Cmd+V` 时，终端只认文本格式（`CF_UNICODETEXT` / `String`），因此终端会自动读取并粘贴图片文件的**本地绝对路径**！
- **聊天应用（如 微信、飞书、Slack、Discord、QQ）**：
  在检测到剪贴板包含图片格式（`PNG` / `CF_DIB` / `CF_BITMAP`）时，聊天输入框会优先以图片对象进行消费，因此依然粘贴为**图片**！

---

## 📂 文件与目录说明

| 平台 | 项目 | 默认路径 |
| :--- | :--- | :--- |
| **Windows** | 可执行程序 | `%USERPROFILE%\.local\bin\clipbridge.exe` |
| | 截图存储目录 | `%USERPROFILE%\.agy_screenshots\` |
| | 运行日志目录 | `%USERPROFILE%\.agy_screenshots\logs\` |
| | 开机自启项 | 注册表 `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` (`ClipBridge`) |
| **macOS** | 可执行程序 | `~/.local/bin/clipbridge` |
| | 截图存储目录 | `~/.agy_screenshots/` |
| | 运行日志目录 | `~/.agy_screenshots/logs/` |
| | LaunchAgent 配置 | `~/Library/LaunchAgents/com.antigravity.clipbridge.plist` |

---

## ⚡️ 性能与轻量化指标 (Benchmarks)

| 指标 | Windows 版本 (C# Win32) | macOS 版本 (Swift 原生) |
| :--- | :--- | :--- |
| **二进制文件大小** | **~20 KB** (单文件，无额外依赖) | **~50 KB** (单文件原生) |
| **外部运行环境** | **零依赖**（利用 Windows 自带 .NET 4.8 / csc.exe） | **零依赖**（macOS 自带 Swift Runtime） |
| **空闲 CPU 占用** | **0.00%** (Win32 事件通知，无轮询) | **接近 0.00%** |
| **常驻内存 (Working Set)** | **~2 MB - 4 MB** | **< 10 MB** |
| **截图捕获延迟** | **< 5 毫秒**（即截即转） | **< 5 毫秒** |

---

## 📄 License

[MIT](LICENSE) © 2026 DuMaChen
