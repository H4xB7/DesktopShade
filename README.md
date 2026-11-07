# DesktopShade

**双击桌面空白处，收起所有桌面图标，只留干净的壁纸；再双击（或右键菜单）放回来。**

Left-click **twice on an empty spot of your desktop** to hide every desktop icon; do it again — or use the right-click menu — to bring them back. Single Windows 10/11 tray app, one file, no dependencies, no admin rights.

## 怎么用

| 动作 | 结果 |
|---|---|
| 左键双击桌面空白处 | 隐藏所有桌面图标 |
| 隐藏状态下再双击桌面空白处 | 恢复 |
| 隐藏状态下右击桌面 | 一层菜单：恢复图标 / Windows 原生菜单 / 开关托盘图标 / 退出 |
| 托盘图标 | 同样的开关项，还可勾选"登录时自动启动" |

不会误触发：

- **双击落在图标上不算手势**——程序向 Explorer 的图标列表做 `LVM_HITTEST` 命中测试，指针下面有图标（含文件名标签区）时什么都不做，正常打开文件不受影响。
- **隐藏状态下点桌面什么都不会发生**：图标层整个被隐藏，没有可命中的对象。
- 桌面被窗口或全屏游戏完全盖住时双击不生效——这时候看不见桌面，也就无所谓隐藏。

## 安装

去 [Releases](https://github.com/H4xB7/DesktopShade/releases/latest) 下载 `DesktopShade-1.1.zip`（发布包只放在 Releases，不进版本库），解压后双击 `install.bat`：

- 直接回车 → 装到 `%LOCALAPPDATA%\DesktopShade`
- 或输入任意路径，例如 `D:\Apps\DesktopShade`（目录会自动建）
- 不想被问：`install.bat -Target "D:\Apps\DesktopShade"`

不需要管理员权限。exe 未做数字签名，第一次运行 SmartScreen 会拦一下，点"更多信息 → 仍要运行"即可。

卸载双击 `uninstall.bat`：装在哪会从注册表自启项里反推，不用你告诉它；它会先把桌面图标恢复显示再清理。

要求：Windows 10 1903+ / Windows 11（用系统自带的 .NET Framework 4，不装任何东西）。

## 配置

同目录 `desktopshade.ini`，用记事本改，**保存后半秒内自动生效，不用重启**：

| 键 | 含义 |
|---|---|
| `autostart` | `True` / `False`，登录自启（改成 False 会立刻注销注册表项） |
| `tray` | `1` 显示托盘图标 / `0` 不显示 |
| `logclean` | 日志自动清理间隔，写法是数字+单位：`3days`、`10minutes`、`1month`（单复数都行；month 按 30 天、year 按 365 天近似） |
| `menu_show` `menu_native` `menu_tray` `menu_exit` | 隐藏态桌面右键菜单四行的**显示文字**（只改字不改功能；`menu_tray` 留空则自动写成"显示/隐藏托盘图标"） |
| `traymenu_state` `traymenu_hide` `traymenu_show` `traymenu_tip` `traymenu_auto` `traymenu_selfhide` `traymenu_quit` | 托盘右键菜单各行的显示文字 |

`menu_*` / `traymenu_*` 的值里不要用 `#` 号（.ini 的行内注释符，这几行不剥注释）。

命令行（可选）：

```
DesktopShade.exe --status            当前状态、配置/日志路径
DesktopShade.exe --hide | --show | --toggle
DesktopShade.exe --probe 960 540     诊断：在这个屏幕点双击会怎样、为什么
DesktopShade.exe --preview           在鼠标处弹一次托盘菜单（图标被折叠时用）
DesktopShade.exe --version | --exit
DesktopShade.exe --install-autostart | --uninstall-autostart
```

## 原理与已知限制

只把桌面图标层 `Progman > SHELLDLL_DefView > SysListView32` 做 `ShowWindow(SW_HIDE)`：壁纸是另一棵子树，任务栏也不碰，所以随时可完全还原。检测靠 `WH_MOUSE_LL` 低级鼠标钩子自己配对两次左键按下（低级钩子收不到 `WM_LBUTTONDBLCLK`，双击消息是 USER32 投递给目标窗口时才合成的），容差跟系统的 `SM_CXDOUBLECLK` 一致。

- 隐藏是**运行期状态**。程序被强杀时图标会暂时留在隐藏态，下次启动会自动放回来（正常退出、`--exit`、托盘里退出都会先恢复）。
- 自启是 `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` 里的一条值，指向当前这个 exe，换目录重装会自动跟着改。
- 日志 `desktopshade.log` 只在事件上写，超 256KB 滚动一代，并按 `logclean` 的间隔在启动时自动清理。
- 托盘菜单是 `NotifyIcon` 弹的，实测横向能偏 279~447 像素，所以程序在 `Opened` 里按"贴着图标往上翻"重算落点并纠正，偏差会写进日志。
- Windows 大版本改动桌面窗口结构会让程序定位失败——此时它写一行日志并放弃接管，双击退化成系统原生行为，不会崩、也不会把图标永久藏丢。

## 自己编译

```powershell
pwsh -File build.ps1          # → bin\DesktopShade.exe
pwsh -File make-package.ps1   # → DesktopShade-1.1.zip（含安装/卸载脚本与使用说明）
```

源码是单个 `src/DesktopShade.cs`（约 1200 行，只用 WinForms + user32）。

## 许可

MIT，见 `LICENSE`。
