# BDO Companion

Windows preview with a Native AOT tracker, optional Avalonia desktop window, and rc.29 browser bridge.

## Start the preview

Extract the Windows ZIP. Copy rc.29's `bdo-market-intelligence.html` beside `Companion.exe`, or pass its path with `--planner`.

Start the tracker, then right-click the BDO Companion tray icon. Choose **Open desktop companion** or **Open browser planner**. Closing the desktop window ends its process. The tracker keeps running until you choose **Exit BDO Companion** or press Ctrl+C in its terminal.

```powershell
.\Companion.exe --tray true --sample true
```

Sample mode sends 45 of 60 uses and two seconds per craft. To try live capture, replace sample mode with the durability label's client-relative rectangle and your measured craft pace. The example coordinates are placeholders.

```powershell
.\Companion.exe --durability-region 120,340,230,56 --seconds-per-craft 2.5
```

The live reader samples only the configured region while a recognized BDO process is foreground and the sampled points are visible. Use a crop containing only the durability value, such as `45/60`. It requires two matching OCR reads before sending an update. Windows English OCR must be installed. Small occlusions between sampled points can escape the visibility check.

Open [the craft planner](http://127.0.0.1:7292/planner#craft) manually if preferred. Use `--port` if 7292 is occupied. The tracker keeps imported NA/EU market quotes in memory for recipe lookups. Quotes reset when the tracker exits.

This preview covers durability and cached quotes. Live loot and guild tracking, persistent history, external price fetching, and full native planner screens are pending. Use rc.29 for those features.

## Build from source

From the repository root, with .NET 10, Visual Studio C++ x64 tools, and a Windows SDK installed:

```powershell
dotnet publish -c Release -r win-x64 -o artifacts/native
./scripts/Build-OcrWorker.ps1 -OutputDirectory artifacts/native
dotnet publish desktop/Companion.Desktop.csproj -c Release -r win-x64 --self-contained true -o artifacts/native/desktop
./scripts/Test-Bridge.ps1 -Executable artifacts/native/Companion.exe
```

See [Contributing and releases](CONTRIBUTING.md) for branch and release conventions.

See the [product and engineering specification](docs/SPEC.md) for scope, competitor comparisons and release gates.
