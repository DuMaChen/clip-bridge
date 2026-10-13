# ClipBridge

为 CLI 编程工具传截图和多媒体文件的原生剪贴板工具。截图自动保存为 PNG；复制图片、音频、视频文件后，根据前台软件提供文件路径或图片 / 文件格式。

## 粘贴行为

| 复制的内容 | CLI / 配置的编程工具 | 其他软件 |
| :--- | :--- | :--- |
| 截图 / 剪贴板图片 | 自动保存的 PNG 绝对路径 | 图片；Windows 可额外提供 Office RTF |
| 单张图片文件 | 原文件绝对路径 | 原始文件格式，并为可解码图片补充图片格式 |
| 音频 / 视频文件 | 原文件绝对路径 | 原始文件引用，由目标软件决定如何导入 |
| 多个文件，至少包含一个媒体文件 | 所有路径，每行一个 | 完整原始文件列表与原始格式 |
| 媒体文件混合普通文件 / 目录 | 保留全部路径与顺序 | 保留全部文件 / 目录与顺序 |
| 普通文本 / 不含媒体的文件选择 | 保持原样 | 保持原样 |

路径用于给编程助手提供本地文件位置，不是可直接执行的 shell 命令。不会按 shell 语法添加引号，也不会执行命令。带空格或中文的路径完整保留。

程序保留当前这一次媒体复制的两种表示，前台软件变化时切换。CLI 模式只提供文本，避免目标工具优先读取图片或文件引用；其他软件恢复完整文件列表或图片格式。Windows 使用前台窗口事件，并每 150 毫秒重试检查；macOS 使用应用激活通知，并每 150 毫秒检查剪贴板。

目标软件必须支持对应粘贴格式：文件引用不保证在每个网页或聊天软件中自动上传为附件；音频 / 视频不转码、不自动发送。编程助手能否读取媒体，还取决于该工具的文件访问能力及模型支持。应用识别按进程名 / bundle ID，不能区分同一个应用内的终端、编辑区与聊天输入框。

## 安全与复制保护

- 多选不会缩减为第一张图片；保留混合选择、原始顺序和可读取的来源格式，包括 Windows 剪切文件的 Preferred DropEffect。
- 无法完整读取原始文件剪贴板格式时，放弃适配，保持原剪贴板。
- Windows 在读取前记录剪贴板序号，并在处理完成和每次写入重试前重新检查；macOS 检查 changeCount。检测到新复制时，放弃旧缓存。
- 系统剪贴板 API 不提供跨进程的原子“比较并写入”；最后一次检查和系统写入之间仍有极短竞争窗口。不要把此防护视为绝对的并发保证。
- Windows 检测 Office OLE 对象格式，避免把复制的形状 / 文本框 PNG 预览当作截图。
- 自动清理只针对存储目录内的 screenshot_*.png。macOS 还限定为普通文件，排除目录和符号链接。配置、日志和复制的原始媒体文件不在清理范围。

## 安装与构建

### Windows

