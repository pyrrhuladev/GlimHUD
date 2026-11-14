using GlimHUD.Localization;

namespace GlimHUD.Native;

internal sealed record HotkeyBinding(int Id, uint VirtualKey, uint Modifiers, string Gesture, string LabelKey)
{
    // Keep the registration catalog language-neutral; only the displayed key name changes.
    internal string LocalizedGesture => VirtualKey switch
    {
        0x20 => L10n.Text(Gesture, "Ctrl + Alt + Пробел"),
        0x25 => L10n.Text(Gesture, "Ctrl + Alt + Влево"),
        0x27 => L10n.Text(Gesture, "Ctrl + Alt + Вправо"),
        0x26 => L10n.Text(Gesture, "Ctrl + Alt + Вверх"),
        0x28 => L10n.Text(Gesture, "Ctrl + Alt + Вниз"),
        _ => Gesture
    };
}

/// <summary>One catalog for native registration and the localized shortcut list.</summary>
internal static class HotkeyBindings
{
    internal const uint ControlAlt = 0x0001 | 0x0002;
    internal const uint NoRepeat = 0x4000;

    internal static IReadOnlyList<HotkeyBinding> All { get; } = Array.AsReadOnly(new[]
    {
        new HotkeyBinding(1, 0x47, ControlAlt | NoRepeat, "Ctrl + Alt + G", "Loc.HotkeyLock"),
        new HotkeyBinding(2, 0x48, ControlAlt | NoRepeat, "Ctrl + Alt + H", "Loc.HotkeyVisibility"),
        new HotkeyBinding(3, 0x53, ControlAlt | NoRepeat, "Ctrl + Alt + S", "Loc.HotkeySettings"),
        new HotkeyBinding(4, 0x20, ControlAlt | NoRepeat, "Ctrl + Alt + Space", "Loc.HotkeyPlayPause"),
        new HotkeyBinding(5, 0x25, ControlAlt | NoRepeat, "Ctrl + Alt + Left", "Loc.HotkeyPrevious"),
        new HotkeyBinding(6, 0x27, ControlAlt | NoRepeat, "Ctrl + Alt + Right", "Loc.HotkeyNext"),
        new HotkeyBinding(7, 0x26, ControlAlt, "Ctrl + Alt + Up", "Loc.HotkeyVolumeUp"),
        new HotkeyBinding(8, 0x28, ControlAlt, "Ctrl + Alt + Down", "Loc.HotkeyVolumeDown"),
        new HotkeyBinding(9, 0x4D, ControlAlt | NoRepeat, "Ctrl + Alt + M", "Loc.HotkeyMute")
    });
}
