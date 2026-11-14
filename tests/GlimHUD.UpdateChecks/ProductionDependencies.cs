// Source-linked updater checks never use production settings or privilege APIs.
namespace GlimHUD.Services;
internal static class SettingsStore
{
    public static string DirectoryPath => throw new InvalidOperationException("Use the injected test cache.");
}
internal static class AppRestartService
{
    public static bool IsAdministrator => false;
}
