using System.ComponentModel;
using System.Diagnostics;
using GlimHUD.Services;

if (args.Contains("--fixture-sleep"))
{
    await Task.Delay(5000);
    return;
}

var passed = 0;
void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
    passed++;
}
void Reject<T>(Action action, string message) where T : Exception
{
    try { action(); }
    catch (T) { passed++; return; }
    throw new InvalidOperationException(message);
}

Check(AppRestartService.GetRestartParent([], 99) is null, "Normal startup must not wait on a process.");
Check(AppRestartService.GetRestartParent(["--restart-from", "123", "--settings"], 99) == 123, "The explicit parent PID must be retained.");
foreach (var invalid in new[] {
    new[] { "--restart-from" }, new[] { "--restart-from", "0" },
    new[] { "--restart-from", "-4" }, new[] { "--restart-from", "99" },
    new[] { "--restart-from", "12;run.exe" }, new[] { "--restart-from", " 123" },
    new[] { "--restart-from", "123", "--restart-from", "124" } })
    Reject<ArgumentException>(() => AppRestartService.GetRestartParent(invalid, 99), "Invalid restart arguments were accepted.");

var executable = Path.Combine(Path.GetTempPath(), "GlimHUD restart checks", "GlimHUD.exe");
var start = AppRestartService.CreateStartInfo(executable, 123);
Check(start.FileName == executable && start.WorkingDirectory == Path.GetDirectoryName(executable), "Executable paths with spaces must remain one path.");
Check(start.UseShellExecute && start.Verb == "runas" && start.WindowStyle == ProcessWindowStyle.Hidden, "Elevation must use the explicit Windows shell verb without a visible console.");
Check(start.ArgumentList.SequenceEqual(new[] { "--restart-from", "123", "--settings" }) && start.Arguments.Length == 0, "Restart arguments must stay separate from shell text.");
Reject<ArgumentException>(() => AppRestartService.CreateStartInfo("GlimHUD.exe", 123), "A relative executable was accepted.");
Reject<ArgumentException>(() => AppRestartService.CreateStartInfo(Path.Combine(Path.GetTempPath(), "dotnet.exe"), 123), "The dotnet development host must not be elevated.");
Reject<ArgumentException>(() => AppRestartService.CreateStartInfo(Path.Combine(Path.GetTempPath(), "GlimHUD.cmd"), 123), "A command script was accepted.");
Reject<ArgumentOutOfRangeException>(() => AppRestartService.CreateStartInfo(executable, 0), "An invalid parent PID was accepted.");

var launchCalls = 0;
Check(!AppRestartService.RequestElevation(executable, 123, info =>
{
    launchCalls++;
    throw new Win32Exception(1223);
}), "Declining UAC must keep the current instance running.");
Check(launchCalls == 1, "A declined request must not automatically retry.");
Reject<Win32Exception>(() => AppRestartService.RequestElevation(executable, 123, info => throw new Win32Exception(5)), "Other launch failures must remain visible to the UI.");
Reject<InvalidOperationException>(() => AppRestartService.RequestElevation(executable, 123, info => null), "A missing child process must not shut down the current instance.");
Check(AppRestartService.RequestElevation(executable, 123, info => new Process()), "An accepted launcher result must be reported.");
Check(await AppRestartService.WaitForRestartParentAsync([]), "Ordinary startup must continue immediately.");
Check(await AppRestartService.WaitForRestartParentAsync(["--restart-from", int.MaxValue.ToString()]), "An already exited parent must not block startup.");

var childStart = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
childStart.ArgumentList.Add("--fixture-sleep");
using (var child = Process.Start(childStart) ?? throw new InvalidOperationException("Could not start the test fixture."))
{
    try
    {
        var clock = Stopwatch.StartNew();
        Check(!await AppRestartService.WaitForRestartParentAsync(["--restart-from", child.Id.ToString()], TimeSpan.FromMilliseconds(80)), "A live parent must time out instead of starting a second instance.");
        Check(clock.Elapsed < TimeSpan.FromSeconds(2), "The restart wait exceeded its bound.");
    }
    finally
    {
        if (!child.HasExited) child.Kill();
        await child.WaitForExitAsync();
    }
}
Console.WriteLine($"{passed} restart checks passed; no elevation request was invoked.");
