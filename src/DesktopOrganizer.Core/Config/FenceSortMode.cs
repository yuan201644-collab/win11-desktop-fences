namespace DesktopOrganizer.Core.Config;

/// <summary>How icons inside each fence are ordered by <c>ArrangeIntoFence</c>.</summary>
public enum FenceSortMode
{
    /// <summary>By display name (A→Z).</summary>
    Name,

    /// <summary>By item kind/category, then name.</summary>
    Type,

    /// <summary>By the target file's last-write time (newest last), then name.</summary>
    Modified,
}