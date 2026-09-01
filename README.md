<div align="center">

# 🗂️ DesktopOrganizer · 桌面图标整理

**让你的 Windows 桌面自动分类，把杂乱图标整理进一个个半透明、可自由拖动的「分组框」——而且绝不会移动你的文件。**

![Windows](https://img.shields.io/badge/Windows%2010%2F11-supported-brightgreen?logo=windows&logoColor=white)
![.NET](https://img.shields.io/badge/.NET%209-WPF-informational?logo=dotnet)
![Language](https://img.shields.io/badge/language-C%23-blue)
![License](https://img.shields.io/badge/License-MIT-green)
![Release](https://img.shields.io/github/v/release/yuan201644-collab/win11-desktop-fences)
![Stars](https://img.shields.io/github/stars/yuan201644-collab/win11-desktop-fences)

简单、本地、无云——纯 Win32 操作真实桌面图标，不搬家文件、不动注册表。

</div>

---

## ✨ 特性

- **自动分类** —— 按文件类型、快捷方式指向的应用、名称关键字，把你的图标自动归类（规则可配置）。
- **半透明分组框** —— 每个分类一个带标题的半透明框，垫在图标旁，桌面瞬间清爽。
- **自由拖动** —— 拖住某个框，框里所有真实图标跟着走；想放哪放哪。
- **个性化配色** —— 框底、边框、标题栏、标题文字的颜色与透明度随时可调，**实时预览、自动保存**。
- **四向边距可调** —— 左右上下框边距可加可减（负数则往里收），图标永远完整露出。
- **排序与折叠** —— 框内图标按名称 / 类型 / 修改时间排；点标题栏右侧的箭头按钮（或双击标题栏）把框收成一条窄标签。
- **分类显隐与顺序** —— 不想要的框直接取消勾选（图标位置不动），上下箭头调整框的排列顺序。
- **自定义分组规则** —— 新增自己的框、填关键字，命中的快捷方式就归进去。
- **多显示器支持** —— 图标按整块虚拟桌面分组，多屏不乱。
- **记住布局** —— 整理结果自动保存，重启/重新整理后布局不丢。
- **后台常驻** —— 最小化到托盘，按需隐藏/唤出；占用桌面应用的时刻，分组框自动让位。

> 私人、非盈利项目，稳定给你用。

---

## 🖼️ 效果预览

点「整理并显示分组」即可看到：桌面图标按类别聚成一簇簇，每簇外套一个半透明标题框，点标题栏右侧的箭头按钮（或双击标题栏）可折叠成窄标签，拖动框体则整簇图标跟着走。

---

## ⬇️ 快速开始（给使用者）

**方式一：直接用现成的软件**（无需装任何环境）

1. 到 **[Releases](../../releases/latest)**（当前最新 **v1.1.0**）下载 `DesktopOrganizer.exe`；
2. 双击运行（首次运行 Windows SmartScreen 可能提示"未知发布者"，点 **更多信息 → 仍要运行**）；
3. 桌面右键取消勾选「自动排列图标」；
4. 回到程序点「**整理并显示分组**」——搞定。

**方式二：从源码构建**（给开发者）

需安装 [.NET 9 SDK](https://dotnet.microsoft.com/download)（Windows）。

```powershell
# 一键发布（生成 .\run\DesktopOrganizer.exe）
.\publish.ps1

# 或手动
dotnet build
dotnet publish src/DesktopOrganizer -c Release -r win-x64 --self-contained \
  -p:PublishSingleFile=true -o publish
```

---

## 🎛️ 个性化

打开左上角"整理并显示分组"旁边的设置区域（随主窗口一起出现）：

| 项目 | 说明 |
|------|------|
| **框底色 / 边框 / 标题栏 / 标题文字** | 颜色 + 透明度滑杆，即改即存 |
| **左右上下边距** | 控制每个框离图标的远近，可正可负 |
| **排序** | 框内图标顺序：按名称 / 按类型 / 按修改时间 |
| **分类显示** | 勾选决定哪些框显示，上下箭头决定框的排列顺序 |
| **分组规则** | 增删自定义框，给每个框填关键字 |

每次调整右上角会出现「已保存」，重启后仍保留。

---

## 🧭 它是怎么工作的

- 程序通过 Win32 找到桌面图标列表 `SysListView32`，读取每个图标的**屏幕坐标**；
- 把坐标重新排进各分类的网格，同时关掉「自动排列」让位置真正生效；
- 图标**从未移动文件**，只是改变了桌面上它们的位置——其他程序仍按原路径访问这些文件；
- 半透明分组框是垫在图标旁的界面层，鼠标点击会穿透回真实图标（打开/选中照常）。

**当前限制**：分组框目前是叠加在图标上方的界面层，因此**框底不能调到 100% 不透明**，否则会盖住图标——透明度上限已自动限制在一个安全值。

---

## 🛠️ 技术栈

| 层 | 选型 |
|----|------|
| 运行时 | .NET 9（`net9.0-windows`） |
| UI | WPF + CommunityToolkit.Mvvm |
| 依赖注入 | Microsoft.Extensions.Hosting |
| 日志 | Serilog |
| 图标控制 | P/Invoke Win32（`SysListView32`、`LVM_*`、`WorkerW` 等） |
| 测试 | xUnit（`DesktopOrganizer.Core`） |

---

## 📦 仓库结构

```
src/
  DesktopOrganizer/          # WPF 程序（MVVM）
    Views/  ViewModels/  Models/  Services/  Win32/
  DesktopOrganizer.Core/     # 纯逻辑：分类、规则、配置（可单测）
  DesktopOrganizer.Tests/    # Core 的 xUnit 测试
docs/
  superpowers/specs/         # 设计文档
```

## ✅ 跑测试

```bash
dotnet test
```

---

## ❓ 常见问题

**Q：点了「整理」但图标没动？**
先确认桌面右键「查看 → 取消勾选『自动排列图标』」，再重试。

**Q：为什么叫半透明但不透明到顶？**
见上文「它是怎么工作的」——这是叠加层的预期限制，已自动把框底透明度限制在安全范围。

**Q：文件会被移动或删除吗？**
绝不会。本工具只调整桌面图标**显示位置**，不碰磁盘上的任何文件。

---

<div align="center">

喜欢的话点个 ⭐ **Star**，遇到问题到 [Issues](../../issues) 反馈。

</div>

## 📄 License

[MIT](LICENSE)