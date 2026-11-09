# BDO Companion

The first Tier 1 milestone is a local C# bridge for the existing rc.29 browser planner. It listens on 127.0.0.1 and starts from the terminal. Run it from this folder with .NET 10.

```powershell
dotnet run -- --planner "C:\path\to\bdo-vision\bdo-market-intelligence.html" --sample true
```

Open [the local planner](http://127.0.0.1:7292/planner#craft) in your browser. Use `--port` to choose a different port if 7292 is busy. Keep the bridge running while using the planner. Press Ctrl+C to stop it.

For a packaged build, use `.\Companion.exe --planner "C:\path\to\bdo-vision\bdo-market-intelligence.html" --sample true` from the extracted folder.

Sample mode serves a synthetic durability event with 45 of 60 uses and two seconds per craft. Omit `--sample true` to run with an empty reading. This milestone serves the planner, exposes bridge health and durability, and records acknowledgments. Live capture, tray controls, recipe and market endpoints, history, and the optional desktop window are pending.

The Release configuration enables Native AOT. To publish for Windows x64 with the .NET Native AOT build prerequisites installed:

```powershell
dotnet publish -c Release -r win-x64
```

See [Contributing and releases](CONTRIBUTING.md) for branches, Windows checks, and versioned packages.
