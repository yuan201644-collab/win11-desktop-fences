namespace DesktopOrganizer.Core.Classification;

/// <summary>
/// Top-level desktop-item kind, decided purely from the file system: is this item a
/// program/shortcut, a folder, or a document file? Used as the first grouping layer
/// so the overlay shows distinct "软件 / 文件夹 / 文件 / 其他" clusters.
/// Purpose-based sub-splitting (office after the games, etc.) can layer on top later.
/// </summary>
public enum ItemKind
{
    Software = 0,
    Folder,
    File,
    Other,
}

public static class ItemKindClassifier
{
    public static string Title(ItemKind kind) => kind switch
    {
        ItemKind.Software => "软件",
        ItemKind.Folder => "文件夹",
        ItemKind.File => "文件",
        _ => "其他",
    };

    /// <summary>
    /// Decides the kind from the icon's file-system path. Hits the filesystem to tell
    /// directories apart from files; the testable decision itself lives in
    /// <see cref="FromPath(string?,bool)"/>.
    /// </summary>
    public static ItemKind FromPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return ItemKind.Other;
        try
        {
            return FromPath(path, Directory.Exists(path));
        }
        catch
        {
            return ItemKind.Other;
        }
    }

    /// <summary>
    /// Pure decision, given whether the path is a directory. Order matters: a folder is
    /// checked first (some shortcuts live inside folders), then shortcut/executable
    /// extensions, then any other file with an extension.
    /// </summary>
    public static ItemKind FromPath(string? path, bool isDirectory)
    {
        if (string.IsNullOrWhiteSpace(path)) return ItemKind.Other;
        if (isDirectory) return ItemKind.Folder;

        var ext = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
        if (ext is "lnk" or "exe" or "url" or "com" or "bat" or "cmd" or "msi" or "appref-ms")
            return ItemKind.Software;

        return ext.Length > 0 ? ItemKind.File : ItemKind.Other;
    }

    /// <summary>
    /// Classifies a desktop item from both its display name and its (best-effort) path.
    /// When the path resolves, the filesystem decides. When it cannot (UWP / Store /
    /// shell shortcuts have no readable .lnk file on disk), the item is almost always an
    /// application — so it falls back to <see cref="ItemKind.Software"/> unless it is a
    /// known system virtual item such as the Recycle Bin.
    /// </summary>
    public static ItemKind FromEntry(string? name, string? path)
    {
        var kind = FromPath(path);
        if (kind != ItemKind.Other) return kind;

        // No resolvable path (UWP shortcuts, or files whose path lookup failed). A name that
        // still visibly carries a file extension is a document, not an application.
        if (HasFileExtension(name)) return ItemKind.File;

        return IsSystemVirtualItem(name) ? ItemKind.Other : ItemKind.Software;
    }

    private static bool HasFileExtension(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        var ext = Path.GetExtension(name).TrimStart('.').ToLowerInvariant();
        if (ext.Length == 0) return false;
        return ext is not ("lnk" or "exe" or "url" or "com" or "bat" or "cmd" or "msi" or "appref-ms");
    }

    private static bool IsSystemVirtualItem(string? name)
        => name is "回收站" or "Recycle Bin" or "此电脑" or "This PC" or "控制面板" or "Control Panel";
}