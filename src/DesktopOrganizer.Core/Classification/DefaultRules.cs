using DesktopOrganizer.Core.Models;

namespace DesktopOrganizer.Core.Classification;

/// <summary>
/// Default classification rules for desktop icons.
///
/// Expanded to cover common Chinese-desktop applications (游戏/通讯/媒体/AI/网盘/
/// 教育/安全工具) in addition to the original English-centric set.
///
/// Classification priority (see ClassifierEngine.Classify):
///   1. Overrides (exact name → category)
///   2. Rules    (user-defined CategoryRule predicates)
///   3. LinkTargetCategories (.lnk target exe name → category)  ← THIS FILE
///   4. ExtensionCategories (file extension → category)         ← THIS FILE
///   5. KeywordCategories  (display-name contains keyword)       ← THIS FILE
///   6. Other (fallback)
/// </summary>
public static class DefaultRules
{
    // ──────────────────────────────────────────────
    //  4. Extension-based classification
    // ──────────────────────────────────────────────
    public static IReadOnlyDictionary<string, Category> ExtensionCategories { get; } =
        new Dictionary<string, Category>(StringComparer.OrdinalIgnoreCase)
        {
            // Images
            ["jpg"] = Category.Images, ["jpeg"] = Category.Images,
            ["png"] = Category.Images, ["gif"] = Category.Images,
            ["bmp"] = Category.Images, ["webp"] = Category.Images,
            ["svg"] = Category.Images, ["heic"] = Category.Images,
            ["ico"] = Category.Images,

            // Documents
            ["txt"] = Category.Documents, ["md"] = Category.Documents,
            ["pdf"] = Category.Documents, ["doc"] = Category.Documents,
            ["docx"] = Category.Documents, ["xls"] = Category.Documents,
            ["xlsx"] = Category.Documents, ["ppt"] = Category.Documents,
            ["pptx"] = Category.Documents, ["rtf"] = Category.Documents,
            ["csv"] = Category.Documents, ["ods"] = Category.Documents,

            // Videos
            ["mp4"] = Category.Videos, ["mkv"] = Category.Videos,
            ["avi"] = Category.Videos, ["mov"] = Category.Videos,
            ["webm"] = Category.Videos, ["wmv"] = Category.Videos,
            ["flv"] = Category.Videos,

            // Audio
            ["mp3"] = Category.Audio, ["wav"] = Category.Audio,
            ["flac"] = Category.Audio, ["aac"] = Category.Audio,
            ["ogg"] = Category.Audio, ["m4a"] = Category.Audio,

            // Archives
            ["zip"] = Category.Archives, ["rar"] = Category.Archives,
            ["7z"] = Category.Archives, ["tar"] = Category.Archives,
            ["gz"] = Category.Archives, ["iso"] = Category.Archives,

            // Code / Dev-related extensions
            ["cs"] = Category.Dev, ["py"] = Category.Dev,
            ["js"] = Category.Dev, ["ts"] = Category.Dev,
            ["java"] = Category.Dev, ["cpp"] = Category.Dev,
            ["h"] = Category.Dev, ["sln"] = Category.Dev,
            ["csproj"] = Category.Dev, ["json"] = Category.Dev,
            ["xml"] = Category.Dev, ["yaml"] = Category.Dev,
            ["yml"] = Category.Dev, ["sh"] = Category.Dev,
            ["bat"] = Category.Dev, ["ps1"] = Category.Dev,
            ["html"] = Category.Dev, ["css"] = Category.Dev,
            ["sql"] = Category.Dev, ["sb3"] = Category.Education,  // Scratch

            // Shapefiles / GIS data
            ["shp"] = Category.Documents, ["dbf"] = Category.Documents,
            ["prj"] = Category.Documents, ["shx"] = Category.Documents,

            // APK
            ["apk"] = Category.Applications,
        };

