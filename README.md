# ClipBridge

> Bridge macOS & Windows screenshot clipboard directly to file paths in Terminal/CLI without losing image pasting in chat apps.
>
> 为 macOS 与 Windows 打造的轻量级原生工具：截图后在终端/CLI 中直接粘贴图片文件路径，而在微信/飞书/Slack 等聊天软件中依然正常粘贴图片本身。

---

## 核心特性 (Features)

- **无缝截图转存**：自动捕获剪贴板中的截图或图片（支持 Windows 自带截图 `Win + Shift + S`、Snipaste、微信截图、QQ 截图；macOS 系统快捷键、Shottr 等），并自动保存为 PNG。
- **多类型剪贴板 (Multi-Format Clipboard)**：
  - 在终端 / CLI / PowerShell / CMD / VS Code / Cursor 中按 `Ctrl + V`（macOS 为 `Cmd + V`）—— 自动粘贴图片的本地绝对路径。
  - 在微信 / 飞书 / 网页 / Slack / Discord 中粘贴 —— 依然正常粘贴图片本身。
  - 在 Word / Excel / PowerPoint / WPS 中粘贴 —— 通过内嵌 RTF 粘贴图片本身（v1.1.0 新增）。
  - 在文件管理器中 —— 支持作为文件实体粘贴。
- **图形设置界面与托盘图标 (v1.1.0)**：双击托盘图标打开设置窗口，可视化配置监听开关、开机自启、存储目录、保留策略、Office 兼容（RTF）与文件引用（FileDrop）。设置保存后即时热生效，无需重启。
- **轻量且零依赖**：
  - Windows 版本：纯 Win32 C# 实现，使用 Windows 自带的 `csc.exe` 编译，可执行文件约 40 KB，单文件无依赖。基于 `AddClipboardFormatListener` 事件驱动，空闲 CPU 占用为 0，常驻内存约 20 MB。
  - macOS 版本：纯 Swift 实现，无 Python/Node 等依赖，常驻内存小于 10 MB，CPU 占用接近 0。
- **自动清理**：自动删除过期截图并限制最大数量（默认保留 7 天 / 200 张，均可在设置中调整），避免磁盘占用持续增长。
- **开机自启**：
  - Windows：通过当前用户注册表启动项（`HKCU Run`）配置，无需管理员权限。
  - macOS：通过原生 `LaunchAgent` 守护进程配置。

---

## 安装与快速开始 (Installation)

### Windows

#### 方式一：一键安装（推荐）