已发布版本可从 [Releases](https://github.com/DuMaChen/clip-bridge/releases) 下载。本文描述当前源码行为；旧版发布包可能不包含前台软件适配。

从源码构建：

```powershell
git clone https://github.com/DuMaChen/clip-bridge.git
cd clip-bridge
.\build.bat
# 构建产物：bin\clipbridge.exe
```

双击构建产物打开安装 / 设置界面，或使用：

```powershell
powershell -ExecutionPolicy Bypass -File .\install.ps1
# 也可使用 install.bat 或 make install
```

使用 Windows 自带的 .NET Framework csc.exe 编译，无需额外 SDK。安装到 %USERPROFILE%\.local\bin；支持托盘、图形设置、热重载与当前用户 HKCU Run 自启。

### macOS

需要 Swift 编译工具与 AppKit，可安装 Xcode Command Line Tools。

```bash
git clone https://github.com/DuMaChen/clip-bridge.git
cd clip-bridge
./install.sh
# 或 make install
```

安装到 ~/.local/bin，通过 LaunchAgent 配置登录自启。请将 ~/.local/bin 加入 PATH。macOS 当前没有图形设置 / 托盘 / Office RTF，保留策略固定为 7 天 / 200 张。

## 前台软件规则

### Windows

编辑 %USERPROFILE%\.agy_screenshots\config.ini 中的 path_processes，逗号分隔，不带 .exe，忽略大小写，保存后热重载。默认：

```ini
path_processes=WindowsTerminal,WindowsTerminalPreview,cmd,powershell,pwsh,conhost,OpenConsole,Code,Cursor,Codex
```

其他进程使用图片 / 原文件格式。将某软件从列表移除，即可让它优先接收图片 / 文件；设置为空可对所有软件提供图片 / 文件。保存图形设置会保留这项手动规则。

### macOS

在 ~/.agy_screenshots/path_apps.txt 填写 bundle ID，以逗号或换行分隔，重启后台服务后生效。默认：

```text
com.apple.Terminal
com.googlecode.iterm2
dev.warp.Warp-Stable
com.microsoft.VSCode
com.todesktop.230313mzl4w4u92
com.openai.codex
```

实际 bundle ID 以本机安装的软件为准；可用下面的命令查询：

```bash
osascript -e 'id of app "Terminal"'
```

环境变量 CLIPBRIDGE_PATH_APPS 可覆盖该文件；LaunchAgent 启动时需要将变量设在其运行环境中。空文件表示所有软件都使用图片 / 原文件格式。

## 命令参考

| 命令 | Windows | macOS |
| :--- | :--- | :--- |
| clipbridge（无参数） | 安装 / 设置界面 | 前台监听 |
| clipbridge start / install | 启动并配置自启 | 安装 / 启动 LaunchAgent |
| clipbridge stop / uninstall | 停止并移除自启 | 卸载 LaunchAgent |
| clipbridge status | 运行、自启、内存及截图统计 | 服务条目、安装状态与目录 |
| clipbridge run | 前台监听 | 前台监听 |
| clipbridge clean / cleanup | 手动清理截图 | 未实现 |
| clipbridge --version | 版本号 | 未实现 |
| clipbridge --help | 帮助 | 帮助 |

macOS 的 status 通过 launchctl list 检查服务条目存在，不代表进程健康检查。

## 文件与配置

| 项目 | Windows | macOS |
| :--- | :--- | :--- |
| 可执行程序 | %USERPROFILE%\.local\bin\clipbridge.exe | ~/.local/bin/clipbridge |
| 截图 | 默认“图片”目录下 clipbridge，可配置 | ~/.agy_screenshots/ |
| 配置 | %USERPROFILE%\.agy_screenshots\config.ini | path_apps.txt（仅前台应用规则） |
| 日志 | %USERPROFILE%\.agy_screenshots\logs\ | ~/.agy_screenshots/logs/ |
| 自启 | HKCU\Software\Microsoft\Windows\CurrentVersion\Run | ~/Library/LaunchAgents/com.antigravity.clipbridge.plist |

Windows config.ini：

| 键 | 默认值 | 说明 |
| :--- | :--- | :--- |
| enabled | 1 | 监听与前台格式切换开关 |
| auto_start | 1 | 登录自启 |
| storage_dir | 空 | 使用系统“图片”目录下的 clipbridge |
| max_age_days | 7 | 截图保留天数 |
| max_count | 200 | 最大截图数量 |
| add_rtf | 1 | 图片模式提供 Office RTF |
| add_filedrop | 1 | 为新截图新增文件引用；不移除复制文件自带的文件列表 |
| path_processes | 见上文 | 接收文本路径的进程名单 |

原文件不复制到截图目录；音频 / 视频不会生成缓存副本。应用仍可能优先选择与预期不同的格式，必要时调整进程规则。剪贴板管理器可能把格式切换记录为新的历史项。

## 验证

```powershell
.\tests\run-windows.ps1
```

回归检查使用合成数据与模拟写入，不改动系统剪贴板，覆盖多文件、混合选择、Unicode 路径、原始格式 / 剪切元数据、进程匹配和剪贴板变化期间的写入重试。

macOS 上运行 `bash tests/run-macos.sh`：先检查生产代码的 Swift 类型，再在临时目录验证清理仅作用于截图文件、保留配置 / 日志 / 符号链接，并遵守数量限制。

还需在实际应用中验证：复制图片 + 音频 + 视频 + 普通文件 → CLI 检查完整路径 → 文件管理器粘贴完整文件列表；切换前台软件后快速粘贴；连续复制截图和文本，确认最终保留最新内容；Office 复制形状；macOS 清理时确认配置 / 日志 / 符号链接保留。

Windows 剪贴板监听由事件触发；前台切换另有 150 毫秒定时检查。macOS 剪贴板检查间隔为 150 毫秒。实际转换延迟还受图片大小、存盘、格式序列化与目标软件影响。旧文档中的固定内存和 <5 毫秒延迟没有可复现基准，已移除。

## License

[MIT](LICENSE) © 2026 DuMaChen
