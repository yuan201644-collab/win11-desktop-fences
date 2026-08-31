namespace DesktopOrganizer.Core.Models;

/// <summary>User-facing (Chinese) titles for fence clusters. Pure data — lives in Core.</summary>
public static class CategoryDisplay
{
    public static string Title(Category c) => c switch
    {
        Category.Images => "图片",
        Category.Documents => "文档",
        Category.Videos => "视频",
        Category.Audio => "音频",
        Category.Archives => "压缩包",
        Category.Applications => "应用",
        Category.Browser => "浏览器",
        Category.Office => "办公",
        Category.Dev => "开发",
        Category.Games => "游戏",
        Category.Downloads => "下载",
        Category.Communication => "通讯",
        Category.Media => "影音",
        Category.Cloud => "网盘",
        Category.Education => "学习",
        Category.AI => "AI 助手",
        Category.Security => "安全",
        Category.System => "系统",
        _ => "其他",
    };
}