    // ──────────────────────────────────────────────
    //  3. LinkTarget (exe name) classification
    // ──────────────────────────────────────────────
    public static IReadOnlyDictionary<string, Category> LinkTargetCategories { get; } =
        new Dictionary<string, Category>(StringComparer.OrdinalIgnoreCase)
        {
            // ── Browser ──
            ["chrome.exe"] = Category.Browser,
            ["msedge.exe"] = Category.Browser,
            ["firefox.exe"] = Category.Browser,
            ["brave.exe"] = Category.Browser,
            ["opera.exe"] = Category.Browser,
            ["vivaldi.exe"] = Category.Browser,
            ["arc.exe"] = Category.Browser,
            ["quark.exe"] = Category.Browser,          // 夸克浏览器
            ["360se.exe"] = Category.Browser,          // 360浏览器
            ["liebao.exe"] = Category.Browser,         // 猎豹浏览器
            ["sogouexplorer.exe"] = Category.Browser,  // 搜狗浏览器

            // ── Office Suite ──
            ["winword.exe"] = Category.Office,
            ["excel.exe"] = Category.Office,
            ["powerpnt.exe"] = Category.Office,
            ["outlook.exe"] = Category.Office,
            ["onenote.exe"] = Category.Office,
            ["msaccess.exe"] = Category.Office,
            ["ksolaunch.exe"] = Category.Office,       // WPS Office
            ["et.exe"] = Category.Office,              // WPS 表格
            ["wpp.exe"] = Category.Office,             // WPS 演示
            ["wpsoffice.exe"] = Category.Office,       // WPS 主程序
            ["wpsoffice.exe"] = Category.Office,

            // ── Dev / IDE ──
            ["devenv.exe"] = Category.Dev,             // Visual Studio
            ["Code.exe"] = Category.Dev,               // VS Code
            ["windbg.exe"] = Category.Dev,
            ["git-gui.exe"] = Category.Dev,
            ["cmd.exe"] = Category.Dev,
            ["powershell.exe"] = Category.Dev,
            ["wt.exe"] = Category.Dev,                 // Windows Terminal
            ["pycharm64.exe"] = Category.Dev,          // PyCharm
            ["pycharm.exe"] = Category.Dev,
            ["python.exe"] = Category.Dev,             // Python (launcher)
            ["pythonw.exe"] = Category.Dev,
            ["node.exe"] = Category.Dev,               // Node.js
            ["dotnet.exe"] = Category.Dev,             // .NET CLI
            ["javaw.exe"] = Category.Dev,              // Java
            ["idea64.exe"] = Category.Dev,             // IntelliJ IDEA
            ["clion64.exe"] = Category.Dev,            // CLion
            ["rider64.exe"] = Category.Dev,            // Rider
            ["datagrip64.exe"] = Category.Dev,         // DataGrip
            ["navicat.exe"] = Category.Dev,            // Navicat
            ["mysqlworkbench.exe"] = Category.Dev,
            ["pgadmin3.exe"] = Category.Dev,
            ["robomongo.exe"] = Category.Dev,          // Robo 3T / MongoDB
            ["postman.exe"] = Category.Dev,
            ["fiddler.exe"] = Category.Dev,            // Fiddler
            ["charles.exe"] = Category.Dev,            // Charles Proxy
            ["wireshark.exe"] = Category.Dev,
            ["burpsuitefree.exe"] = Category.Dev,      // Burp Suite
            ["OpenIV.exe"] = Category.Dev,             // OpenIV (modding tool)
            ["qgis-ltr-bin.exe"] = Category.Dev,       // QGIS
            ["notepad++.exe"] = Category.Dev,
            ["sublime_text.exe"] = Category.Dev,

            // ── Games (launchers / platforms / tools) ──
            ["steam.exe"] = Category.Games,
            ["steamwebhelper.exe"] = Category.Games,
            ["gameservices.exe"] = Category.Games,     // Epic Games
            ["epicgameslauncher.exe"] = Category.Games,
            ["launcher.exe"] = Category.Games,         // Generic (miHoYo, etc.)
            ["bootstrapper-v2.exe"] = Category.Games,  // Paradox Launcher
            ["LauncherPatcher.exe"] = Category.Games,  // Rockstar / GTA
            ["wemod.exe"] = Category.Games,            // WeMod
            ["5EClient.exe"] = Category.Games,         // 5E game platform
            ["leigod_launcher.exe"] = Category.Games,  // 雷神加速器
            ["uu_launcher.exe"] = Category.Games,      // UU加速器
            ["xunyou.exe"] = Category.Games,           // 迅游加速器
            ["battle.net.exe"] = Category.Games,       // Blizzard
            ["origin.exe"] = Category.Games,           // EA Origin
            ["ea app.exe"] = Category.Games,           // EA App
            ["uplay.exe"] = Category.Games,            // Ubisoft Connect
            ["minecraftlauncher.exe"] = Category.Games,
            ["javaw.exe"] = Category.Games,            // Minecraft Java
            ["runtimetester.exe"] = Category.Games,    // Unity test

            // ── Media / Video Platforms & Editors ──
            ["douyin.exe"] = Category.Media,           // 抖音
            ["douyinlite.exe"] = Category.Media,       // 抖音精简版
            ["JianyingPro.exe"] = Category.Media,      // 剪映专业版
            ["Jianying.exe"] = Category.Media,         // 剪映
            ["QyClient.exe"] = Category.Media,         // 爱奇艺
            ["iqiyi_ex.exe"] = Category.Media,         // 爱奇艺(旧)
            ["qqplayer.exe"] = Category.Media,         // 腾讯视频
            ["youkuclient.exe"] = Category.Media,      // 优酷
            ["bilibili.exe"] = Category.Media,         // 哔哩哔哩
            ["bilibilimacclient.exe"] = Category.Media,
            ["vlc.exe"] = Category.Media,              // VLC player
            ["potplayer.exe"] = Category.Media,        // PotPlayer
            ["mpc-hc64.exe"] = Category.Media,         // MPC-HC
            ["mpc-be64.exe"] = Category.Media,         // MPC-BE
            ["obs64.exe"] = Category.Media,             // OBS Studio
            ["reaper.exe"] = Category.Media,           // REAPER (DAW)

            // ── Communication ──
            ["wechat.exe"] = Category.Communication,   // 微信
            ["wxwork.exe"] = Category.Communication,    // 企业微信
            ["qq.exe"] = Category.Communication,       // QQ
            ["tim.exe"] = Category.Communication,       // TIM
            ["dingtalk.exe"] = Category.Communication,  // 钉钉
            ["feishu.exe"] = Category.Communication,    // 飞书/Lark
            ["lark.exe"] = Category.Communication,
            ["discord.exe"] = Category.Communication,
            ["slack.exe"] = Category.Communication,
            ["teams.exe"] = Category.Communication,     // Microsoft Teams
            ["wemeetapp.exe"] = Category.Communication, // 腾讯会议
            ["zoom.exe"] = Category.Communication,

            // ── Cloud Storage ──
            ["BaiduNetdisk.exe"] = Category.Cloud,      // 百度网盘
            ["aDrive.exe"] = Category.Cloud,            // 阿里云盘
            ["pan.exe"] = Category.Cloud,               // 115网盘
            ["cloud drive.exe"] = Category.Cloud,       // 坚果云
            ["onedrive.exe"] = Category.Cloud,
            ["dropbox.exe"] = Category.Cloud,
            ["googledrivesync.exe"] = Category.Cloud,

            // ── AI Assistants ──
            ["Doubao.exe"] = Category.AI,              // 豆包
            ["coze.exe"] = Category.AI,                // Coze (字节跳动)
            ["chatgpt.exe"] = Category.AI,             // ChatGPT official
            ["claude.exe"] = Category.AI,              // Claude (if exists)

            // ── Education ──
            ["cxstudy.exe"] = Category.Education,       // 学习通 (超星)
            ["chaoxing.exe"] = Category.Education,      // 超星学习通
            ["yoke.js"] = Category.Education,           // 雨课堂
            ["mooc.exe"] = Category.Education,          // 中国大学MOOC
            ["msedge_proxy.exe"] = Category.Education,  // MOOC uses Edge wrapper

            // ── Security ──
            ["hws.exe"] = Category.Security,            // 火绒安全软件
            ["hsupdate.exe"] = Category.Security,        // 火绒更新
            ["360safe.exe"] = Category.Security,         // 360安全卫士
            ["360tray.exe"] = Category.Security,
            ["kismain.exe"] = Category.Security,         // 卡巴斯基

            // ── System / Drivers / Utilities ──
            ["explorer.exe"] = Category.System,
            ["mspaint.exe"] = Category.Applications,
            ["vmware.exe"] = Category.System,            // VMware Workstation
            ["vmplayer.exe"] = Category.System,
            ["parsecd.exe"] = Category.System,           // Parsec
            ["nvcplui.exe"] = Category.System,           // NVIDIA Control Panel
            ["nvidia-cpl-proxy.exe"] = Category.System,
            ["razerzone.exe"] = Category.System,         // Razer Synapse (Legion Zone)
            ["ajazz-mouse-device.exe"] = Category.System,
            ["cc-switch.exe"] = Category.Applications,    // CC Switch utility
            ["360zip.exe"] = Category.Applications,       // 360压缩
            ["bandizip.exe"] = Category.Applications,     // Bandizip
            ["7zfm.exe"] = Category.Applications,         // 7-Zip Manager
            ["everything.exe"] = Category.Applications,   // Everything search
            ["listary.exe"] = Category.Applications,      // Listary
            ["ditto.exe"] = Category.Applications,        // Ditto clipboard
            ["snipaste.exe"] = Category.Applications,     // Snipaste screenshot
            ["fluent-designer.exe"] = Category.Applications, // Fluent Designer (transparency tool)
            ["gpu-z.exe"] = Category.System,              // TechPowerUp GPU-Z
            ["cpu-z.exe"] = Category.System,
            ["crystaldiskinfo.exe"] = Category.System,
            ["gitmind.exe"] = Category.Applications,       // GitMind mind map
        };

