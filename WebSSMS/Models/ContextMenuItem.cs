using Microsoft.AspNetCore.Components;

namespace WebSSMS.Models;

public class ContextMenuItem
{
    public string Label { get; set; } = string.Empty;
    public string? Shortcut { get; set; }
    public RenderFragment? Icon { get; set; }
    public bool IsEnabled { get; set; } = true;
    public bool IsSeparator { get; set; }
    public Func<Task>? OnClick { get; set; }

    public static ContextMenuItem Separator() => new() { IsSeparator = true };
}
