# AGENTS.md

Logitech/Loupedeck plugin (`.lplug4`) that shows CPU/GPU/RAM stats from LibreHardwareMonitor on device buttons. C# / .NET 10, root namespace `Loupedeck.HardwareMonitorPlugin`. Single project at `src/`; sln is at repo root.

## Build & dev loop

- **Prereq:** the Logitech Logi Plugin Service SDK must be installed. The csproj references `C:\Program Files\Logi\LogiPluginService\PluginApi.dll` (macOS: `/Applications/Utilities/LogiPluginService.app/...`). Build fails without it.
- **TFM is net10.0 and must match the installed SDK.** `PluginApi.dll` is a .NET 10 assembly; a mismatched `TargetFramework` (e.g. net8.0) fails with CS1705. If the installed Logi SDK version changes, adjust the TFM accordingly.
- **Do not build the csproj directly** (`dotnet build src\...csproj`): `Directory.Build.props` redirects `BaseIntermediateOutputPath` to the repo-root `obj/`, so building the csproj alone leaves stale generated `.cs` files in `src/obj/` that get globbed as source and cause duplicate-attribute (CS0579) errors. Always build from the repo root via the `.sln`.
- **Build:** `dotnet build -c Debug` (or `-c Release`) from repo root. `dotnet build -s`/`-p:...` not needed.
- **Build = hot reload.** The csproj `PostBuild` target writes a `.link` file into `%LocalAppData%\Logi\LogiPluginService\Plugins\` pointing at the output dir and fires `loupedeck:plugin/HardwareMonitor/reload`, so a running Logi Plugin Service picks up changes automatically. For dev you do not need to pack/install.
- **Debugging:** attach the coreclr debugger to `LogiPluginService.exe` (the service must already be running). `.vscode/` is git-ignored personal config.
- **No tests, no CI.** "Verify" means building successfully and checking `%LocalAppData%\Logi\LogiPluginService\Logs\plugin_logs\HardwareMonitor.log` for `18 dynamic actions loaded` (a failed load only says `Cannot load plugin`).
- **Repo:** `github.com/GavryschukOleg/HardwareMonitor`, default branch `main`. README screenshots live in `docs/images/` and are real renders of `CardRenderer` (button tiles composed into strips), not mockups — regenerate them when a card style changes.

## Output & packaging

- Output layout: plugin DLL + dependency DLLs land in `bin/<Config>/bin/`; `src/package/` (metadata) is copied to `bin/<Config>/`. The `.link` mechanism runs the plugin in place from there. `pluginFolderWin: bin` in `LoupedeckPackage.yaml` matches this.
- Distribution package:
  - `logiplugintool pack ./bin/Release ./HardwareMonitor.lplug4`
  - `logiplugintool install ./HardwareMonitor.lplug4` / `logiplugintool uninstall HardwareMonitor`
- Bump `version` in `src/package/metadata/LoupedeckPackage.yaml` for releases.

## Runtime data source

- Reads `http://127.0.0.1:8085/data.json` from LibreHardwareMonitor — the LHM web server must be enabled and running, or buttons show `Offline`.
- Sensor selection in `HardwareMonitorClient.cs` is **universal**: it walks the JSON tree and tracks each sensor's hardware node from either LHM `Type` (`cpu`, `gpu*`, `memory`) or the `HardwareId` device path (e.g. `/amdcpu/0`, `/gpu-nvidia/0`, `/ram`) — the web server uses `HardwareId`, not `Type`, on hardware nodes. It then picks load/temperature/clock/VRAM by sensor `Type` + name keywords (e.g. prefer "tctl"/"tdie"/"package", "total", "average"). No vendor-specific full `SensorId` paths. `/vram` is deliberately ignored (pagefile, not physical RAM). When several GPUs exist it shows the one with the highest load. LHM JSON uses `Text` for display names (not `Name`).
- Buttons are drawn, not just text: the card command overrides `GetCommandImage` and renders with `BitmapBuilder` — title row; metric row (CPU/GPU: load% left + temp big right, RAM: big load% centered); a load-history sparkline; and footer line(s) (CPU: clock MHz, GPU: VRAM `used / total GB`, RAM: `used / total GB` + `Free: x.x GB`). The big value is colored by the metric itself (CPU/GPU temp, RAM load% — green <60 / amber <85 / red); the sparkline (and CPU/GPU load%) is colored by load%. All numeric formatting uses `CultureInfo.InvariantCulture` (dot decimal separator, matching the spec). Text is drawn with the positioned box overload `DrawText(text, x, y, w, h, color, fontSize, lineHeight, spaceHeight, fontName)` — the 4-arg `DrawText(text, color, x, y, width)` overload does NOT exist in this PluginApi (it silently binds to the origin-only font-size overload). See "Card styles" below for the measured box behavior.
- `HardwareMonitorClient` is a process-wide singleton (`HardwareMonitorClient.Instance`) with its own background 1.5 s `Timer` that fetches `data.json` once and fans the result out via a `SnapshotUpdated` event — CPU/GPU/RAM buttons no longer each own a client/timer/cache, so there's one HTTP poll per tick, not three. A fetch still in flight when the next tick fires is skipped (`Interlocked` guard), not queued. `GetDisplay(kind)` reads the last cached snapshot synchronously and never blocks on the network, so `GetCommandImage` (called on the SDK's render path) can't stall on a slow/hung LHM connection. The client also keeps one load history per metric (`GetHistory(kind)`, 48 points), pushed exactly once per poll in `RefreshAsync` — never from `GetCommandImage` — and shared by every card style. Card commands subscribe to `SnapshotUpdated` in their ctor and only repaint (`ActionImageChanged()`). Connection loss/restore is logged once per transition (`_isConnected`), not on every failed poll. They also self-register in a static list so `HardwareMonitorPlugin.Unload()` can dispose them and the shared client (stop timers, unsubscribe, dispose `HttpClient`) — the SDK's reflection-created command instances have no other lifecycle hook.

