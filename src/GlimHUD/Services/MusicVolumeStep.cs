namespace GlimHUD.Services;

internal static class MusicVolumeStep
{
    internal static double? Next(string? targetSession, string? displayedSession,
        double displayedPercent, double snapshotPercent, double deltaPercent)
    {
        if (string.IsNullOrWhiteSpace(targetSession) || !double.IsFinite(deltaPercent)) return null;
        var current = targetSession == displayedSession && double.IsFinite(displayedPercent)
            ? displayedPercent : snapshotPercent;
        return double.IsFinite(current) ? Math.Clamp(current + deltaPercent, 0, 100) : null;
    }
}
