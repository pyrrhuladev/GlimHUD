using GlimHUD.Localization;

namespace GlimHUD.Services;

/// <summary>Explicit decimal SI units; link speed is distinct from measured traffic.</summary>
public static class NetworkValueFormatter
{
    public static string FormatRate(double? bytesPerSecond, bool useBits = false)
        => bytesPerSecond is { } value && double.IsFinite(value) && value >= 0
            ? Format(value * (useBits ? 8 : 1), useBits ? BitRateUnits : ByteRateUnits)
            : "—";
    public static string FormatBytes(ulong? bytes)
        => bytes is { } value ? Format(value, L10n.Language == "ru" ? ["Б", "кБ", "МБ", "ГБ", "ТБ", "ПБ", "ЭБ"] : ["B", "kB", "MB", "GB", "TB", "PB", "EB"]) : "—";
    public static string FormatLinkSpeed(long? bitsPerSecond)
        => bitsPerSecond is > 0 ? Format(bitsPerSecond.Value, BitRateUnits) : "—";
    private static string[] BitRateUnits => L10n.Language == "ru" ? ["бит/с", "кбит/с", "Мбит/с", "Гбит/с", "Тбит/с"] : ["b/s", "kb/s", "Mb/s", "Gb/s", "Tb/s"];
    private static string[] ByteRateUnits => L10n.Language == "ru" ? ["Б/с", "кБ/с", "МБ/с", "ГБ/с", "ТБ/с"] : ["B/s", "kB/s", "MB/s", "GB/s", "TB/s"];
    private static string Format(double value, string[] units)
    {
        if (!double.IsFinite(value) || value < 0) return "—";
        var unit = 0;
        while (value >= 1000 && unit < units.Length - 1) { value /= 1000; unit++; }
        var digits = unit == 0 ? "0" : value >= 100 ? "0" : value >= 10 ? "0.#" : "0.##";
        return value.ToString(digits, L10n.Culture) + " " + units[unit];
    }
}
