# 🌉 ClipBridge

> **Bridge macOS screenshot clipboard directly to file paths in Terminal/CLI without losing image pasting in chat apps.**
>
> 为 macOS 设计的轻量级原生工具：截图后在终端/CLI 中直接粘贴**图片文件路径**，而在微信/飞书/Slack 等聊天软件中依然正常粘贴**图片本身**。

---

## 🌟 核心特性 (Features)

- 📸 **无缝截图转存**：自动捕获剪贴板中的截图或图片（支持 macOS 系统快捷键、微信截图、Snipaste、Shottr 等），并自动保存为高质量 PNG。
- 🪄 **多类型剪贴板 (Multi-type Pasteboard)**：
  - **在终端 / CLI / Anti Gravity CLI 中按 `Cmd + V`** ➡️ 自动粘贴图片的**本地绝对路径**。
  - **在微信 / 飞书 / 网页 / 聊天软件中按 `Cmd + V`** ➡️ 依然正常粘贴**图片本身**。
- ⚡️ **极致轻量**：纯原生 Swift 编写，无 Python/Node 依赖，常驻内存 < 10MB，CPU 占用接近 0%。
- 🔄 **自动防爆盘**：内置自动清理机制，自动清除 7 天前的历史截图，保持磁盘轻盈。
- 🚀 **开机自启**：一键配置 macOS 原生 `LaunchAgent` 守护进程，后台静默运行。

---

## 📦 安装与快速开始 (Installation)

### 方式 1：快速安装脚本

```bash
git clone https://github.com/DuMaChen/clip-bridge.git
cd clipbridge
./install.sh
```

### 方式 2：使用 Make

```bash
make install
```

> **注意**：请确保 `~/.local/bin` 在你的 `PATH` 环境变量中。

---

## 🛠️ 命令参考 (CLI Usage)

```bash
# 查看当前运行状态
clipbridge status

# 停止后台守护进程（并注销开机自启）
clipbridge stop

# 启动后台守护进程（并配置开机自启）
clipbridge start

# 在前台运行（用于调试查看实时日志）
clipbridge run

# 查看帮助
clipbridge --help
```

---

## 🧭 工作原理 (How It Works)

macOS 系统的 `NSPasteboard` 允许同一个复制条目同时挂载多种数据类型。

```text
[截图复制到剪贴板] 
       │
       ▼
[ClipBridge 自动捕获并存盘为 ~/.agy_screenshots/screenshot_xxx.png]
       │
       ├──► NSPasteboardTypePNG / TIFF (图片位图) ────► 微信 / 飞书 / 浏览器 (粘贴图片)
       └──► NSPasteboardTypeString (文件路径文本) ──► Terminal / CLI (粘贴路径)
```

---

## 📂 文件与目录说明

- **可执行文件**：`~/.local/bin/clipbridge`
- **截图存储目录**：`~/.agy_screenshots/`
- **日志目录**：`~/.agy_screenshots/logs/`
- **LaunchAgent 配置文件**：`~/Library/LaunchAgents/com.antigravity.clipbridge.plist`

---

## 📄 License

[MIT](LICENSE) © 2026 DuMaChen
