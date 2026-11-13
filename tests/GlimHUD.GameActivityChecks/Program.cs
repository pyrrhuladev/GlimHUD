using System.Text.Json;
using GlimHUD.Services;
using GlimHUD.Localization;

if (args is ["--diagnostics", var output])
{
    using var service = new GameActivityService();
    var snapshot = service.GetSnapshot();
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
    await File.WriteAllTextAsync(output, JsonSerializer.Serialize(new
    {
        snapshot.IsGameActive, snapshot.IsOverlayFocused, snapshot.ProcessName, snapshot.Status,
        HasExecutablePath = snapshot.ExecutablePath is not null
    }, new JsonSerializerOptions { WriteIndented = true }));
    return 0;
}

var passed = 0;
void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
    Console.WriteLine("PASS " + message);
    passed++;
}
bool IsGame(string path, string[]? configured = null) => GameApplicationClassifier.Classify(path, configured).IsGame;

Check(IsGame(@"D:\SteamLibrary\steamapps\common\Enshrouded\enshrouded.exe"), "A Steam-library game is recognized in windowed or fullscreen mode.");
Check(IsGame(@"C:\PROGRAM FILES (X86)\STEAM\STEAMAPPS\COMMON\Game\bin\game.EXE"), "Steam path classification is case-insensitive and supports nested binaries.");
Check(IsGame(@"D:\EpicGames\Fortnite\FortniteGame\Binaries\Win64\FortniteClient-Win64-Shipping.exe"), "An Epic library game is recognized.");
Check(IsGame(@"C:\Program Files\Epic Games\Satisfactory\FactoryGame\Binaries\Win64\FactoryGame.exe"), "An Epic Games directory with a space is recognized.");
Check(IsGame(@"D:\GOG Games\The Witcher 3\bin\x64\witcher3.exe"), "A GOG-library game is recognized.");
Check(IsGame(@"D:\XboxGames\Example\Content\Game.exe"), "An Xbox game installation is recognized.");
Check(!IsGame(@"C:\Program Files\Mozilla Firefox\firefox.exe"), "Fullscreen browser video never counts as a game by itself.");
Check(!IsGame(@"C:\Windows\explorer.exe"), "The desktop and Explorer do not count as games.");
Check(!IsGame(@"C:\Program Files (x86)\Steam\steam.exe"), "Steam launcher is excluded.");
Check(!IsGame(@"D:\EpicGames\Launcher\Portal\Binaries\Win64\EpicGamesLauncher.exe"), "Epic launcher is excluded.");
Check(!IsGame(@"D:\EpicGames\Launcher\Engine\Binaries\Win64\UnrelatedHelper.exe"), "Epic launcher helper directories are excluded.");
Check(!IsGame(@"D:\SteamLibrary\steamapps\common\wallpaper_engine\wallpaper64.exe"), "Steam desktop utilities are excluded.");
Check(!IsGame(@"D:\SteamLibrary\steamapps\common\Game\_CommonRedist\setup.exe"), "Redistributable setup programs do not count as games.");
Check(!IsGame(@"C:\Program Files\Epic Games\UE_5.6\Engine\Binaries\Win64\UnrealEditor.exe"), "The Unreal Editor is not classified as a game.");
Check(!IsGame(@"D:\EpicGamesFake\Game\Game.exe"), "Similar directory names cannot impersonate a game-library root.");
Check(!IsGame(@"D:\SteamLibrary\steamapps\common\file.exe"), "A file directly inside Steam common is not a game installation.");
Check(!IsGame(@"D:\Games\Standalone\game.exe"), "Unknown standalone executables remain conservative until configured.");
Check(IsGame(@"D:\Games\Standalone\game.exe", [@"d:\games\standalone\GAME.EXE"]), "An explicitly selected standalone game is recognized by exact normalized path.");
Check(!IsGame(@"D:\Games\Standalone\game.exe", [@"D:\Games\Other\game.exe"]), "Same executable basename in another folder never inherits a configured identity.");
Check(IsGame(@"C:\Program Files\Mozilla Firefox\firefox.exe", [@"C:\Program Files\Mozilla Firefox\firefox.exe"]), "A browser can only be shown as a game after an explicit executable selection.");
Check(!IsGame(@"C:\Windows\explorer.exe", [@"C:\Windows\explorer.exe"]), "System-shell exclusions still protect desktop visibility from a mistaken entry.");
Check(!IsGame("relative\\game.exe", ["relative\\game.exe"]), "Relative executable entries are rejected.");
Check(!IsGame(@"D:\SteamLibrary\steamapps\common\Game\readme.txt"), "Non-executable files are rejected.");
Check(!GameApplicationClassifier.Classify(null).IsGame, "Missing process identity safely hides the overlay.");
var languageProbe = @"D:\SteamLibrary\steamapps\common\Game\game.exe";
var english = GameApplicationClassifier.Classify(languageProbe);
Check(english.Status == "Game from the Steam library", "Game classification defaults to an English description.");
try
{
    L10n.SetLanguage("ru");
    var russian = GameApplicationClassifier.Classify(languageProbe);
    Check(russian.IsGame == english.IsGame && russian.Status == "Игра из библиотеки Steam", "Changing language updates the description and preserves game identity.");
}
finally { L10n.SetLanguage("en"); }

using (var service = new GameActivityService())
{
    var snapshot = service.GetSnapshot();
    Check(!(snapshot.IsOverlayFocused && snapshot.IsGameActive), "Own-window focus is reported separately from an active game.");
    Check(!string.IsNullOrWhiteSpace(snapshot.Status), "Real foreground sampling returns a usable status even when inspection is denied.");
    service.Dispose();
    try { service.GetSnapshot(); throw new InvalidOperationException("Disposed service still sampled."); }
    catch (ObjectDisposedException) { Check(true, "Disposed foreground service cannot resume sampling."); }
}
Console.WriteLine($"{passed} checks passed.");
return 0;
