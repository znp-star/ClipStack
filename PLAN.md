# ClipStack — Windows 剪贴板管理器

> 状态：M1 已实现（监听 / 入库 / 回贴链路打通），M2 起待开发
> 目标平台：Windows 11（开发机 26200 x64）
> 许可：MIT

---

## 1. 目标

Windows 11 上自用的剪贴板管理器，交互以 Win+V 为骨架，补齐内置版本缺的部分：搜索、置顶、编辑、类型过滤、可调保留期、可配置快捷键、可拖拽缩放的面板。公开发布在 GitHub，具备自动更新。

### 非目标

- 不做跨设备同步
- 不做插件体系与脚本引擎
- 不做导入合并、不做数据导出
- 不做富文本编辑、不做图片编辑
- 不做 beta 通道、不做匿名统计

---

## 2. 技术选型

| 决策 | 选择 | 理由 |
|---|---|---|
| 语言/框架 | .NET 9 + WPF | 本机 SDK 9.0.319 与 WindowsDesktop 运行时已就绪；全局热键、无边框窗口、DPI 缩放都是成熟的 Win32 互操作路径 |
| 存储 | SQLite（WAL）+ 文件 | 清理、搜索、排序都是 SQL 的活；图片进 BLOB 会让库迅速膨胀 |
| 图片 | DIB 转 PNG 落盘 | DB 只存路径，备份与清理独立 |
| 面板 | 无边框工具窗口 + DWM Mica | 视觉贴近 Win11，不引入 WinUI 3 的打包约束 |
| 快捷键 | `RegisterHotKey` | 系统级注册，冲突可探测；不做低级键盘钩子 |
| 更新 | Velopack + GitHub Releases | 非打包 WPF 应用最省事，支持差分更新 |
| 加密 | 不做自定义加密 | 数据仅本地、单用户；敏感内容根本不落盘 |

---

## 3. 架构

单进程常驻 + 托盘图标，无后台服务、无驱动。

```
ClipStack.App (WPF)
 ├─ Tray / 单实例 / 自启 / 主题
 ├─ PanelWindow（列表、搜索、过滤条、置顶区）
 ├─ SettingsWindow（热键、保留期、主题）
 └─ Updater (Velopack)
ClipStack.Core
 ├─ ClipboardMonitor   AddClipboardFormatListener + 格式提取
 ├─ ItemStore          SQLite + 图片文件
 ├─ SensitivityFilter  三层判定 → 敏感队列（内存态）
 ├─ PasteInvoker       前台窗口还原 + SendInput
 ├─ HotkeyManager      RegisterHotKey 封装与冲突检测
 └─ RetentionService   过期清理
```

### 目录结构

```
ClipStack/
  PLAN.md
  ClipStack.sln
  src/ClipStack.App/
  src/ClipStack.Core/
  tests/ClipStack.Core.Tests/
  scripts/wsl-nvim-setup.sh
```

---

## 4. 数据模型

单表，四类格式用可空列；以后要支持任意 MIME 再拆 `item_formats` 子表。

```sql
CREATE TABLE items (
  id            INTEGER PRIMARY KEY,
  content_hash  TEXT    NOT NULL,      -- SHA-256(type + 规范化内容)
  type          TEXT    NOT NULL CHECK (type IN ('text','image','file')),
  text          TEXT,
  html          TEXT,
  rtf           TEXT,
  image_path    TEXT,                  -- %LOCALAPPDATA%\ClipStack\images\xx\<hash>.png
  source_app    TEXT,
  source_title  TEXT,
  created_at    INTEGER NOT NULL,      -- Unix ms
  last_used_at  INTEGER NOT NULL,
  use_count     INTEGER NOT NULL DEFAULT 0,
  pinned        INTEGER NOT NULL DEFAULT 0,
  pinned_at     INTEGER
);

CREATE UNIQUE INDEX ux_items_hash   ON items(content_hash);
CREATE INDEX        ix_items_recent ON items(last_used_at DESC);
CREATE INDEX        ix_items_pinned ON items(pinned, pinned_at DESC);
```

