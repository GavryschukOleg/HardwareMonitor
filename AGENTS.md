# AGENTS.md

Logitech/Loupedeck plugin (`.lplug4`) that shows CPU/GPU/RAM stats from LibreHardwareMonitor on device buttons. C# / .NET 10, root namespace `Loupedeck.HardwareMonitorPlugin`. Single project at `src/`; sln is at repo root.

## Build & dev loop

- **Prereq:** the Logitech Logi Plugin Service SDK must be installed. The csproj references `C:\Program Files\Logi\LogiPluginService\PluginApi.dll` (macOS: `/Applications/Utilities/LogiPluginService.app/...`). Build fails without it.
- **TFM is net10.0 and must match the installed SDK.** `PluginApi.dll` is a .NET 10 assembly; a mismatched `TargetFramework` (e.g. net8.0) fails with CS1705. If the installed Logi SDK version changes, adjust the TFM accordingly.
- **Do not build the csproj directly** (`dotnet build src\...csproj`): `Directory.Build.props` redirects `BaseIntermediateOutputPath` to the repo-root `obj/`, so building the csproj alone leaves stale generated `.cs` files in `src/obj/` that get globbed as source and cause duplicate-attribute (CS0579) errors. Always build from the repo root via the `.sln`.
- **Build:** `dotnet build -c Debug` (or `-c Release`) from repo root. `dotnet build -s`/`-p:...` not needed.
- **Build = hot reload.** The csproj `PostBuild` target writes a `.link` file into `%LocalAppData%\Logi\LogiPluginService\Plugins\` pointing at the output dir and fires `loupedeck:plugin/HardwareMonitor/reload`, so a running Logi Plugin Service picks up changes automatically. For dev you do not need to pack/install.
- **Debugging:** `.vscode/launch.json` attaches the coreclr debugger to `LogiPluginService.exe` (the service must already be running). `src/HardwareMonitorPlugin.csproj.user` configures it as the start program.
- **No tests, no CI, no README, not a git repo.** "Verify" means building successfully.

## Output & packaging

- Output layout: plugin DLL + dependency DLLs land in `bin/<Config>/bin/`; `src/package/` (metadata) is copied to `bin/<Config>/`. The `.link` mechanism runs the plugin in place from there. `pluginFolderWin: bin` in `LoupedeckPackage.yaml` matches this.
- Distribution package (from `.vscode/tasks.json`):
  - `logiplugintool pack ./bin/Release ./HardwareMonitor.lplug4`
  - `logiplugintool install ./HardwareMonitor.lplug4` / `logiplugintool uninstall HardwareMonitor`
- Bump `version` in `src/package/metadata/LoupedeckPackage.yaml` for releases.

## Runtime data source

- Reads `http://127.0.0.1:8085/data.json` from LibreHardwareMonitor — the LHM web server must be enabled and running, or buttons show `Offline`.
- Sensor selection in `HardwareMonitorClient.cs` is **universal**: it walks the JSON tree and tracks each sensor's hardware node from either LHM `Type` (`cpu`, `gpu*`, `memory`) or the `HardwareId` device path (e.g. `/amdcpu/0`, `/gpu-nvidia/0`, `/ram`) — the web server uses `HardwareId`, not `Type`, on hardware nodes. It then picks load/temperature/clock/VRAM by sensor `Type` + name keywords (e.g. prefer "tctl"/"tdie"/"package", "total", "average"). No vendor-specific full `SensorId` paths. `/vram` is deliberately ignored (pagefile, not physical RAM). When several GPUs exist it shows the one with the highest load. LHM JSON uses `Text` for display names (not `Name`).
- Buttons are drawn, not just text: the card command overrides `GetCommandImage` and renders with `BitmapBuilder` — title row; metric row (CPU/GPU: load% left + temp big right, RAM: big load% centered); a load-history sparkline; and footer line(s) (CPU: clock MHz, GPU: VRAM `used / total GB`, RAM: `used / total GB` + `Free: x.x GB`). The big value is colored by the metric itself (CPU/GPU temp, RAM load% — green <60 / amber <85 / red); the sparkline (and CPU/GPU load%) is colored by load%. All numeric formatting uses `CultureInfo.InvariantCulture` (dot decimal separator, matching the spec). Text is drawn with the positioned box overload `DrawText(text, x, y, w, h, color, fontSize, lineHeight, spaceHeight, fontName)` — the 4-arg `DrawText(text, color, x, y, width)` overload does NOT exist in this PluginApi (it silently binds to the origin-only font-size overload); glyph tops land ~`14 - 0.2*fontSize` px below the box y.
- One shared `HardwareMonitorClient.Shared` polls LHM every 1.5 s on a background timer and raises `SnapshotUpdated`; each button (CPU/GPU/RAM) is a `HardwareStatsCommand` that redraws on that event and samples its sparkline history once per poll. Rendering reads the cached snapshot and never blocks on HTTP. `HardwareMonitorPlugin.Unload()` calls `HardwareMonitorClient.Shutdown()`.

## Code layout gotchas

- **Actions are registered by the SDK's reflection scan, which only auto-registers `PluginDynamicCommand` types with a public parameterless constructor.** `HardwareStatsCommand` is an **abstract** base (so the scan skips it); the concrete `CpuStatsCommand`/`GpuStatsCommand`/`RamStatsCommand` subclasses each have a parameterless ctor and are auto-registered — the load log shows `Dynamic action added` ×3 and `3 dynamic actions loaded`. Don't register instances via `Plugin.AddDynamicAction(...)` from `HardwareMonitorPlugin.Load()`: multiple instances of the **same** type share the action `Name` (derived from the type name) and the SDK keeps only the last one — the earlier attempt registered three `HardwareStatsCommand` instances this way and only the RAM button appeared on the device. Also don't add a parameterless ctor to the shared base: the scan would then create ONE generic action instead of three.
- Shared helpers: `Helpers/PluginLog.cs` (init from `this.Log` in plugin ctor), `Helpers/PluginResources.cs` (embedded resources; currently unused — only `Init` is called).
- Build output / intermediates: everything (DLLs, package copy, NuGet restore, generated `.cs`) lands under repo-root `bin/` and `obj/` via `Directory.Build.props` + the csproj's `BaseOutputPath`; there is no `src/obj/`.

## Style (enforced as warnings by `src/.editorconfig`)

- `using` directives **inside** the namespace block; block-scoped namespace (not file-scoped).
- Prefer BCL type names (`String`, `Boolean`, `Byte`) over keywords — this is the codebase norm.
- `this.` qualification required on field/property/method access.
- CRLF line endings, 4-space indent, PascalCase members. `<Nullable>disable</Nullable>`, `LangVersion latest`.
