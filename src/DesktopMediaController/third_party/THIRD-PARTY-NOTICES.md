# 第三方组件声明

本工具（DesktopMediaController）在运行时分发以下第三方组件。列出它们既是许可证义务，也是为了让
后来者一眼看到"哪些能力不是我们写的"。

---

## 1. Lyricify.Lyrics.Helper 0.2.0

- **用途**：歌词的搜索、匹配、解析与 QRC/KRC 解密。本工具**不修改**其源码，仅以 NuGet 二进制形式引用。
- **来源**：<https://github.com/WXRIW/Lyricify-Lyrics-Helper>
- **许可证**：Apache License 2.0 —— 全文见同目录 `Lyricify-Lyrics-Helper-LICENSE.txt`
- **版权**：Copyright (c) WXRIW (XY Wang) 及贡献者

### 为什么该声明是必需的

Apache-2.0 第 4 条要求分发者在再分发时**随附许可证副本**。上游的 NuGet 包内**并不包含** LICENSE
文件（实测其包内容只有 `lib/`、`README.md` 与图标），所以这份副本必须由**我们自己**提供，否则
本工具的分发就构成许可违规。

### 传递依赖

`Lyricify.Lyrics.Helper` 自带以下依赖，随 NuGet 还原一并分发：

| 组件 | 用途 |
|---|---|
| Newtonsoft.Json | 上游接口的 JSON 解析 |
| SharpZipLib | QRC/KRC 解压（zlib） |
| ChineseConverter | 繁简转换 |

`Lyricify.Lyrics.Helper` 自身 README 还列出其借鉴的两个项目：LyricParser（MIT）、
163MusicLyrics（Apache-2.0）。

---

## 2. 关于 Lyricify 的界面设计（CC BY-SA 4.0）

Lyricify 的**代码**是 Apache-2.0，但其**首创界面形态**——「灵动词岛 / 妙控条 /
Live Album Cover / Lyricify Syllable / 智能引擎」——另有 CC BY-SA 4.0 声明，要求署名并同协议衍生。

本工具的歌词区（卡内固定 3 行、当前行整行或逐字高亮）**不采用**上述任一形态，因此不触发该条款。
此处记录仅作备忘：**日后若照搬那几种形态，需要补署名。**

---

## 维护提示

上游会随 QQ 音乐等接口变动持续发版。升级时改
`DesktopMediaController.csproj` 里的 `PackageReference` 版本号即可；
若上游更换许可证，需要同步更新本目录。