数据目录：`%LOCALAPPDATA%\ClipStack\`（`clipstack.db`、`images\`、`logs\`）。

### 写入规则

- 内容哈希已存在 → 不新建，`last_used_at = now`、`use_count + 1`，自然浮到最前。
- 单条上限 10MB；超限时图片丢弃并记为“太大未保存”，文本超限截断入库。
- `CF_HDROP` 只记录路径文本（`type = 'file'`），不复制文件本体。

### 清理规则

- `last_used_at` 超过保留天数（默认 2，可调 1–30）即删除，图片文件一并删。
- `pinned = 1` 免疫过期。
- 时机：启动时、每小时一次、退出前。

---

## 5. 关键机制

### 5.1 监听

`AddClipboardFormatListener` 收到 `WM_CLIPBOARDUPDATE`（`0x031D`）后：

1. `OpenClipboard` 失败重试 5 次 × 10ms（剪贴板常年被抢）。
2. 只读取需要的格式：`CF_UNICODETEXT`、`HTML Format`、`Rich Text Format`、`PNG`/`DIB`、`CF_HDROP`。
3. 延迟渲染的数据一律在后台线程取，不在消息回调里做重活。
4. 读到 `ExcludeClipboardContentFromMonitorProcessing` 存在则直接走敏感分支。

> 接收通知的窗口必须是**真实顶层窗口**（`WS_POPUP` + `WS_EX_TOOLWINDOW`，不调用 `ShowWindow`），不能用 `HWND_MESSAGE` 消息窗口；后者收不到剪贴板通知。这样的隐藏窗口同时用于 `RegisterHotKey`。

### 5.2 自身回环

粘贴时我们要写回剪贴板，监听器会立刻收到一次“新复制”。

- 写回前注册自定义格式 `ClipStack.IgnoreNext` 并写入；监听侧检测到该格式即跳过本次事件并清除标记。
- 不采用“比对内容”这种猜测式去重。

### 5.3 敏感内容（不落盘）

三层判定，命中任一即进入内存态队列：

1. 剪贴板存在 `ExcludeClipboardContentFromMonitorProcessing`。
2. 来源进程名或窗口标题命中黑名单：KeePass、1Password、Bitwarden、LastPass、浏览器无痕（标题启发式）。
3. 高精度内容正则：PEM 私钥块、`AKIA[0-9A-Z]{16}`、`ghp_/gho_/ghs_`、`sk-[A-Za-z0-9]{32,}`、Luhn 校验通过的 13–19 位卡号。

内存态行为：

- 面板内正常显示，带“未保存”角标，归入“敏感”过滤条。
- 可搜索、可回粘。
- 默认存留 10 分钟（可调 1–60），应用退出立即清空。
- 禁用“置顶”按钮，避免绕过“永不落盘”。
- 队列上限 50 条 / 100MB，超出丢最旧。

### 5.4 粘贴回焦

1. 面板显示前记录 `GetForegroundWindow()`。
2. 选中后隐藏面板，`SetForegroundWindow(记录值)`。
3. 约 30–50ms 后 `SendInput` 发送 `Ctrl+V`。

特殊处理：

| 目标 | 行为 |
|---|---|
| Windows Terminal / VS Code 终端 | 发送 `Shift+Insert` |
| 以管理员运行的窗口（UIPI 拦截） | 只复制，不发送，面板提示 |
| mstsc 远程会话 | 只复制，不发送 |
| 目标窗口已消失 | 只复制，不发送 |

本进程不提权。

> **注意**：面板不能加 `WS_EX_NOACTIVATE`——那样收不到键盘输入。正确做法是普通工具窗口（`WS_EX_TOOLWINDOW`）正常激活，靠记录并还原前台窗口实现“粘回原处”。这是本产品最容易出边角 bug 的地方。

### 5.5 排序

- 列表顺序 = 置顶区（按 `pinned_at`）→ 其余按 `last_used_at` 倒序。
- 选中并粘贴后，该条 `last_used_at` 刷新，自然回到顶部；这是“最近使用排序”，不是改数字顺序。
- 新复制插入即最前。
- 置顶是独立维度，不参与自动重排。

---

## 6. 交互规格

### 面板

- 默认 420×520；最小 320×240；最大工作区 70%。
- 四边四角可拖拽缩放；首次居中，之后按显示器分别记忆 `(x, y, w, h, dpi)`。
- 打开即聚焦搜索框；↑↓ 移动选中（焦点留在搜索框）；Enter 粘贴；Esc 有内容先清空、空则关闭。
- 鼠标滚轮滚动、单击粘贴。
- 顶部：搜索框 + 过滤条（全部 / 文本 / 图片 / 文件 / 敏感）。
- 置顶区与时间流之间一条细分隔线。
- 条目显示：内容摘要、来源应用图标、时间、`未保存` 角标（仅敏感）。

### 快捷键（9 个可配置动作）

作用域分两类：**全局**用 `RegisterHotKey`，**面板内**在面板获得焦点时生效。

| 动作 | 作用域 | 默认值 |
|---|---|---|
| 唤出面板 | 全局 | `Ctrl+Shift+V` |
| 聚焦搜索 | 面板 | `Ctrl+F` |
| 粘贴 | 面板 | `Enter` |
| 粘贴为纯文本 | 面板 | `Ctrl+Enter` |
| 删除条目 | 面板 | `Delete` |
| 编辑条目 | 面板 | `F2` |
| 置顶/取消置顶 | 面板 | `Ctrl+P` |
| 切换过滤条 | 面板 | `Ctrl+Tab` |
| 清空历史 | 面板 | `Ctrl+Shift+Delete` |

冲突处理：九个动作之间不许重复；全局键注册失败提示“被其他程序占用，请换一个”。不扫描全系统热键。

### 主题

三态：跟随系统 / 浅色 / 深色，托盘二级菜单与设置页均可切换，立即生效。背景用 DWM Mica（`DWMWA_SYSTEMBACKDROP_TYPE`），跟随系统深浅色。弹入 120ms 淡入 + 8px 上移，不做弹跳。选中色用系统 accent。

### 托盘与生命周期

- 左键唤出面板；右键：暂停/恢复监听、清空历史、设置、退出。
- 开机自启默认开启，可在设置关闭。
- 单实例：命名 Mutex；第二次启动唤出已有实例面板。

---

## 7. 设置项

| 键 | 默认 | 说明 |
|---|---|---|
| `retention_days` | 2 | 1–30 |
| `sensitive_ttl_minutes` | 10 | 1–60 |
| `max_item_bytes` | 10485760 | 单条上限 |
| `theme` | `system` | `system` / `light` / `dark` |
| `autostart` | `true` | 开机自启 |
| `hotkeys.*` | 见上表 | 9 个动作 |
| `panel.geometry.<monitor>` | — | 按显示器记忆 |

---

## 8. WSL / nvim 桥

只走文本，不承诺图片。

- 仓库提供 `scripts/wsl-nvim-setup.sh`：幂等安装 `win32yank`，向 `~/.config/nvim` 追加 clipboard provider 配置。
- README 同时给三行手动步骤。
- 不做 WSL 侧后台 daemon。

---

## 9. 发布与更新

- 公开仓库，MIT。
- Release 产物：`Setup.exe`（Velopack）+ 便携 zip。
- 更新走 GitHub Releases，应用内提示“重启生效”，只做 stable 通道。
- 不签名：接受首次运行 SmartScreen 提示，README 提供 SHA256 校验方式。

---

## 10. 日志与诊断

- 滚动日志保留 7 天，写入 `%LOCALAPPDATA%\ClipStack\logs\`。
- 全局未处理异常捕获，崩溃后提示日志路径。
- 不上报任何数据。

---

## 11. 测试范围

只覆盖四块会静默出错的逻辑：

1. 内容去重与哈希
2. 过期清理（含置顶免疫）
3. 敏感判定（三层规则）
4. 自身写入标记（回环）

UI 与 Win32 交互不铺自动化测试。

---

## 12. 里程碑

| 阶段 | 内容 | 验收 |
|---|---|---|
| M1 ✅ | 监听、入库、粘贴链路 | 复制文本 → 出现在列表 → 选中粘贴回原窗口 |
| M2 | 面板与搜索 | 快捷键唤出、实时过滤、↑↓/Enter/Esc |
| M3 | 置顶、编辑、类型过滤 | 置顶区独立、编辑保存、过滤条可用 |
| M4 | 敏感与过期 | 敏感项带角标不落库、过期自动清理 |
| M5 | 设置与热键 | 9 个动作可改、冲突提示、主题三态 |
| M6 | 打包与自动更新 | Velopack 产物、GitHub Release 可升级 |

### M1 实现备注

- 启动参数 `--silent`：不显示面板（供开机自启与自动化测试使用）。
- 单次复制可能连发多条 `WM_CLIPBOARDUPDATE`，用「同哈希 800ms 内不重复入库」折叠。
- 面板自身占据前台时，来源应用记为未知，避免把 `ClipStack` 记成来源。
- 图片格式捕获留到 M2（DIB→PNG 需要图像栈）。

---

## 13. 已知风险

- **焦点还原**：UWP 窗口、全屏游戏、权限提升窗口上不可靠，必须有“只复制”降级路径。
- **剪贴板占用**：`OpenClipboard` 常被抢，重试与超时必须到位。
- **大图转换**：DIB→PNG 在 UI 线程会卡，放后台。
- **WPF + Mica**：需要 P/Invoke `DwmSetWindowAttribute`，且高对比度主题下要退化为纯色。
- **Velopack 更新**：更新时若程序正在写 DB，需先停监听再退出。
- **WSL 只过文本**：终端里复制图片不存在，UI 不要给承诺。

---

## 14. 参考：两条既有路线的取舍

- Win+V：系统服务（`cbdhsvc`）负责历史与加密落盘，`TextInputHost` 里的 `PasteControl` 负责面板。我们复刻的是交互，不是那套服务化架构。
- CopyQ：Qt 应用 + 插件 + QJSEngine 脚本。能力上限高，代价是体积与复杂度；本方案明确放弃这一层。

---

## 15. 实现默认值（可改）

1. 搜索只查 `text` 列与文件类型条目的路径文本，不索引 HTML/RTF。
2. 内存敏感队列上限 50 条 / 100MB。
3. UI 先只做中文，i18n 结构保留但不翻译。
