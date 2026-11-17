using System;
using System.Collections.Generic;
using System.Linq;

namespace BetterGenshinImpact.Core.Config;

public enum OverlayMetricItem
{
    GameFps,
    ProcessingCost,
    PeakProcessingCost,
    CaptureCost,
    TriggerCost,
    SkippedTicks,
    CpuUsage,
    GpuUsage,
    MemoryUsage,
    BgiMemoryUsage,
    BgiCpuUsage
}

public static class OverlayMetricItemDefaults
{
    // 显式顺序同时决定遮罩三行三列显示顺序和设置页复选框顺序，不要随手改回 Enum.GetValues。
    public static IReadOnlyList<OverlayMetricItem> AllItems { get; } =
    [
        OverlayMetricItem.GameFps,
        OverlayMetricItem.ProcessingCost,
        OverlayMetricItem.PeakProcessingCost,
        OverlayMetricItem.CaptureCost,
        OverlayMetricItem.TriggerCost,
        OverlayMetricItem.SkippedTicks,
        OverlayMetricItem.GpuUsage,
        OverlayMetricItem.CpuUsage,
        OverlayMetricItem.MemoryUsage,
        OverlayMetricItem.BgiMemoryUsage,
        OverlayMetricItem.BgiCpuUsage
    ];

    public static Dictionary<string, bool> CreateDefaultItems()
    {
        return AllItems.ToDictionary(item => item.ToString(), IsEnabledByDefault);
    }

    public static bool IsEnabledByDefault(OverlayMetricItem item)
    {
        // 默认只开启低风险的常用实时指标；硬件传感器项由用户按需开启，读不到时也不会占位显示。
        return item is OverlayMetricItem.GameFps
            or OverlayMetricItem.ProcessingCost
            or OverlayMetricItem.PeakProcessingCost
            or OverlayMetricItem.CaptureCost
            or OverlayMetricItem.SkippedTicks
            or OverlayMetricItem.BgiMemoryUsage
            or OverlayMetricItem.MemoryUsage;
    }

    public static string GetDisplayName(OverlayMetricItem item)
    {
        return item switch
        {
            OverlayMetricItem.GameFps => "Game FPS",
            OverlayMetricItem.ProcessingCost => "Process Time",
            OverlayMetricItem.PeakProcessingCost => "Peak Time",
            OverlayMetricItem.CaptureCost => "Capture Time",
            OverlayMetricItem.TriggerCost => "Trigger Time",
            OverlayMetricItem.SkippedTicks => "Skipped Ticks",
            OverlayMetricItem.CpuUsage => "CPU Usage",
            OverlayMetricItem.GpuUsage => "GPU Usage",
            OverlayMetricItem.MemoryUsage => "RAM Usage",
            OverlayMetricItem.BgiMemoryUsage => "BGI RAM",
            OverlayMetricItem.BgiCpuUsage => "BGI CPU",
            _ => item.ToString()
        };
    }

    public static string GetToolTipText(OverlayMetricItem item)
    {
        return item switch
        {
            OverlayMetricItem.GameFps => "Current rendering frame rate of the game.",
            OverlayMetricItem.ProcessingCost => "Total time spent by BetterGI per cycle on capture, recognition, and triggers.",
            OverlayMetricItem.PeakProcessingCost => "Peak single-cycle processing time of BetterGI over the last 5 seconds.",
            OverlayMetricItem.CaptureCost => "Time taken to capture a single game frame.",
            OverlayMetricItem.TriggerCost => "Total time spent executing triggers in the current cycle.",
            OverlayMetricItem.SkippedTicks => "Number of scheduled ticks skipped this second because the previous cycle had not finished.",
            OverlayMetricItem.CpuUsage => "Total CPU usage; automatically hidden if unavailable.",
            OverlayMetricItem.GpuUsage => "Overall GPU usage for Genshin Impact's active GPU; automatically hidden if unavailable.",
            OverlayMetricItem.MemoryUsage => "System memory usage; automatically hidden if unavailable.",
            OverlayMetricItem.BgiMemoryUsage => "Private memory used by the BetterGI process.",
            OverlayMetricItem.BgiCpuUsage => "CPU usage of the BetterGI process.",
            _ => string.Empty
        };
    }
}
