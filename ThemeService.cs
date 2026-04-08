using System.Windows;
using System.Windows.Media;

namespace Schmube;

public static class ThemeService
{
    public static void ApplyTheme(bool isDarkMode)
    {
        var resources = Application.Current.Resources;
        if (resources is null)
        {
            return;
        }

        if (isDarkMode)
        {
            SetBrush(resources, "WindowBackgroundBrush", "#0B1220");
            SetBrush(resources, "SurfaceBrush", "#111827");
            SetBrush(resources, "SurfaceAltBrush", "#1F2937");
            SetBrush(resources, "SurfaceMutedBrush", "#0F172A");
            SetBrush(resources, "InputBackgroundBrush", "#0F172A");
            SetBrush(resources, "BorderBrush", "#374151");
            SetBrush(resources, "DividerBrush", "#475569");
            SetBrush(resources, "PrimaryTextBrush", "#E5E7EB");
            SetBrush(resources, "SecondaryTextBrush", "#CBD5E1");
            SetBrush(resources, "TertiaryTextBrush", "#94A3B8");
            SetBrush(resources, "InverseTextBrush", "#F9FAFB");
            SetBrush(resources, "InverseMutedTextBrush", "#CBD5E1");
            SetBrush(resources, "StatusSurfaceBrush", "#020617");
            SetBrush(resources, "PlayerBackgroundBrush", "#020617");
            SetBrush(resources, "PlayerChromeBrush", "#111827");
            SetBrush(resources, "DangerTextBrush", "#FCA5A5");
            SetBrush(resources, "AccentTextBrush", "#93C5FD");
            SetBrush(resources, "SelectionBrush", "#1D4ED8");
            SetBrush(resources, "SelectionTextBrush", "#FFFFFF");
            SetBrush(resources, "HoverBrush", "#273449");
            SetBrush(resources, "HoverBorderBrush", "#475569");
            SetBrush(resources, "PressedBrush", "#334155");
            ApplySystemBrushes(
                resources,
                controlBrush: "#1F2937",
                controlLightBrush: "#334155",
                controlDarkBrush: "#0F172A",
                controlTextBrush: "#E5E7EB",
                windowBrush: "#111827",
                windowTextBrush: "#E5E7EB",
                highlightBrush: "#1D4ED8",
                highlightTextBrush: "#FFFFFF",
                inactiveHighlightBrush: "#334155",
                inactiveHighlightTextBrush: "#E5E7EB",
                grayTextBrush: "#94A3B8",
                menuBrush: "#111827",
                menuTextBrush: "#E5E7EB",
                infoBrush: "#1F2937",
                infoTextBrush: "#E5E7EB");
        }
        else
        {
            SetBrush(resources, "WindowBackgroundBrush", "#F3F4F6");
            SetBrush(resources, "SurfaceBrush", "#FFFFFF");
            SetBrush(resources, "SurfaceAltBrush", "#F9FAFB");
            SetBrush(resources, "SurfaceMutedBrush", "#F3F4F6");
            SetBrush(resources, "InputBackgroundBrush", "#FFFFFF");
            SetBrush(resources, "BorderBrush", "#E5E7EB");
            SetBrush(resources, "DividerBrush", "#D1D5DB");
            SetBrush(resources, "PrimaryTextBrush", "#111827");
            SetBrush(resources, "SecondaryTextBrush", "#4B5563");
            SetBrush(resources, "TertiaryTextBrush", "#6B7280");
            SetBrush(resources, "InverseTextBrush", "#F9FAFB");
            SetBrush(resources, "InverseMutedTextBrush", "#D1D5DB");
            SetBrush(resources, "StatusSurfaceBrush", "#111827");
            SetBrush(resources, "PlayerBackgroundBrush", "#0F172A");
            SetBrush(resources, "PlayerChromeBrush", "#111827");
            SetBrush(resources, "DangerTextBrush", "#FCA5A5");
            SetBrush(resources, "AccentTextBrush", "#93C5FD");
            SetBrush(resources, "SelectionBrush", "#2563EB");
            SetBrush(resources, "SelectionTextBrush", "#FFFFFF");
            SetBrush(resources, "HoverBrush", "#E5E7EB");
            SetBrush(resources, "HoverBorderBrush", "#CBD5E1");
            SetBrush(resources, "PressedBrush", "#D1D5DB");
            ApplySystemBrushes(
                resources,
                controlBrush: "#F9FAFB",
                controlLightBrush: "#FFFFFF",
                controlDarkBrush: "#E5E7EB",
                controlTextBrush: "#111827",
                windowBrush: "#FFFFFF",
                windowTextBrush: "#111827",
                highlightBrush: "#2563EB",
                highlightTextBrush: "#FFFFFF",
                inactiveHighlightBrush: "#D1D5DB",
                inactiveHighlightTextBrush: "#111827",
                grayTextBrush: "#6B7280",
                menuBrush: "#FFFFFF",
                menuTextBrush: "#111827",
                infoBrush: "#F9FAFB",
                infoTextBrush: "#111827");
        }
    }

    private static void SetBrush(ResourceDictionary resources, string key, string hexColor)
    {
        resources[key] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hexColor));
    }

    private static void SetBrush(ResourceDictionary resources, object key, string hexColor)
    {
        resources[key] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hexColor));
    }

    private static void ApplySystemBrushes(
        ResourceDictionary resources,
        string controlBrush,
        string controlLightBrush,
        string controlDarkBrush,
        string controlTextBrush,
        string windowBrush,
        string windowTextBrush,
        string highlightBrush,
        string highlightTextBrush,
        string inactiveHighlightBrush,
        string inactiveHighlightTextBrush,
        string grayTextBrush,
        string menuBrush,
        string menuTextBrush,
        string infoBrush,
        string infoTextBrush)
    {
        SetBrush(resources, SystemColors.ControlBrushKey, controlBrush);
        SetBrush(resources, SystemColors.ControlLightBrushKey, controlLightBrush);
        SetBrush(resources, SystemColors.ControlDarkBrushKey, controlDarkBrush);
        SetBrush(resources, SystemColors.ControlTextBrushKey, controlTextBrush);
        SetBrush(resources, SystemColors.WindowBrushKey, windowBrush);
        SetBrush(resources, SystemColors.WindowTextBrushKey, windowTextBrush);
        SetBrush(resources, SystemColors.HighlightBrushKey, highlightBrush);
        SetBrush(resources, SystemColors.HighlightTextBrushKey, highlightTextBrush);
        SetBrush(resources, SystemColors.InactiveSelectionHighlightBrushKey, inactiveHighlightBrush);
        SetBrush(resources, SystemColors.InactiveSelectionHighlightTextBrushKey, inactiveHighlightTextBrush);
        SetBrush(resources, SystemColors.GrayTextBrushKey, grayTextBrush);
        SetBrush(resources, SystemColors.MenuBrushKey, menuBrush);
        SetBrush(resources, SystemColors.MenuTextBrushKey, menuTextBrush);
        SetBrush(resources, SystemColors.InfoBrushKey, infoBrush);
        SetBrush(resources, SystemColors.InfoTextBrushKey, infoTextBrush);
    }
}
