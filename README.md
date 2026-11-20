# MH Performance Analyzer (source)

Source code of **MH Performance Analyzer**, a Railroader mod that records frame times, lag spikes, memory and the main-thread time of other mods, and writes an offline HTML report. Download the mod from its Nexus Mods page; this repository is here so you can read exactly what it does before you install it.

## What to look at

- **File access:** `src/OutputStore.cs` is the only class that touches files. Every path goes through `Resolve`, which refuses rooted paths and `..` and only accepts paths inside the mod's own `Output` folder. It deletes only old session folders inside Output, and never links or subfolders.
- **The report template:** `src/HtmlReport.cs` reads `report-template.html` from inside the DLL (an embedded resource), not from disk.
- **Opening things:** `Application.OpenURL` appears only in `src/Recording.cs`, called when the player clicks Open report or Open Output folder, and only for paths inside Output.
- **What is recorded:** `src/Inventory.cs` (system and mod list), `src/GameSettings.cs` (the game's graphics settings) and `src/GameContext.cs` (camera, cars, game time). No CPU or GPU model, resolution, operating system, user name, computer name, file paths or save names.
- **No network, no processes:** there is no networking or process code anywhere. Search for `System.Net`, `WebRequest`, `UnityWebRequest`, `Process` or `Socket` to check.
- **Timing:** `src/Timing.cs` and `src/Instrumenter.cs` add one Harmony prefix and finalizer pair to the methods they time. No generated code.

## Check that the release matches this source

The build is deterministic, so building this source gives a byte-identical DLL.

1. Install the .NET SDK (built with 11.0.100) and Railroader 0.6.3.
2. Build: `dotnet build src/MHPerformanceAnalyzer.csproj -c Release -p:RailroaderGameDir="<your Railroader folder>"`
3. Compare `src/bin/Release/MHPerformanceAnalyzer.dll` with the DLL in the mod's zip, for example with `Get-FileHash` in PowerShell.

| Version | MHPerformanceAnalyzer.dll SHA-256 |
|---|---|
| 1.0.0 | `C88B9EF546805E1F1088F1EFC297752898264EFAE157C20097A60ACBE0036AB5` |

`0Harmony.dll` in the zip is the unchanged Lib.Harmony 2.4.3 from NuGet.

## Licence

The source is published so you can inspect it. The licence is the same as the mod's: all rights reserved, see `LICENSE.txt`. Third-party components keep their own licences, listed in `THIRD-PARTY-NOTICES.txt`.

Made by MuerteHorrible.
