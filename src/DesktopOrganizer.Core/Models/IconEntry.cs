namespace DesktopOrganizer.Core.Models;

public sealed record IconEntry(
    int Index,
    string Name,
    string Path,
    string? LinkTargetApp,
    Category Category = Category.Other);
