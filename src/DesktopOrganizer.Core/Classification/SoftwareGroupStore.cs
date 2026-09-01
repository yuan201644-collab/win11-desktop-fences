using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace DesktopOrganizer.Core.Classification;

/// <summary>
/// Persists the software purpose-box rules as a JSON file the user can read and edit. On first
/// run the file is seeded with a default table built from the desktop's known software, so the
/// box layout is customizable without touching code.
/// </summary>
public static class SoftwareGroupStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static string DefaultFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DesktopOrganizer", "software-groups.json");

    public static SoftwareGroupingConfig Load(string filePath)
    {
        try
        {
            if (!File.Exists(filePath))
            {
                var seed = Default();
                Save(filePath, seed);
                return seed;
            }
            var cfg = JsonSerializer.Deserialize<SoftwareGroupingConfig>(File.ReadAllText(filePath), Options)
                      ?? Default();
            Normalize(cfg);
            return cfg;
        }
        catch (Exception)
        {
            return Default();
        }
    }

    /// <summary>
    /// Writes the rules to disk. Keywords are normalized first (trimmed, lowercased, empties
    /// dropped) because <see cref="SoftwarePurposeClassifier"/> lowercases only the haystack and
    /// then compares keywords ordinally — a keyword typed as "Word" in the rule editor would
    /// otherwise never match. Mutates <paramref name="config"/> so the live object and the file
    /// can never disagree about what a keyword is.
    /// </summary>
    public static void Save(string filePath, SoftwareGroupingConfig config)
    {
        Normalize(config);
        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(filePath, JsonSerializer.Serialize(config, Options));
    }

    private static void Normalize(SoftwareGroupingConfig config)
    {
        foreach (var group in config.Groups)
        {
            group.Keywords = group.Keywords
                .Select(k => k.Trim().ToLowerInvariant())
                .Where(k => k.Length > 0)
                .ToList();
        }
    }

    /// <summary>
    /// Default box layout, seeded from the desktop's actual software (议题：games、办公、开发、
    /// 上网、网盘、聊天、剪辑、硬件、GIS、模组…). Keywords are lowercase stored; matching is
    /// case-insensitive substring against display-name + target exe.
    /// </summary>
    public static SoftwareGroupingConfig Default() => new()
    {
        Groups = new List<SoftwareGroup>
        {
            new("办公软件", new[]
            {
                "word", "winword", "access", "excel", "wps", "wpp", "wpsoffice", "powerpnt", "ppt.exe",
                "onenote", "outlook", "acrobat", "foxit", "sumatrapdf", "pdf", "office", "文档",
                "办公套件", "vivo", "workbuddy",
            }),
            new("开发/信息安全", new[]
            {
                "python", "py.exe", "jupyter", "pycharm", "visual studio", "vscode", "code.exe",
                "devenv", "idea64", "eclipse", "navicat", "sqlserver", "sql", "burpsuite", "vmware",
                "virtualbox", "docker", "git", "github", "postman", "node.js", "notepad++",
                "sublime", "wireshark", "kali", "火绒", "数据库", "scratch",
            }),
            new("影音娱乐/游戏", new[]
            {
                "grand theft auto", "gta", "slay the spire", "杀戮尖塔", "三角洲", "delta force",
                "明日方舟", "arknights", "鹰角", "hypergryph", "米哈游", "mihoyo", "原神", "崩坏", "绝区零", "star rail",
                "steam", "epic games", "gog", "wegame", "bilibili", "哔哩", "爱奇艺", "iqiyi",
                "youku", "qq音乐", "qqmusic", "网易云", "netease", "potplayer", "vlc", "mpv",
                "player", "影音", "视频", "音乐", "游戏", "模拟器",
            }),
            new("聊天通讯", new[]
            {
                "wechat", "微信", "qq.exe", "qq", "抖音", "douyin", "腾讯会议", "meeting", "钉钉",
                "dingtalk", "飞书", "feishu", "企业微信", "豆包", "telegram", "discord", "zoom",
            }),
            new("学习教育", new[]
            {
                "学习通", "cxstudy", "mooc", "网课", "课程",
            }),
            // 网盘 comes BEFORE 上网 on purpose: besides the netdisk apps, 上网/网络工具 carries a
            // bare "夸克", whose substring would otherwise swallow "夸克网盘" into the browser box.
            new("网盘/下载工具", new[]
            {
                "夸克网盘", "百度网盘", "baidu netdisk", "阿里云盘", "aliyun", "联想超级文件", "网盘",
                "迅雷", "xunlei", "idm", "下载",
            }),
            new("上网/网络工具", new[]
            {
                "chrome", "msedge", "edge.exe", "firefox", "opera", "夸克浏览器", "夸克", "proton",
                "v2ray", "clash", "加速器", "飞鸟", "雷神", "cc switch", "a hub", "浏览器", "vpn",
            }),
            new("办公/思维导图", new[]
            {
                "gitmind", "xmind", "mindmaster", "幕布", "亿图", "思维导图", "脑图",
            }),
            new("媒体剪辑", new[]
            {
                "剪映", "capcut", "premiere", "prem.exe", "pr.exe", "after effects", "vegas",
                "剪辑", "movie maker", "imovie",
            }),
            new("硬件工具", new[]
            {
                "legion", "nvidia", "gpu-z", "cpuz", "hwinfo", "msi afterburner", "键盘", "ajazz",
                "鼠标", "驱动", "driver", "硬件", "鲁大师", "图吧", "xear",
            }),
            new("GIS 地理工具", new[]
            {
                "qgis", "arcgis", "geoserver", "gis", "arcmap",
            }),
            new("模组工具", new[]
            {
                "openiv", "rockstar games", "paradox", "wemod", "weamod", "模组",
            }),
            new("系统小工具", new[]
            {
                "snipaste", "pixpin", "截图", "screenshot", "7zip", "7zfm", "winrar", "bandizip",
                "压缩", "everything", "powertoys", "taskmgr", "regedit", "notepad", "写字板",
                "calculator", "计算器", "清理", "parsec", "fences",
            }),
        },
    };
}