## Card styles (18 actions)

- `HardwareStatsCommand.cs`: abstract `HardwareCardCommand` (widget mode, `SnapshotUpdated` subscription, dispose registry, `GetCommandImage` → abstract `Render`), `HardwareStatsCommand` (one metric × `CardStyle`: Classic/Ring/Fill/BigNumber/Graph) and `SystemStatsCommand` (CPU+GPU+RAM on one button × `SystemCardStyle`: Bars/Columns/Rings), then one sealed parameterless type per action. `CpuStatsCommand`/`GpuStatsCommand`/`RamStatsCommand` are the Classic style and keep their original type and display names ("CPU"/"GPU"/"RAM") so existing button assignments survive; new styles are named like "CPU · Ring", "System · Bars".
- `CardRenderer.cs` draws all styles. Classic is the original height-based layout, unchanged. The others are laid out on a 180×180 design grid (the size of the design mockups) and mapped to the real button by `Frame` (scale + centering, fonts floored at 10 px). The PluginApi `BitmapBuilder` has `DrawArc`, `FillRectangle`, `FillCircle`, `DrawLine`, alpha colors (`new BitmapColor(color, alpha)`) but no polygon fill — the Graph style fills its area one pixel column at a time.
- **Text placement (measured, not guessed):** `BitmapBuilder.DrawText(text, x, y, w, h, …)` centers the text inside the box **both horizontally and vertically**, and wraps when the box is too narrow. With box height `font+8`, the top of digits/capitals lands `4 + 0.27*font` px below the box top, and digit height is `0.75*font`. `CardRenderer.Label` uses this plus a per-character advance-width table (measured from the SDK's own font) to place text by left/center/right edge and ink top; `FitFont` shrinks text to a width. The `Legacy*` helpers (old `14 - 0.2*font` offset, char-count widths) are kept only so the Classic style looks exactly as before. Classic does not fit on 80×80 keys (`Width90`); the other styles were checked at 80×80 and 116×116 with typical and worst-case values ("100%", "127.9 / 128 GB").
- Adding a style: add an enum value, a `CardRenderer.DrawX`, the switch case, display name/description, and three (or one) sealed parameterless types.

## Code layout gotchas

- **Actions are registered by the SDK's reflection scan, which only auto-registers `PluginDynamicCommand` types with a public parameterless constructor.** `HardwareStatsCommand` is an **abstract** base (so the scan skips it); the concrete `CpuStatsCommand`/`GpuStatsCommand`/`RamStatsCommand` subclasses each have a parameterless ctor and are auto-registered — the load log shows `Dynamic action added` ×3 and `3 dynamic actions loaded`. Don't register instances via `Plugin.AddDynamicAction(...)` from `HardwareMonitorPlugin.Load()`: multiple instances of the **same** type share the action `Name` (derived from the type name) and the SDK keeps only the last one — the earlier attempt registered three `HardwareStatsCommand` instances this way and only the RAM button appeared on the device. Also don't add a parameterless ctor to the shared base: the scan would then create ONE generic action instead of three.
- **`HardwareMonitorApplication.cs` looks like a dead template stub but is required.** Deleting it makes the service log `Cannot load plugin` and put the plugin on its disabled list (verified). Keep the empty `ClientApplication` subclass.
- Shared helper: `Helpers/PluginLog.cs` (init from `this.Log` in plugin ctor).
- Build output / intermediates: everything (DLLs, package copy, NuGet restore, generated `.cs`) lands under repo-root `bin/` and `obj/` via `Directory.Build.props` + the csproj's `BaseOutputPath`; there is no `src/obj/`.

## Style (enforced as warnings by `src/.editorconfig`)

- `using` directives **inside** the namespace block; block-scoped namespace (not file-scoped).
- Prefer BCL type names (`String`, `Boolean`, `Byte`) over keywords — this is the codebase norm.
- `this.` qualification required on field/property/method access.
- CRLF line endings, 4-space indent, PascalCase members. `<Nullable>disable</Nullable>`, `LangVersion latest`.
