using GlimHUD.Services;

var passed = 0;
void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
    passed++;
}
void Reject<T>(Action operation) where T : Exception
{
    try { operation(); }
    catch (T) { passed++; return; }
    throw new InvalidOperationException("The invalid startup operation was accepted.");
}

var executable = Path.Combine(Path.GetTempPath(), "GlimHUD startup checks", "GlimHUD.exe");
var store = new MemoryRegistration();
var service = new StartupService(store, executable);
Check(store.Writes == 0 && !service.ReadState().IsEnabled && store.Writes == 0,
    "Construction and reads must not register autostart.");
Check(StartupService.BuildCommand(executable) == "\"" + executable + "\"",
    "The entire executable path, including spaces, is quoted.");
Check(!StartupService.BuildCommand(executable).Contains("runas")
    && !StartupService.BuildCommand(executable).Contains("--restart"),
    "Normal login must not request elevation or inherit restart arguments.");
foreach (var bad in new[] { "GlimHUD.exe", "", Path.Combine(Path.GetTempPath(), "dotnet.exe"),
    Path.Combine(Path.GetTempPath(), "GlimHUD.cmd"), executable + "\" --other" })
    Reject<ArgumentException>(() => StartupService.BuildCommand(bad));

Check(service.SetEnabled(true) is { IsEnabled: true, UsesCurrentExecutable: true } && store.Writes == 1,
    "Explicit enable registers the current executable.");
service.SetEnabled(true);
Check(store.Writes == 1, "Repeated enable is idempotent.");
Check(service.SetEnabled(false).IsEnabled == false && store.Writes == 2,
    "Explicit disable removes the registration.");
service.SetEnabled(false);
Check(store.Writes == 2, "Repeated disable does not create a key or write.");

store.Command = "\"D:\\Old GlimHUD\\GlimHUD.exe\"";
Check(service.ReadState() is { IsEnabled: true, UsesCurrentExecutable: false },
    "An old installation is reported honestly without silently rewriting it.");
Check(store.Writes == 2, "Reading another installation must not migrate it automatically.");
service.SetEnabled(true);
Check(service.ReadState().UsesCurrentExecutable && store.Writes == 3,
    "Explicit enable can select this copy instead of an older location.");

var failedStore = new MemoryRegistration { WriteFailure = true };
var failed = new StartupService(failedStore, executable);
Reject<UnauthorizedAccessException>(() => failed.SetEnabled(true));
Check(!failed.ReadState().IsEnabled, "A rejected write must not report enabled.");
failedStore.ReadFailure = true;
Check(failed.ReadState() is { CanChange: false, UnavailableReason: StartupUnavailableReason.ReadFailed },
    "An inaccessible registry is unavailable rather than falsely enabled.");
var packagedStore = new MemoryRegistration();
var packaged = new StartupService(packagedStore, executable, packaged: true);
Check(packaged.ReadState() is { CanChange: false, UnavailableReason: StartupUnavailableReason.Packaged },
    "An undeclared packaged startup task is unavailable.");
Reject<InvalidOperationException>(() => packaged.SetEnabled(true));
Check(packagedStore.Writes == 0, "Packaged checks never write a desktop Run entry.");
var unsupported = new StartupService(new MemoryRegistration(), "dotnet.exe");
Check(!unsupported.ReadState().CanChange, "Development hosts cannot register autostart.");

Console.WriteLine($"{passed} startup checks passed; the real Windows Run entry was never modified.");

internal sealed class MemoryRegistration : IStartupRegistrationStore
{
    internal string? Command;
    internal int Writes;
    internal bool WriteFailure;
    internal bool ReadFailure;
    public string? ReadCommand() => ReadFailure ? throw new UnauthorizedAccessException() : Command;
    public void WriteCommand(string command)
    {
        if (WriteFailure) throw new UnauthorizedAccessException();
        Command = command;
        Writes++;
    }
    public void DeleteCommand()
    {
        if (WriteFailure) throw new UnauthorizedAccessException();
        Command = null;
        Writes++;
    }
}