1. 前往 [Releases](https://github.com/DuMaChen/clip-bridge/releases) 下载最新的 `ClipBridge-vX.Y.Z.exe`（单文件，约 40 KB）；
2. 双击运行，点击"一键安装"即可完成。程序会自动安装到 `%USERPROFILE%\.local\bin`、加入 PATH、配置开机自启并启动后台服务；
3. 安装完成后，双击托盘图标即可打开设置界面。

已安装用户再次运行该文件即为"重新安装 / 更新"，已有配置与截图数据不受影响。

#### 方式二：从源码安装

```bash
git clone https://github.com/DuMaChen/clip-bridge.git
cd clip-bridge
```

```powershell
# PowerShell 编译安装
powershell -ExecutionPolicy Bypass -File .\install.ps1
```

```cmd
:: 或使用批处理 / Make
install.bat
make install
```

脚本会使用 Windows 自带的 `csc.exe` 编译生成单文件 GUI 程序（无需安装任何 SDK），安装至 `~/.local/bin/clipbridge.exe`，自动追加 PATH、注册开机自启并启动。

### macOS

```bash
# 方式一：安装脚本
./install.sh

# 方式二：使用 Make
make install
```

注意：请确保 `~/.local/bin` 在你的 `PATH` 环境变量中。

---

## 命令参考 (CLI Usage)

双平台通用命令（Windows 下 `clipbridge` 或 `clipbridge.exe`，macOS 下 `clipbridge`）：

```bash
# 打开图形界面：未安装时为安装向导，已安装时为设置窗口
clipbridge

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

# 查看版本 / 帮助
clipbridge --version
clipbridge --help
```

Windows 版所有设置均可通过托盘图标的设置界面修改，保存后即时生效（无需重启）；也可以直接编辑 `%USERPROFILE%\.agy_screenshots\config.ini`，守护进程支持热重载。

---

## 工作原理 (How It Works)

现代操作系统的剪贴板（macOS 的 `NSPasteboard` 与 Windows 的 Win32 Clipboard）均支持在同一个剪贴条目上同时挂载多种数据格式：

```text
                       [截图工具 / 复制图片]
                                |
                                v
         [ClipBridge 自动捕获并存盘为 ~/.agy_screenshots/screenshot_xxx.png]
                                |
       +------------------------+------------------------+
       v                                                 v
[图像格式: PNG / DIB / Bitmap]             [纯文本格式: CF_UNICODETEXT / String]
       |                                                 |
       v                                                 v
微信 / 飞书 / Slack / 网页                    Terminal / PowerShell / CMD / CLI
       |                                                 |
[粘贴为图像]                                      [粘贴为绝对文件路径]
```

### 为什么终端和聊天软件能同时正常工作

- **终端 / CLI（Windows Terminal、PowerShell、CMD、macOS Terminal 等）**：只读取文本格式（`CF_UNICODETEXT` / `String`），因此粘贴得到图片文件的本地绝对路径。
- **聊天应用（微信、飞书、Slack、Discord、QQ 等）**：检测到剪贴板包含图像格式（`PNG` / `CF_DIB` / `CF_BITMAP`）时优先以图片对象消费，因此粘贴为图片本身。
- **Office（Word / Excel / PowerPoint / WPS）**：纯文本格式优先级高于位图，因此额外提供内嵌图片的 RTF 格式，粘贴结果为图片本身而非文件路径。
- **Office 形状 / 文本框复制保护（v1.1.1）**：Office 复制形状时会附带 PNG 预览与 OLE 对象格式，ClipBridge 检测到 OLE 格式即放行，不会误把文档内容当作截图处理。

---

## 文件与目录说明

| 平台 | 项目 | 默认路径 |
| :--- | :--- | :--- |
| **Windows** | 可执行程序 | `%USERPROFILE%\.local\bin\clipbridge.exe` |
| | 截图存储目录 | `%USERPROFILE%\.agy_screenshots\`（可在设置中更改） |
| | 配置文件 | `%USERPROFILE%\.agy_screenshots\config.ini` |
| | 运行日志目录 | `%USERPROFILE%\.agy_screenshots\logs\` |
| | 开机自启项 | 注册表 `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` (`ClipBridge`) |
| **macOS** | 可执行程序 | `~/.local/bin/clipbridge` |
| | 截图存储目录 | `~/.agy_screenshots/` |
| | 运行日志目录 | `~/.agy_screenshots/logs/` |
| | LaunchAgent 配置 | `~/Library/LaunchAgents/com.antigravity.clipbridge.plist` |

### Windows 可配置项（config.ini）

| 配置键 | 默认值 | 说明 |
| :--- | :--- | :--- |
| `enabled` | `1` | 剪贴板监听开关（托盘右键也可快速暂停/恢复） |
| `auto_start` | `1` | 开机自启（HKCU Run） |
| `storage_dir` | 空（默认目录） | 截图存储目录 |
| `max_age_days` | `7` | 截图保留天数 |
| `max_count` | `200` | 最多保留张数 |
| `add_rtf` | `1` | 为 Office 提供 RTF 内嵌图片（Word/Excel/PPT 粘贴为图片） |
| `add_filedrop` | `1` | 提供文件引用格式（文件管理器可粘贴为文件副本） |

---

## 性能指标 (Benchmarks)

| 指标 | Windows 版本 (C# Win32) | macOS 版本 (Swift) |
| :--- | :--- | :--- |
| 二进制文件大小 | ~40 KB（单文件，含 GUI 与托盘，无额外依赖） | ~50 KB（单文件原生） |
| 外部运行环境 | 零依赖（Windows 自带 .NET Framework 4.8 / csc.exe） | 零依赖（macOS 自带 Swift Runtime） |
| 空闲 CPU 占用 | 0%（Win32 事件通知，无轮询） | 接近 0% |
| 常驻内存 (Working Set) | ~20 MB（含托盘与设置界面，定期自动修剪） | < 10 MB |
| 截图捕获延迟 | < 5 毫秒 | < 5 毫秒 |

---

## License

[MIT](LICENSE) © 2026 DuMaChen
