# SnipTrans · 截图即翻译

[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
[![.NET](https://img.shields.io/badge/.NET-8.0-512BD4.svg)](https://dotnet.microsoft.com/)
[![Platform](https://img.shields.io/badge/Windows-10%20%7C%2011-0078D4.svg)](#)

给 Windows 的 `Win + Shift + S` 装上翻译能力：你照常框选屏幕区域，光标旁自动弹出译文浮窗。

无需改变任何操作习惯，也不用自己画截图框。

## 为什么是"监听剪贴板"

`Win + Shift + S` 是系统截图工具的硬编码热键，**第三方程序无法替换或劫持它**。但截图工具会把框选结果写入剪贴板 —— 所以正确做法是监听剪贴板：

```
你按 Win+Shift+S 框选
      ↓  系统截图工具把图写入剪贴板
监听剪贴板（GetClipboardSequenceNumber 轮询，零侵入）
      ↓
Windows 系统自带 OCR 识别文字（Windows.Media.Ocr）
      ↓
调翻译接口
      ↓
光标旁弹出无边框浮窗（Esc 关闭 / 双击复制）
```

好处：不用装 OCR 模型、不用改操作习惯、单文件 25MB，托盘常驻内存约 200MB。

## 使用方法

1. 双击 `SnipTrans.exe`，托盘出现蓝色图标。
2. 正常按 `Win + Shift + S`，框选任意区域。
3. 松开鼠标，译文浮窗自动出现在光标附近。
4. `Esc` 关闭浮窗，**双击译文**复制，双击原文可复制原文。

托盘图标右键菜单：

| 菜单项 | 说明 |
|---|---|
| 启用截图翻译 | 临时暂停／恢复监听 |
| 立即翻译剪贴板图片 | 手动处理剪贴板中已有的图 |
| 设置… | 引擎、语言、显示行为 |
| 开机自动启动 | 写注册表 `HKCU\...\Run` |
| 关于 / 退出 | — |

## 引擎说明

### OCR

用 Windows 自带的 `Windows.Media.Ocr`，无需下载模型。可用语言取决于系统已安装的语言包：

- Windows 中文版通常自带 **en-US** 与 **zh-Hans-CN**
- 需要日/韩识别时，到「设置 → 时间和语言 → 语言」添加对应语言包

程序会**同时跑中英两个引擎再择优**：中文引擎会把汉字逐字插空格、且英文易断词；英文引擎完全认不出汉字。策略是：

- 整行纯英文 → 用英文引擎（连词最准）
- 中英混排行 → 汉字用中文引擎、纯 ASCII 词按坐标重叠替换成英文引擎的结果
- 全文以汉字为主 → 中文引擎 + 清理字间空格

### 翻译

| 引擎 | 是否需要配置 | 说明 |
|---|---|---|
| **MyMemory**（默认） | 不需要 | 免费，国内可直连。单次请求有长度上限，程序会按 UTF-8 字节严格分段 |
| AI 大模型 | 需 API Key | 任何 OpenAI 兼容接口（DeepSeek / 通义 / GLM / GPT）。质量最好，长文本也不分段 |

> 截图翻译一天几千字，用 DeepSeek 之类的模型成本大约几分钱。
> 设置里点「测试翻译引擎」可以先确认当前网络能通。

实测参考延迟（本机 1920×1080，MyMemory）：OCR 20–100ms，翻译热身后约 0.5s、冷启动 1–1.8s，偶发网络抖动可能到 10s+。

## 配置文件

`%APPDATA%\SnipTrans\settings.json`，调试日志在同目录 `debug.log`。

托盘「设置 → 打开配置文件夹」可直接打开。

| 字段 | 说明 |
|---|---|
| `Enabled` | 是否监听剪贴板 |
| `Engine` | `MyMemory` / `OpenAi` |
| `TargetLanguage` | 默认目标语言 |
| `AutoSwitchDirection` | 原文是中文时自动反向译成英文 |
| `ShowOriginal` | 浮窗中显示识别到的原文 |
| `CloseOnDeactivate` | 浮窗失焦自动关闭 |
| `AutoCloseSeconds` | 自动关闭秒数，0 = 不关 |
| `MaxImageSide` | 超过该边长的图忽略（防误处理壁纸） |
| `SkipDuplicateImage` | 跳过重复的相同截图 |
| `MinTextLength` | 识别文字少于此长度不弹窗 |

## 已知限制

- **中英混排**：中文引擎认英文容易断词（`OCR` → `OC RO`），词级融合能修一部分，但不保证全对。纯英文或纯中文场景效果最好。
- 浮窗不抢焦点，因此按 `Esc` 前需要先点一下浮窗（或用鼠标点关闭按钮）。
- 截图工具需开启「自动复制到剪贴板」（Win11 默认开启）。
- 复制网页图片、从资源管理器复制图片文件也会触发翻译，但识别不到文字时会静默收掉浮窗，不会打扰。
- 读取剪贴板放在独立 STA 线程并设 2s 超时 —— 如果占用剪贴板的程序卡死，本次翻译会被跳过而不是卡住托盘程序。

## 实测数据（本机 1920×1080，Windows 11）

| 项目 | 结果 |
|---|---|
| 剪贴板检测到出图 | 约 100–300ms |
| OCR（720×190 英文图） | 20–135ms（首次 105ms，引擎预热后 20–90ms） |
| 翻译（MyMemory 热身后） | **约 500ms**；冷启动 1–1.8s；偶有网络抖动到 10s+ |
| 连续 10 轮压测 | 全部成功，0 错误 |
| 常驻内存 | 基线 80MB → 首次出图后稳定 **约 200MB**（无持续泄漏） |

> 注意：单文件若开启 `EnableCompressionInSingleFile`（70MB 版本），内存会涨到约 340MB，
> 因为解压缓存常驻。所以正式包**不启用压缩**（164MB，内存 200MB）。

## 下载与构建

发布包放在 **Releases** 页：`SnipTrans-lite.exe`（约 25MB，启动最快）。

需要先装 **[.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0)**（Windows x64）。
如果不想装运行时，可自行构建独立版（约 164MB，见下）。

```bash
# 调试运行
dotnet build
bin/Debug/net8.0-windows10.0.19041.0/SnipTrans.exe

# 依赖运行时的轻量单文件（推荐，约 25MB）
dotnet publish -c Release -r win-x64 --self-contained false \
  -p:PublishSingleFile=true -p:DebugType=none \
  -p:AssemblyName=SnipTrans-lite -o dist-lite

# 可选：自带运行时的独立版（约 164MB，免装 .NET）
dotnet publish -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:DebugType=none -o dist
```

> 独立版**不要**开启 `EnableCompressionInSingleFile`：体积能压到 70MB，但常驻内存会从 200MB 涨到 340MB。

图标由 `tools/gen-icon.ps1`（GDI+ 绘制多尺寸 PNG）+ `tools/pack-ico.py`（打包为 ICO）生成。

## 结构

```
SnipTrans/
├── App.xaml(.cs)              托盘、菜单、主流程编排
├── Services/
│   ├── AppSettings.cs         settings.json 读写
│   ├── ClipboardWatcher.cs    剪贴板监听（序号轮询 + 多格式读取 + 超时保护）
│   ├── OcrService.cs          双引擎 OCR + 行/词级融合
│   ├── TranslateService.cs    多引擎翻译 + 严格分段
│   └── Log.cs                 文件日志
├── Views/
│   ├── ResultWindow.xaml      译文浮窗（深色、置顶、无边框）
│   └── SettingsWindow.xaml    设置界面
├── app.manifest               PerMonitorV2 DPI 感知
└── app.ico
```
