namespace Loupedeck.HardwareMonitorPlugin
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Linq;
    using System.Net.Http;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using System.Timers;

    internal enum HardwareStatKind
    {
        Cpu,
        Gpu,
        Ram,
    }

    internal sealed class HardwareMonitorClient : IDisposable
    {
        private static readonly Uri DefaultApiUri = new("http://127.0.0.1:8085/data.json");

        private static readonly HashSet<String> HardwareTypes = new(StringComparer.OrdinalIgnoreCase)
        {
            "computer",
            "motherboard",
            "superio",
            "cpu",
            "memory",
            "gpu",
            "gpunvidia",
            "gpuamd",
            "gpuintel",
            "gpuintelarc",
            "graphicscard",
            "storage",
            "network",
            "cooler",
            "battery",
            "psu",
            "embeddedcontroller",
            "sensortree",
            "thermalzone",
            "fancontroller",
        };

        // Single shared instance polled in the background: CPU/GPU/RAM buttons used to
        // each own a client, cache and timer, tripling the HTTP load on LibreHardwareMonitor
        // for identical data. All three buttons now read the same cached snapshot.
        public static readonly HardwareMonitorClient Instance = new();

        private readonly HttpClient _httpClient;
        private readonly System.Timers.Timer _pollTimer;

        // 0/1 guard (via Interlocked) instead of a lock: if a fetch is still in flight when
        // the next tick fires (slow/hung network), the tick is skipped rather than queued.
        private Int32 _isFetching;

        // null = no poll finished yet. Only touched inside FetchSnapshotAsync, which the guard above serializes.
        private Boolean? _isConnected;

        private volatile HardwareSnapshot _snapshot = HardwareSnapshot.Unavailable("Waiting for LibreHardwareMonitor");
        private Boolean _disposed;

        // One load history per metric, shared by every card style, one point per poll.
        private const Int32 HistoryLength = 48;
        private readonly List<Double>[] _history = { new(), new(), new() };
        private readonly Object _historyLock = new();

        // Raised after each poll (success or failure) so buttons can repaint on new data
        // instead of each polling the network independently.
        public event EventHandler SnapshotUpdated;

        private HardwareMonitorClient()
        {
            this._httpClient = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(2),
            };

            this._pollTimer = new System.Timers.Timer(1500) { AutoReset = true };
            this._pollTimer.Elapsed += this.OnPollTimerElapsed;
            this._pollTimer.Start();

            _ = this.RefreshAsync();
        }

        private void OnPollTimerElapsed(Object sender, ElapsedEventArgs e) => _ = this.RefreshAsync();

        private async Task RefreshAsync()
        {
            if (Interlocked.CompareExchange(ref this._isFetching, 1, 0) != 0)
            {
                return;
            }

            try
            {
                var snapshot = await this.FetchSnapshotAsync().ConfigureAwait(false);
                this._snapshot = snapshot;
                this.PushHistory(snapshot);
                this.SnapshotUpdated?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                PluginLog.Warning(ex, "Failed to refresh hardware snapshot");
            }
            finally
            {
                Interlocked.Exchange(ref this._isFetching, 0);
            }
        }

        // Reads are non-blocking: they return whatever the background poll last cached,
        // never wait on the network themselves.
        public HardwareDisplayData GetDisplay(HardwareStatKind kind) => this._snapshot.ToDisplay(kind);

        public Double[] GetHistory(HardwareStatKind kind)
        {
            lock (this._historyLock)
            {
                return this._history[(Int32)kind].ToArray();
            }
        }

        private void PushHistory(HardwareSnapshot snapshot)
        {
            if (!snapshot.Available)
            {
                return;
            }

            lock (this._historyLock)
            {
                foreach (HardwareStatKind kind in Enum.GetValues(typeof(HardwareStatKind)))
                {
                    var load = snapshot.LoadOf(kind);
                    if (!load.HasValue)
                    {
                        continue;
                    }

                    var history = this._history[(Int32)kind];
                    history.Add(load.Value);
                    if (history.Count > HistoryLength)
                    {
                        history.RemoveRange(0, history.Count - HistoryLength);
                    }
                }
            }
        }

        public void Dispose()
        {
            if (this._disposed)
            {
                return;
            }

            this._disposed = true;
            this._pollTimer.Stop();
            this._pollTimer.Elapsed -= this.OnPollTimerElapsed;
            this._pollTimer.Dispose();
            this._httpClient.Dispose();
        }

        private async Task<HardwareSnapshot> FetchSnapshotAsync()
        {
            try
            {
                using var response = await this._httpClient.GetAsync(DefaultApiUri).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

                using var document = JsonDocument.Parse(json);
                var root = document.RootElement;
                var sensors = new List<SensorValue>();

                FlattenSensors(root, sensors, String.Empty, String.Empty, String.Empty);

                var snapshot = HardwareSnapshot.FromSensors(sensors);

                if (this._isConnected != true)
                {
                    PluginLog.Info(this._isConnected == false
                        ? "Connection to LibreHardwareMonitor restored"
                        : "Connected to LibreHardwareMonitor");
                    this._isConnected = true;
                }

                return snapshot;
            }
            catch (Exception ex)
            {
                // Only the transition is logged: while LibreHardwareMonitor is down this runs every poll.
                if (this._isConnected != false)
                {
                    PluginLog.Error(ex, "Lost connection to LibreHardwareMonitor (will keep retrying silently)");
                    this._isConnected = false;
                }

                return HardwareSnapshot.Unavailable("No connection");
            }
        }

        private static void FlattenSensors(JsonElement element, List<SensorValue> result, String hardwareType, String hardwareName, String devicePath)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                var type = TryGetString(element, "Type");
                var typeLower = (type ?? String.Empty).ToLowerInvariant();
                var hardwareId = TryGetString(element, "HardwareId");
                var hasChildren = element.TryGetProperty("Children", out var childrenProp) && childrenProp.ValueKind == JsonValueKind.Array;

                // Hardware nodes may expose their identity via "Type" (older LHM) or "HardwareId" (newer LHM web server).
                if (IsHardwareType(typeLower))
                {
                    hardwareType = typeLower;
                    hardwareName = TryGetName(element) ?? hardwareName;
                    devicePath = !String.IsNullOrEmpty(hardwareId) ? hardwareId : devicePath;
                }
                else if (!String.IsNullOrEmpty(hardwareId))
                {
                    var derived = DeriveHardwareType(hardwareId);
                    if (!String.IsNullOrEmpty(derived))
                    {
                        hardwareType = derived;
                        hardwareName = TryGetName(element) ?? hardwareName;
                        devicePath = hardwareId;
                    }
                }

                // Leaves (which carry an empty "Children" array) must be added before descending, otherwise they are never collected.
                if (element.TryGetProperty("Value", out _)
                    && element.TryGetProperty("SensorId", out var idProp)
                    && idProp.ValueKind == JsonValueKind.String)
                {
                    var sensorId = idProp.GetString();
                    if (String.IsNullOrEmpty(hardwareType))
                    {
                        // Safety net: if no hardware node context was propagated, derive it from the sensor's own path.
                        var derived = DeriveHardwareType(sensorId);
                        if (!String.IsNullOrEmpty(derived))
                        {
                            hardwareType = derived;
                            devicePath = DevicePathOf(sensorId);
                        }
                    }

                    result.Add(new SensorValue(
                        sensorId,
                        TryGetString(element, "Value") ?? String.Empty,
                        TryGetName(element) ?? String.Empty,
                        type ?? String.Empty,
                        hardwareType ?? String.Empty,
                        hardwareName,
                        devicePath));
                    return;
                }

                if (hasChildren)
                {
                    foreach (var child in childrenProp.EnumerateArray())
                    {
                        FlattenSensors(child, result, hardwareType, hardwareName, devicePath);
                    }
                }
            }
            else if (element.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in element.EnumerateArray())
                {
                    FlattenSensors(item, result, hardwareType, hardwareName, devicePath);
                }
            }
        }

        // LibreHardwareMonitor identifies hardware in the web-server JSON via "HardwareId" device paths such as
        // /amdcpu/0, /gpu-nvidia/0, /ram. Map those vendor-neutral prefixes onto the hardware types the matcher uses.
        private static String DeriveHardwareType(String path)
        {
            var lower = (path ?? String.Empty).ToLowerInvariant();
            if (lower.StartsWith("/amdcpu")
                || lower.StartsWith("/intelcpu")
                || lower.StartsWith("/genericcpu")
                || lower.StartsWith("/cpu"))
            {
                return "cpu";
            }

            if (lower.StartsWith("/gpu"))
            {
                return "gpu";
            }

            // "/ram" only: the "/vram" node reports the pagefile, not physical memory.
            if (lower.StartsWith("/ram"))
            {
                return "memory";
            }

            return null;
        }

        // Sensor ids embed the owning hardware node as a prefix, e.g. "/gpu-nvidia/0/temperature/2" -> "/gpu-nvidia/0".
        private static String DevicePathOf(String sensorId)
        {
            var segments = (sensorId ?? String.Empty).Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length >= 2)
            {
                return "/" + segments[0] + "/" + segments[1];
            }

            return segments.Length == 1 ? "/" + segments[0] : String.Empty;
        }

        // LibreHardwareMonitor's web server JSON uses "Text" for display names; fall back to "Name" for other variants.
        private static String TryGetName(JsonElement element)
            => TryGetString(element, "Text") ?? TryGetString(element, "Name");

        private static String TryGetString(JsonElement element, String propertyName)
            => element.TryGetProperty(propertyName, out var prop) && prop.ValueKind == JsonValueKind.String ? prop.GetString() : null;

        private static Boolean IsHardwareType(String typeLower) => HardwareTypes.Contains(typeLower);

        private static List<SensorValue> HardwareSensors(List<SensorValue> sensors, String hardwareType, String pathContains)
        {
            var byType = sensors.Where(s => String.Equals(s.HardwareType, hardwareType, StringComparison.Ordinal)).ToList();
            if (byType.Count > 0)
            {
                return byType;
            }

            return sensors.Where(s => s.PathLower.Contains(pathContains)).ToList();
        }

        private static Double? MaxValue(List<SensorValue> sensors, String type, params String[] nameKeywords)
        {
            var candidates = sensors.Where(s => String.Equals(s.TypeLower, type, StringComparison.Ordinal) && s.NumericValue.HasValue).ToList();
            if (candidates.Count == 0)
            {
                return null;
            }

            foreach (var keyword in nameKeywords)
            {
                if (String.IsNullOrEmpty(keyword))
                {
                    continue;
                }

                var named = candidates.Where(s => s.NameLower.Contains(keyword)).ToList();
                if (named.Count > 0)
                {
                    return named.Max(s => s.NumericValue.Value);
                }
            }

            return candidates.Max(s => s.NumericValue.Value);
        }

        private static Double? NamedSensor(List<SensorValue> sensors, params String[] nameKeywords)
        {
            var candidates = sensors
                .Where(s => (s.TypeLower == "data" || s.TypeLower == "smalldata" || s.TypeLower == "small_data") && s.NumericValue.HasValue)
                .ToList();
            if (candidates.Count == 0)
            {
                return null;
            }

            foreach (var keyword in nameKeywords)
            {
                var match = candidates.FirstOrDefault(s => s.NameLower.Contains(keyword));
                if (match.NumericValue.HasValue)
                {
                    return match.NumericValue;
                }
            }

            return null;
        }

        // Lowercased strings and the parsed number are computed once at construction; the matchers
        // read them several times per sensor on every poll.
        private readonly struct SensorValue
        {
            public SensorValue(String sensorId, String valueText, String name, String type, String hardwareType, String hardwareName, String devicePath)
            {
                this.SensorId = sensorId;
                this.Name = name;
                this.Type = type;
                this.HardwareType = hardwareType;
                this.HardwareName = hardwareName;
                this.DevicePath = devicePath;

                this.PathLower = sensorId?.ToLowerInvariant() ?? String.Empty;
                this.NameLower = name?.ToLowerInvariant() ?? String.Empty;
                this.TypeLower = type?.ToLowerInvariant() ?? String.Empty;
                this.NumericValue = ParseNumeric(valueText);
            }

            public String SensorId { get; }
            public String Name { get; }
            public String Type { get; }
            public String HardwareType { get; }
            public String HardwareName { get; }
            public String DevicePath { get; }

            public String PathLower { get; }
            public String NameLower { get; }
            public String TypeLower { get; }
            public Double? NumericValue { get; }

            private static Double? ParseNumeric(String valueText)
            {
                if (String.IsNullOrWhiteSpace(valueText))
                {
                    return null;
                }

                var text = valueText.Trim();
                if (Double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                {
                    return value;
                }

                var filtered = new String(valueText.Where(c => Char.IsDigit(c) || c == '.' || c == ',').ToArray()).Replace(',', '.');
                return Double.TryParse(filtered, NumberStyles.Float, CultureInfo.InvariantCulture, out value) ? value : (Double?)null;
            }
        }

        private sealed class HardwareSnapshot
        {
            public bool Available { get; private set; }
            public string ErrorMessage { get; private set; }

            // CPU Stats
            public double? CpuLoadPercent { get; private set; }
            public double? CpuClockGHz { get; private set; }
            public double? CpuTempC { get; private set; }

            // GPU Stats
            public double? GpuLoadPercent { get; private set; }
            public double? GpuTempC { get; private set; }
            public double? GpuVramUsedMb { get; private set; }
            public double? GpuVramTotalMb { get; private set; }

            // RAM Stats
            public double? RamLoadPercent { get; private set; }
            public double? RamUsedGb { get; private set; }
            public double? RamTotalGb { get; private set; }

            public static HardwareSnapshot Unavailable(string message)
            {
                return new HardwareSnapshot { Available = false, ErrorMessage = message };
            }

            public static HardwareSnapshot FromSensors(List<SensorValue> sensors)
            {
                var snapshot = new HardwareSnapshot { Available = true };

                // CPU — any vendor: Intel, AMD, ARM. Match by hardware type, prefer package/tctl/tdie temps and the average clock.
                var cpu = HardwareSensors(sensors, "cpu", "cpu");
                snapshot.CpuLoadPercent = MaxValue(cpu, "load", "total");
                snapshot.CpuTempC = MaxValue(cpu, "temperature", "tctl", "tdie", "package");
                var cpuClockMhz = MaxValue(cpu, "clock", "average") ?? MaxValue(cpu, "clock");
                if (cpuClockMhz.HasValue)
                {
                    snapshot.CpuClockGHz = cpuClockMhz.Value / 1000.0;
                }

                // GPU — any vendor (NVIDIA/AMD/Intel iGPU). If several GPUs exist, pick the active one (highest load).
                var gpuSensors = sensors.Where(s => s.HardwareType.StartsWith("gpu")).ToList();
                if (gpuSensors.Count == 0)
                {
                    gpuSensors = sensors.Where(s => s.PathLower.Contains("/gpu")).ToList();
                }

                var primary = gpuSensors;
                var gpuGroups = gpuSensors.Where(s => s.HardwareType.StartsWith("gpu")).GroupBy(s => s.DevicePath ?? s.HardwareName ?? String.Empty).ToList();
                if (gpuGroups.Count > 1)
                {
                    primary = gpuGroups
                        .OrderByDescending(g => g.Where(s => s.TypeLower == "load" && s.NumericValue.HasValue).Select(s => s.NumericValue.Value).DefaultIfEmpty(0).Max())
                        .First()
                        .ToList();
                }

                snapshot.GpuLoadPercent = MaxValue(primary, "load", "core", "gpu");
                snapshot.GpuTempC = MaxValue(primary, "temperature", "core", "gpu");
                snapshot.GpuVramUsedMb = NamedSensor(primary, "gpu memory used", "dedicated memory used", "memory used");
                var vramFreeMb = NamedSensor(primary, "gpu memory free", "dedicated memory free", "memory free");
                var vramTotalMb = NamedSensor(primary, "gpu memory total", "dedicated memory total", "memory size");
                if (vramTotalMb.HasValue)
                {
                    snapshot.GpuVramTotalMb = vramTotalMb.Value;
                }
                else if (snapshot.GpuVramUsedMb.HasValue && vramFreeMb.HasValue)
                {
                    snapshot.GpuVramTotalMb = snapshot.GpuVramUsedMb.Value + vramFreeMb.Value;
                }

                // RAM — generic memory hardware node.
                var ram = HardwareSensors(sensors, "memory", "/ram");
                snapshot.RamLoadPercent = MaxValue(ram, "load", "memory");
                snapshot.RamUsedGb = NamedSensor(ram, "used memory", "used");
                var ramAvailableGb = NamedSensor(ram, "available memory", "available");
                if (snapshot.RamUsedGb.HasValue && ramAvailableGb.HasValue)
                {
                    snapshot.RamTotalGb = snapshot.RamUsedGb.Value + ramAvailableGb.Value;
                }

                return snapshot;
            }

            public Double? LoadOf(HardwareStatKind kind) => kind switch
            {
                HardwareStatKind.Cpu => this.CpuLoadPercent,
                HardwareStatKind.Gpu => this.GpuLoadPercent,
                _ => this.RamLoadPercent,
            };

            public HardwareDisplayData ToDisplay(HardwareStatKind kind)
            {
                if (!this.Available)
                {
                    return HardwareDisplayData.Offline(kind.ToString().ToUpper(), this.ErrorMessage);
                }

                var data = new HardwareDisplayData { Available = true, Title = kind.ToString().ToUpper() };
                switch (kind)
                {
                    case HardwareStatKind.Cpu:
                        data.LoadPercent = this.CpuLoadPercent;
                        data.ColorValue = this.CpuTempC;
                        data.TempC = this.CpuTempC;
                        data.TempText = this.FormatTemp(this.CpuTempC);
                        data.LeftValue = this.FormatPercent(this.CpuLoadPercent);
                        data.RightValue = data.TempText;
                        data.Footer1 = this.FormatMhz(this.CpuClockGHz);
                        break;

                    case HardwareStatKind.Gpu:
                        data.LoadPercent = this.GpuLoadPercent;
                        data.ColorValue = this.GpuTempC;
                        data.TempC = this.GpuTempC;
                        data.TempText = this.FormatTemp(this.GpuTempC);
                        data.LeftValue = this.FormatPercent(this.GpuLoadPercent);
                        data.RightValue = data.TempText;
                        data.Footer1 = this.FormatVramSpec();
                        break;

                    case HardwareStatKind.Ram:
                        data.LoadPercent = this.RamLoadPercent;
                        data.ColorValue = this.RamLoadPercent;
                        data.RightValue = this.FormatPercent(this.RamLoadPercent);
                        data.Footer1 = this.FormatRamUsedTotal();
                        data.Footer2 = this.FormatRamFree();
                        break;
                }

                data.LoadText = this.FormatPercent(data.LoadPercent);
                data.Secondary = data.TempText ?? this.FormatRamUsedShort();
                return data;
            }

            private String FormatRamUsedShort()
                => this.RamUsedGb.HasValue ? $"{this.RamUsedGb.Value.ToString("F1", CultureInfo.InvariantCulture)}G" : "--G";

            private String FormatPercent(Double? value) => value.HasValue ? $"{value.Value.ToString("F0", CultureInfo.InvariantCulture)}%" : "--%";

            private String FormatTemp(Double? value) => value.HasValue ? $"{value.Value.ToString("F0", CultureInfo.InvariantCulture)}°C" : "--°C";

            private String FormatMhz(Double? valueGhZ) => valueGhZ.HasValue ? $"{(valueGhZ.Value * 1000.0).ToString("F0", CultureInfo.InvariantCulture)} MHz" : "-- MHz";

            private String FormatVramSpec()
            {
                if (!this.GpuVramUsedMb.HasValue || !this.GpuVramTotalMb.HasValue)
                {
                    return "--";
                }

                var usedGb = this.GpuVramUsedMb.Value / 1024.0;
                var totalGb = this.GpuVramTotalMb.Value / 1024.0;
                return $"{usedGb.ToString("F1", CultureInfo.InvariantCulture)} / {totalGb.ToString("F1", CultureInfo.InvariantCulture)} GB";
            }

            private String FormatRamUsedTotal()
                => (this.RamTotalGb.HasValue && this.RamUsedGb.HasValue)
                    ? $"{this.RamUsedGb.Value.ToString("F1", CultureInfo.InvariantCulture)} / {Math.Round(this.RamTotalGb.Value, 0).ToString("F0", CultureInfo.InvariantCulture)} GB"
                    : "--";

            private String FormatRamFree()
                => (this.RamTotalGb.HasValue && this.RamUsedGb.HasValue)
                    ? $"Free: {Math.Round(this.RamTotalGb.Value - this.RamUsedGb.Value, 1).ToString("F1", CultureInfo.InvariantCulture)} GB"
                    : "Free: -- GB";
        }
    }

    internal sealed class HardwareDisplayData
    {
        public Boolean Available { get; set; }
        public String Title { get; set; }
        public String Message { get; set; }
        public Double? LoadPercent { get; set; }
        public Double? ColorValue { get; set; }
        public String LoadText { get; set; }
        public Double? TempC { get; set; }

        // CPU/GPU temperature text; null for RAM.
        public String TempText { get; set; }

        // Short second value for compact cards: temperature for CPU/GPU, used GB for RAM.
        public String Secondary { get; set; }
        public String LeftValue { get; set; }
        public String RightValue { get; set; }
        public String Footer1 { get; set; }
        public String Footer2 { get; set; }

        public static HardwareDisplayData Offline(String title, String message)
        {
            return new HardwareDisplayData { Available = false, Title = title, Message = message };
        }
    }
}
