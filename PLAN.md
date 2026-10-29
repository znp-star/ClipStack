# ClipStack — Windows 剪贴板管理器

> 状态：M1–M5 已实现（监听 / 入库 / 回贴 / 面板视觉 / 尺寸记忆 / 图片捕获 / 置顶 / 编辑 / 类型过滤 / 托盘与开机自启 / 定时清理 / 敏感与过期 / 设置与热键）；M6（打包与自动更新）待开发
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
  pinned_at     INTEGER,
  is_link       INTEGER NOT NULL DEFAULT 0  -- 文本且整条内容就是一个网址
);

CREATE UNIQUE INDEX ux_items_hash   ON items(content_hash);
CREATE INDEX        ix_items_recent ON items(last_used_at DESC);
CREATE INDEX        ix_items_pinned ON items(pinned, pinned_at DESC);
```

数据目录：`%LOCALAPPDATA%\ClipStack\`（`clipstack.db`、`images\`、`logs\`）。

### 写入规则

- 内容哈希已存在 → 不新建，`last_used_at = now`、`use_count + 1`，自然浮到最前。
- 单条上限 10MB；超限时图片丢弃并记为“太大未保存”，文本超限截断入库。
- `CF_HDROP` 记录路径列表（`type = 'file'`，换行分隔），不复制文件本体；回写时写回 `CF_HDROP`，不再退化成路径文本。
- 单个文件且是图片扩展名（`.png`、`.jpg`、`.bmp`、`.gif`、`.tif`）时：若剪贴板同时带像素则按 `type = 'image'` 存；否则仍存为文件条目，但列表显示缩略图与「图片」角标（缩略图直接指向该文件路径）。

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
- 置顶区与时间流之间一条细分隔线 → 行与行之间统一用浅灰细线（`EdgeBrush`）分隔，置顶块的边界自然落在其中。
- 条目显示：行首缩略图（图片 / 图片文件）、最多两行的内容摘要（超出省略号）；置顶用行尾图钉表示。来源、时间、大小不进列表，把面板留给内容本身。

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

三态：跟随系统 / 浅色 / 深色，托盘菜单与设置页均可切换，立即生效。托盘里三项平铺（不设二级菜单，理由见 M5 备注）。背景用 DWM Mica（`DWMWA_SYSTEMBACKDROP_TYPE`），跟随系统深浅色。弹入 120ms 淡入 + 8px 上移，不做弹跳。选中色用系统 accent。

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
| M2 ✅ | 面板与搜索 | 快捷键唤出、实时过滤、↑↓/Enter/Esc |
| M3 ✅ | 置顶、编辑、类型过滤 | 置顶区独立、编辑保存、过滤条可用 |
| M3.5 ✅ | 托盘、自启与定时清理 | 通知区图标（左键唤出、右键菜单）、开机自启、每小时清理 |
| M4 ✅ | 敏感与过期 | 敏感项带「未保存」角标且不落库；过期清理已随 M3.5 完成 |
| M5 ✅ | 设置与热键 | 9 个动作可改、冲突提示、主题三态 |
| M6 | 打包与自动更新 | Velopack 产物、GitHub Release 可升级 |

### M1 实现备注

- 启动参数 `--silent`：不显示面板（供开机自启与自动化测试使用）。
- 单次复制可能连发多条 `WM_CLIPBOARDUPDATE`，用「同哈希 800ms 内不重复入库」折叠。
- 面板自身占据前台时，来源应用记为未知，避免把 `ClipStack` 记成来源。

### M2 实现备注

- 面板改用 `WindowChrome`（`CaptionHeight=0`、`ResizeBorderThickness=6`、`GlassFrameThickness=-1`）：四边可拖拽缩放，且客户区交给 DWM 绘制 Mica。
- Mica 用 `DWMWA_SYSTEMBACKDROP_TYPE = 2`（需 22621+）；不可用时回退为不透明 `SurfaceBrush`，日志记 `Panel backdrop`。
- 选中色取自 DWM 系统强调色（`DwmGetColorizationColor`），透明度分层区分 hover / selected。
- 位置尺寸按显示器设备名记忆，存**物理像素 + 该显示器 DPI**，恢复时按当前 DPI 换算，避免跨屏漂移；上限为工作区 70%。
- 图片捕获：优先读 `PNG` 格式（原生保留 alpha），否则读 `CF_DIBV5`/`CF_DIB` 并自行转 PNG（`DibImage`，支持 24/32 位、BI_RGB/BI_BITFIELDS、上下行序），不引入 `System.Drawing`。
- 图片按内容哈希落盘为 `images\<hash>.png`，同图只存一份；DB 仅存路径。
- 已知限制（已于 M3 后修复）：图片回写现在同时放 `PNG` 与 `CF_DIB`（`DibImage.EncodeBgra32` 由 App 层 `DibConverter` 用 WPF 解码 PNG 生成）。实验证明 Chromium 只从 DIB 合成粘贴文件——PNG-only 时 `clipboardData.items` 为空，DIB 时得到 `file:image/png`。
- 文件条目按 `CF_HDROP` 回写（`DropFilesData`：20 字节 DROPFILES 头 + UTF-16 双空终止路径），单个 `.png` 文件额外附带 PNG 像素，兼顾「粘贴文件」与「粘贴图片」两类目标。
- 「复制图片」有两种来源：截图工具往往同时给像素（存为图片条目）；文件管理器只给文件路径（存为文件条目，靠缩略图区分）。读取时单图片文件且带像素则一律按图片处理。
- 剪贴板读写各有 10 次 × 15ms 的 `OpenClipboard` 重试：系统剪贴板历史服务与其它监听者会在写入后瞬间抢占，单次尝试经常失败。
- 来源应用图标尚未绘制，留到 M3 与条目编辑一并处理。

### M3 实现备注

- 过滤条做 全部 / 文本 / 链接 / 图片 / 文件；「敏感」要等 M4 的 `SensitivityFilter` 落地后再加，避免出现永远为空的分类（已于 M4 补齐）。`Ctrl+Tab` / `Ctrl+Shift+Tab` 循环切换，每次唤出面板都重置为「全部」。
- 链接不是第四种 `ClipType`：库里仍是 `text`，另加 `is_link` 列（`LinkText.Is`：整条内容匹配 `^(https?://|www\.)\S+$`，夹在正文里的网址不算），这样过滤、排序、分页都走 SQL，回贴行为不变。`is_link` 属于 schema v2，旧库在打开时 `ALTER TABLE` 补列并用 C# 回填分类，`user_version` 从 1 升到 2。
- 置顶排序沿用 `Query` 里的 `pinned DESC, pinned_at DESC`。行与行之间统一画浅灰细线（`DividerBrush`，左右各内缩 12px）：线是 `ListBoxItem` 模板里行背景的兄弟节点，选中高亮盖不到它；置顶块与时间流的边界因此不再需要单独的分隔线（`DividerVisibility` 已删）。置顶用行尾的裸图钉图标（Segoe Fluent Icons `E718`，无底色）：置顶行常显、强调色，未置顶行悬停才出现、灰色，点击即切换（`PinButton_Click`，点按钮不触发条目粘贴），Ctrl+P 仍作用于选中行。置顶行整体带一层极淡的强调色底（`PinnedRowBrush`）。
- 行首放缩略图（56×38，垂直居中，圆角 4、1px `EdgeBrush` 描边）。缩略图用 `ImageBrush` 当 `Border` 背景而不是 `Image` 子元素：`ClipToBounds` 只按矩形裁剪，圆角只能靠背景绘制才生效。有缩略图的行不再显示类型角标，图片条目也不显示「图片」这条预览文本。
- 行的垂直节奏：`Padding="12,7,10,7"` + 1px 分隔线，上下对称各 7px；不再给 `ListBoxItem` 加外边距，否则分隔线会贴下一行更近，看起来是「1 行 + 大空隙」。
- 选中态除了底色与 1px 描边，还在行的左内侧加一根 3px 强调色竖条（`ActiveStripe`）：面板是键盘驱动的，颜色之外还要有一个形状信号说明「Enter 会粘贴这一行」。
- 右侧滚动条换成细滚动条：`App.xaml` 里的隐式 `ScrollBar` 样式去掉上下箭头按钮与轨道底色，只留 6px 圆角滑块（10px 命中宽度），hover / 拖动两档加深；轨道空白处仍可点击翻页。样式是应用级的，编辑窗口的文本框滚动条一并跟上。
- 取消置顶（`Ctrl+P`）后，该条会落到下方时间流里，选中若跟着它走会把面板甩到很远的位置。因此取消置顶后默认回到置顶区的第一条；置顶区清空时就是列表第一条（`SelectPinnedOrFirst`）。置顶操作本身仍然跟着那条走。
- 编辑只对文本条目开放（`F2`），图片与文件给出「只有文本条目可以编辑」的提示。保存时重算内容哈希、清掉 `html` / `rtf`，否则回贴会把旧富文本粘出去；新文本与另一条重复时拒绝保存并提示。
- 编辑窗口是独立 `ToolWindow`，以面板为 Owner。打开前打上 `_suppressDeactivate`，否则面板会被自己的失焦逻辑藏起来；关闭后强制把焦点还给搜索框。
- 删除（`Delete`）走 `ItemStore.Delete`，顺带删掉图片文件；搜索框有内容时 `Delete` 仍留给文本框编辑，空搜索框才删条目。

### M3.5 实现备注

- 托盘用 `Shell_NotifyIcon` + 隐藏消息窗口（复用剪贴板监听那套 `NativeMessageWindow`）。图标不引入二进制资源：运行时用 `DrawingVisual` 画（强调色圆角方块 + 两道白条），`RenderTargetBitmap` → `CreateIconIndirect` 转成 `HICON`；失败时退回系统默认图标。
- 点击回调按通道分流：任务栏角落发 `WM_LBUTTONUP` / `WM_RBUTTONUP`，键盘激活发 `NIN_SELECT`，溢出面板还可能是 `WM_CONTEXTMENU`，四种都处理。左键/双击唤出面板，右键弹菜单。
- 右键菜单是 WPF `ContextMenu`（模板在 `App.xaml`，和面板同一套圆角扁平样式）。弹出前先 `SetForegroundWindow` 隐藏窗口，点别处才会自动关闭；`MenuItem` 的勾选列固定 16px，避免「暂停监听」勾上后菜单宽度跳变。
- Explorer 重启会抹掉所有托盘图标，监听 `TaskbarCreated` 广播后重新 `NIM_ADD`。
- 自启写 `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`，值为 `"<exe 路径>" --silent`。每次启动按 `settings.autostart` 校正一次，顺带修复程序被移动后的旧路径；失败只记日志，不阻塞启动。
- 清理时机补齐为：启动、每小时（`DispatcherTimer`）、退出前。置顶条目的免疫仍在 `ItemStore.DeleteOlderThan` 里。
- 从托盘唤出面板时前台是任务栏/桌面，粘不进去：`WindowInfo.IsShellSurface` 命中 `Shell_TrayWnd` / `Progman` / `WorkerW` 等类名时，沿用上一次记录的真实目标窗口。
- 托盘菜单也提供「暂停监听」（`ClipboardMonitor.IsPaused`）与「清空历史」（`ItemStore.Clear(includePinned: true)`，带确认框）。

### M4 实现备注

- 三层判定集中在 `SensitivityFilter.Evaluate`，顺序即优先级：① 载荷的 `Excluded`（`ExcludeClipboardContentFromMonitorProcessing`，密码管理器与系统自己会用这个格式）② 来源黑名单（进程名 KeePass / 1Password / Bitwarden / LastPass / Dashlane / Enpass / Proton Pass，窗口标题含 InPrivate / Incognito / Private Browsing / 无痕 / 隐私浏览 / 隐身模式）③ 内容正则（PEM 私钥块、`AKIA[0-9A-Z]{16}`、`gh[pousr]_…`、`sk-…`、Luhn 校验通过的 13–19 位卡号）。精度重于召回：误判只是少存几分钟，漏判就是密码落盘。
- `Excluded` 必须在 `CloseClipboard` 之前读走（`ClipboardReader` 里在 `finally` 关闭前 `WithExcluded`），来源则由 `ClipboardMonitor` 事后 `WithSource` 补上——两者都不在载荷构造时可得。判定只看文本与来源，不做图片像素识别：整张截图走来源/flag 这两层。
- 分流点只有一处，在 `App.OnClipboardChanged`：先去重折叠（同哈希 800ms），再跑判定——命中就进 `SensitiveQueue` 并直接 return，日志记 `Held sensitive … (reason)`；干净内容才 `ItemStore.AddOrTouch`，日志记 `Captured …`。去重与回环标记两条路共用，避免敏感内容绕开 800ms 折叠。
- `SensitiveQueue` 是纯内存 `List<ClipItem>`，id 从 -1 往下发，所以「这条在不在库里」等价于 `Item.IsSensitive`：删除走 `_queue.Delete`、粘贴不 bump `use_count`、置顶按钮直接隐藏。凡是拿 `row.Item.Id` 打数据库的地方，都要先过这一层判断。
- TTL 由启动时的 `SensitiveTtlMinutes`（钳 1–60，默认 10）注入，按 `CreatedAtMs` 判到期，重复粘贴不续命。清理挂在三处：`Add`、`Query`、每小时的 `PurgeExpired`，面板整天不开也会按时释放；上限 50 条 / 100MB，超出丢最旧。同内容重复复制复用原 id，面板选中不会跳。
- 合并显示在 `RefreshItems`：置顶块在上，库中未置顶项与队列项按 `LastUsedAtMs` 拼成同一条时间流；「敏感」过滤条只取队列（`_sensitiveOnly`），搜索对队列走内存 `Contains`、对库走 SQL。
- 行的区分靠两处：行尾「未保存」胶囊（`BadgeBackgroundBrush` / `BadgeForegroundBrush`，`ItemRow.SensitiveBadgeVisibility`），以及图钉按钮被 `DataTrigger IsSensitive` 压成 `Collapsed`——这条触发器必须排在样式触发器的最后，否则会被 hover / 已置顶两条重新点亮。`Ctrl+P` 与 `PinButton_Click` 另各留一层拦截并提示：库里没有这一行，`SetPinned` 的 `UPDATE` 会静默影响 0 行，看起来像点了没反应。
- 回环在这条路上更要紧：敏感项回粘时来源会变成 `ClipStack` 自己，内容正则也未必再命中（比如从密码管理器复制、内容只是一串普通密码）。所以依旧靠写入前的 `ClipStack.IgnoreNext` 标记让监听侧跳过整次更新，而不是靠内容比对。
- 敏感图片的像素只放在 `ClipItem.ImageBytes`，缩略图由 `ItemRow.FromBytes` 从内存解码，不写 `images\`。托盘「清空历史」一并 `Clear()` 队列，`OnExit` 退出前再清一次。
- 已知限制：TTL 只在启动时读一次，改设置需重启（设置窗口在 M5）；判定不看图片内容，也不看 `use_count`。

### M5 实现备注

- 九个动作集中登记在 `Hotkeys`（id / 中文标签 / 作用域 / 默认值 / 校验），设置窗口、面板查表、托盘都从这一处取，避免三份表各自漂移。
- 手势用与 UI 无关的 `HotkeyGesture`（虚拟键码 + `HotkeyModifiers`），`TryParse` 认 A–Z / 0–9 / F1–F24 与命名键；解析失败回落默认值，手改错一个字符只会回到默认，不会把动作变成死键。App 层再用 `KeyInterop` 与 WPF 的 `Key` 互转。
- 面板内快捷键改成查表匹配（`MatchAction`）。Alt 组合在 WPF 里 `e.Key` 是 `Key.System`，必须取 `e.SystemKey`，否则任何带 Alt 的绑定都匹配不上。↑↓ 与 Esc 仍是硬编码：它们描述面板怎么用，不描述面板做什么。
- `Ctrl+Shift+Tab` 的反向切换由「已配置的过滤键 + Shift」派生，不额外占一个动作位。
- 「粘贴为纯文本」走 `ClipItem.ToPastePayload(plainText: true)`，丢掉 html / rtf 两种 rendition；否则部分编辑器仍按富文本贴。
- 全局键只在 `App` 注册。设置窗口保存前先试注册（`TryRegisterGlobal`），失败就把旧键注册回去并在那一行标红——接受一个注册不上的键会让面板再也唤不出来。
- 校验三条：修饰键单独出现、全局动作不带修饰键、九个动作互相重复，都拒绝保存；重复时两行同时标红，因为不知道用户想改哪一条。
- 保存后即时生效：`ApplyHotkeys` 重绑并重排底部的提示行（提示文字由键位表生成，不再写死）；`ApplySensitiveTtl` 直接改队列 TTL；保留期变短会立刻 `PurgeExpired`；主题当场换。设置窗口关闭后把焦点还给搜索框（`ShowDialogSuppressed`），否则面板会被自己的失焦逻辑藏起来。
- 主题用两份合并字典（`Themes/Light.xaml`、`Themes/Dark.xaml`）而不是 `ThemeDictionaries`——后者是 WinUI 的概念，WPF 不支持。全应用没有硬编码颜色，换字典即换主题；强调色在深色下向白混合 40%，系统给的深蓝在 `#1F1F23` 上根本看不见。
- Mica 跟着主题走（`TryApplyMica(hwnd, dark)`）；没有 Mica 的旧系统上面板退回不透明 `SurfaceBrush`。对话框只改标题栏深色（`ApplyDarkFrame`），在 `SourceInitialized` 里做，晚开的窗口不会留着浅色标题栏。
- 「跟随系统」订阅 `SystemEvents.UserPreferenceChanged`：系统切换应用深浅色时无需重启跟随，且只在 `Theme = system` 时响应。
- 设置文件改用 `UnsafeRelaxedJsonEscaping`，否则 `Ctrl+Shift+V` 会写成 `Ctrl\u002BShift\u002BV`，手改设置文件时很难辨认。
- 托盘的主题切换用「分组标题 + 三行缩进项」而不是二级菜单：现有 `ContextMenu` 模板刻意不支持嵌套 Popup，补上要连带处理键盘导航、逐层关闭与贴屏翻边，而眼下只有「主题」一项需要分组，平铺反而少一次点击。标题是非可点的禁用项（11px、`TextTertiaryBrush`），三行用 `MenuChildItem` 缩进，既省掉三遍「主题：」前缀，也仍然一眼看出归属。等托盘出现第二个需要分组的项时再做子菜单。
- 勾选标记移到行首的固定 16px 槽位（Windows 菜单的惯例），不再放行尾：这样有没有勾选，文字都落在同一列，不会左右跳。菜单整体加 18px 模糊、3px 偏移的投影，因为它盖在任务栏上，没有投影会和任务栏糊成一片。
- 新增启动参数 `--settings`：直接打开设置窗口。它需要面板已显示（对话窗的 Owner 必须是已显示的窗口），因此与 `--silent` 同用时忽略；打开动作排在 `ApplicationIdle`，否则在 `OnStartup` 里弹模态窗会一直等外层的首次渲染。
- 设置窗口可以从托盘打开，此时面板是隐藏的。因此 `ShowDialogSuppressed` 关闭后**只在面板仍可见时**才 `ForceForeground` + `Activate`：该函数内部会 `ShowWindow(SW_RESTORE)`，对隐藏窗口调用等于把原生窗口强行显示出来，而 WPF 仍认为它隐藏，于是桌面上留下一个不绘制内容的空矩形（主题换色时还会跟着变色）。

### 面板预览浮层

- 用自管的 `Popup` 而不是 `ToolTip`：`ToolTipService` 只按指针位置显示与关闭，既跟不上键盘选中（↑↓ 换到图片条目时不弹），也无法在指针移到别的行时立刻收起——它要等下一次指针移动。
- 触发：鼠标停在某行 280 ms 后显示（扫过列表不会逐行闪），键盘 ↑↓ 立即显示（`MoveSelection` 末尾同步）。浮层同时只归属一行；指针移到不需要预览的行、移出列表、滚动、列表重建、面板隐藏或粘贴都会关掉。
- 内容：有缩略图的行显示大图（`ItemRow.PreviewImage`，长边 800 解码并按行缓存一次）；没图但文本被两行截断时显示原文。截断判断分两层——`IsPreviewTrimmed`（`BuildPreview` 自己砍过内容）与 `IsTextClipped`（`FormattedText` 按当前行宽量高度，不改动 `TextBlock` 自身布局），命中任一即可。原文上限 4000 字符，超出标注已截断。
- 位置：宽度取面板左右剩余空间中的较大者并夹在 240–520，优先放右侧（右侧窄于 300 才换左边）；浮层锚在面板而不是行容器上，否则「放在左边」等于压在列表上。
- 设置里可以关掉（`ShowPreviewTip`，默认开）。它是面板唯一会盖住其他窗口的东西，必须能关：关掉时 `NeedsTip` 直接返回 false 并收起已经弹出的一层。

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