    // ──────────────────────────────────────────────
    //  5. Keyword-based classification (display name)
    //  Used as fallback when path/lnk resolution fails.
    // ──────────────────────────────────────────────
    public static IReadOnlyDictionary<string, Category> KeywordCategories { get; } =
        new Dictionary<string, Category>(StringComparer.OrdinalIgnoreCase)
        {
            // ── Images ──
            ["screenshot"] = Category.Images,
            ["截图"] = Category.Images,
            ["壁纸"] = Category.Images,
            ["photo"] = Category.Images,
            ["照片"] = Category.Images,
            ["图片"] = Category.Images,

            // ── Archives / Backup ──
            ["backup"] = Category.Archives,
            ["备份"] = Category.Archives,
            ["archive"] = Category.Archives,

            // ── Downloads ──
            ["download"] = Category.Downloads,
            ["downloads"] = Category.Downloads,
            ["installer"] = Category.Downloads,
            ["安装包"] = Category.Downloads,
            ["setup"] = Category.Downloads,

            // ── Games (Chinese names) ──
            ["steam"] = Category.Games,
            ["游戏"] = Category.Games,
            ["启动器"] = Category.Games,                  // generic launcher hint
            ["加速器"] = Category.Games,                  // game accelerator
            ["明日方舟"] = Category.Games,
            ["三角洲"] = Category.Games,
            ["gta"] = Category.Games,
            ["trainer"] = Category.Games,
            ["wemod"] = Category.Games,
            ["slay the spire"] = Category.Games,
            ["zuma"] = Category.Games,
            ["ea sports"] = Category.Games,
            ["legion zone"] = Category.Games,             // Lenovo gaming
            ["rockstar"] = Category.Games,
            ["paradox"] = Category.Games,

            // ── Browser ──
            ["edge"] = Category.Browser,
            ["microsoft edge"] = Category.Browser,
            ["夸克"] = Category.Browser,

            // ── Office ──
            ["word"] = Category.Office,
            ["excel"] = Category.Office,
            ["powerpoint"] = Category.Office,
            ["access"] = Category.Office,
            ["wps"] = Category.Office,
            ["office"] = Category.Office,

            // ── Dev ──
            ["visual studio"] = Category.Dev,
            ["vs code"] = Category.Dev,
            ["pycharm"] = Category.Dev,
            ["python"] = Category.Dev,
            ["navicat"] = Category.Dev,
            ["qgis"] = Category.Dev,
            ["burp"] = Category.Dev,
            ["git"] = Category.Dev,
            [".claude"] = Category.Dev,
            ["agent"] = Category.Dev,
            ["开发"] = Category.Dev,
            ["编程"] = Category.Dev,
            ["代码"] = Category.Dev,
            ["api"] = Category.Dev,
            ["demo"] = Category.Dev,
            ["workflow"] = Category.Dev,

            // ── Media / Video ──
            ["抖音"] = Category.Media,
            ["douyin"] = Category.Media,
            ["哔哩哔哩"] = Category.Media,
            ["bilibili"] = Category.Media,
            ["b站"] = Category.Media,
            ["爱奇艺"] = Category.Media,
            ["剪映"] = Category.Media,
            ["视频"] = Category.Media,
            ["音乐"] = Category.Media,
            ["qq音乐"] = Category.Media,

            // ── Communication ──
            ["微信"] = Category.Communication,
            ["wechat"] = Category.Communication,
            ["qq"] = Category.Communication,
            ["钉钉"] = Category.Communication,
            ["飞书"] = Category.Communication,
            ["腾讯会议"] = Category.Communication,
            ["会议"] = Category.Communication,
            ["通讯"] = Category.Communication,

            // ── Cloud Storage ──
            ["百度网盘"] = Category.Cloud,
            ["网盘"] = Category.Cloud,
            ["阿里云盘"] = Category.Cloud,
            ["夸克网盘"] = Category.Cloud,

            // ── AI ──
            ["豆包"] = Category.AI,
            ["doubao"] = Category.AI,
            ["claude"] = Category.AI,
            ["chatgpt"] = Category.AI,
            ["ai"] = Category.AI,
            ["人工智能"] = Category.AI,
            ["脱敏"] = Category.AI,                       // "隐盾 AI 个人信息智能脱敏工具"

            // ── Education ──
            ["学习通"] = Category.Education,
            ["mooc"] = Category.Education,
            ["超星"] = Category.Education,
            ["课程"] = Category.Education,
            ["scratch"] = Category.Education,
            ["编程课"] = Category.Education,
            ["数学"] = Category.Education,
            ["实验"] = Category.Education,
            ["花名册"] = Category.Documents,             // school roster
            ["论文"] = Category.Documents,
            ["申报书"] = Category.Documents,
            ["项目"] = Category.Documents,               // school project docs
            ["报告"] = Category.Documents,
            ["进展报告"] = Category.Documents,
            ["攻略"] = Category.Documents,
            ["登记表"] = Category.Documents,
            ["调研"] = Category.Documents,
            ["评估"] = Category.Documents,
            ["对比"] = Category.Documents,
            ["算法"] = Category.Dev,                      // algorithm doc → dev-adjacent
            ["shape"] = Category.Documents,               // shapefile data
            ["栖地"] = Category.Documents,                // habitat (GIS)
            ["保留區"] = Category.Documents,

            // ── Security ──
            ["火绒"] = Category.Security,
            ["安全"] = Category.Security,
            ["杀毒"] = Category.Security,

            // ── System / Hardware ──
            ["vmware"] = Category.System,
            ["parsec"] = Category.System,
            ["nvidia"] = Category.System,
            ["驱动"] = Category.System,
            ["mouse"] = Category.System,                  // hardware driver
            ["keyboard"] = Category.System,               // hardware driver
            ["usb device"] = Category.System,
            ["联想"] = Category.System,                   // Lenovo utilities
            ["vivo"] = Category.Office,                    // vivo办公套件

            // ── Utilities ──
            ["360压缩"] = Category.Applications,
            ["压缩"] = Category.Applications,
            ["图吧"] = Category.System,                    // 图吧工具箱 (hardware info)
            ["gpu-z"] = Category.System,
            ["办公套件"] = Category.Office,
            ["文件夹"] = Category.Other,                    // "新建文件夹" etc.
            ["整理工具"] = Category.Dev,                    // our own tool
            ["workbuddy"] = Category.Dev,
        };
}
