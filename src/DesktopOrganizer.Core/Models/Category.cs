namespace DesktopOrganizer.Core.Models;

public enum Category
{
    Other = 0,
    Images,
    Documents,
    Videos,
    Audio,
    Archives,
    Applications,
    Browser,
    Office,
    Dev,
    Games,
    Downloads,
    // Added to cover common Chinese-desktop app types
    Communication,   // 微信, QQ, 钉钉, etc.
    Media,           // 抖音, 哔哩哔哩, 爱奇艺, 剪映 (video platforms & editors)
    Cloud,           // 百度网盘, 阿里云盘, 夸克网盘
    Education,       // 学习通, MOOC, 学校相关
    AI,              // 豆包, Claude, ChatGPT clients
    Security,        // 火绒, 杀毒工具
    System,           // VMware, 驱动, 系统工具
}
