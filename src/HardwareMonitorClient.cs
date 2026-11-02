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

        // One poll interval for all buttons. LibreHardwareMonitor itself refreshes about once per second.
        private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1.5);

        private static readonly Object InstanceLock = new();
        private static HardwareMonitorClient _instance;

        private readonly HttpClient _httpClient;
        private readonly Timer _pollTimer;

        private volatile HardwareSnapshot _snapshot = HardwareSnapshot.Unavailable("Waiting for LibreHardwareMonitor");
        private Int32 _pollInProgress;
        private Boolean _lastFetchFailed;
        private Boolean _disposed;

        // Raised on a background thread after every poll (successful or not). Static so that
        // subscribers survive a Shutdown(): the next access to Shared starts a new client.
        public static event EventHandler SnapshotUpdated;

        // All CPU/GPU/RAM buttons share one client, so the plugin makes one HTTP request per interval
        // instead of one per button, and rendering never waits on the network.
        public static HardwareMonitorClient Shared
        {
            get
            {
                lock (InstanceLock)
                {
                    return _instance ??= new HardwareMonitorClient();
                }
            }
        }

        public static void Shutdown()
        {
            lock (InstanceLock)
            {
                _instance?.Dispose();
                _instance = null;
            }
        }

        private HardwareMonitorClient()
        {
            this._httpClient = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(1.2),
            };

            this._pollTimer = new Timer(this.OnPollTimer, null, TimeSpan.Zero, PollInterval);
        }

        public HardwareDisplayData GetDisplay(HardwareStatKind kind)
        {
            try
            {
                return this._snapshot.ToDisplay(kind);
            }
            catch (Exception ex)
            {
                PluginLog.Warning(ex, $"Failed to get {kind} stats");
                return HardwareDisplayData.Offline(kind.ToString().ToUpper(), "API error");
            }
        }

        public void Dispose()
        {
            if (this._disposed)
            {
                return;
            }

            this._disposed = true;
            this._pollTimer.Dispose();
            this._httpClient.Dispose();
        }

        private async void OnPollTimer(Object state)
        {
            // Skip a tick if the previous request is still running (e.g. LHM is slow to answer).
            if (this._disposed || Interlocked.Exchange(ref this._pollInProgress, 1) == 1)
            {
                return;
            }

            try
            {
                this._snapshot = await this.FetchSnapshotAsync().ConfigureAwait(false);
                if (!this._disposed)
                {
                    SnapshotUpdated?.Invoke(this, EventArgs.Empty);
                }
            }
            catch (Exception ex)
            {
                PluginLog.Warning(ex, "Hardware poll failed");
            }
            finally
            {
                Interlocked.Exchange(ref this._pollInProgress, 0);
            }
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

                if (this._lastFetchFailed)
                {
                    this._lastFetchFailed = false;
                    PluginLog.Info("LibreHardwareMonitor connection restored");
                }

                return HardwareSnapshot.FromSensors(sensors);
            }
            catch (Exception ex)
            {
                // Log only the transition to offline; otherwise a stopped LHM floods the log every poll.
                if (!this._lastFetchFailed)
                {
                    this._lastFetchFailed = true;
                    PluginLog.Warning(ex, $"Cannot reach LibreHardwareMonitor at {DefaultApiUri}");
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

                    var sensor = new SensorValue
                    {
                        SensorId = sensorId,
                        ValueText = TryGetString(element, "Value") ?? String.Empty,
                        Name = TryGetName(element) ?? String.Empty,
                        Type = type ?? String.Empty,
                        HardwareType = hardwareType ?? String.Empty,
                        HardwareName = hardwareName,
                        DevicePath = devicePath,
                    };
                    result.Add(sensor);
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

        private struct SensorValue
        {
            public string SensorId { get; set; }
            public string ValueText { get; set; }
            public string Name { get; set; }
            public string Type { get; set; }
            public string HardwareType { get; set; }
            public string HardwareName { get; set; }
            public string DevicePath { get; set; }

            public string PathLower => this.SensorId?.ToLowerInvariant() ?? String.Empty;
            public string NameLower => this.Name?.ToLowerInvariant() ?? String.Empty;
            public string TypeLower => this.Type?.ToLowerInvariant() ?? String.Empty;

            public Double? NumericValue
            {
                get
                {
                    if (String.IsNullOrWhiteSpace(this.ValueText))
                    {
                        return null;
                    }

                    var text = this.ValueText.Trim();
                    if (Double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                    {
                        return value;
                    }

                    var filtered = new string(this.ValueText.Where(c => Char.IsDigit(c) || c == '.' || c == ',').ToArray()).Replace(',', '.');
                    return Double.TryParse(filtered, NumberStyles.Float, CultureInfo.InvariantCulture, out value) ? value : (Double?)null;
                }
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
            public double? GpuClockGHz { get; private set; }
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
                var gpuClockMhz = MaxValue(primary, "clock", "core");
                if (gpuClockMhz.HasValue)
                {
                    snapshot.GpuClockGHz = gpuClockMhz.Value / 1000.0;
                }

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
                        data.LeftValue = this.FormatPercent(this.CpuLoadPercent);
                        data.RightValue = this.FormatTemp(this.CpuTempC);
                        data.Footer1 = this.FormatMhz(this.CpuClockGHz);
                        data.Lines = new[]
                        {
                            $"CPU {this.FormatTemp(this.CpuTempC)}",
                            $"{this.FormatPercent(this.CpuLoadPercent)} / {this.FormatGhz(this.CpuClockGHz)}",
                        };
                        break;

                    case HardwareStatKind.Gpu:
                        data.LoadPercent = this.GpuLoadPercent;
                        data.ColorValue = this.GpuTempC;
                        data.LeftValue = this.FormatPercent(this.GpuLoadPercent);
                        data.RightValue = this.FormatTemp(this.GpuTempC);
                        data.Footer1 = this.FormatVramSpec();
                        data.Lines = new[]
                        {
                            $"GPU {this.FormatTemp(this.GpuTempC)}",
                            $"{this.FormatPercent(this.GpuLoadPercent)} / {this.FormatGhz(this.GpuClockGHz)}",
                            this.FormatVram(),
                        };
                        break;

                    case HardwareStatKind.Ram:
                        data.LoadPercent = this.RamLoadPercent;
                        data.ColorValue = this.RamLoadPercent;
                        data.RightValue = this.FormatPercent(this.RamLoadPercent);
                        data.Footer1 = this.FormatRamUsedTotal();
                        data.Footer2 = this.FormatRamFree();
                        data.Lines = new[]
                        {
                            $"RAM {this.FormatPercent(this.RamLoadPercent)}",
                            $"{this.FormatGb(this.RamUsedGb)} / {this.FormatTotalGb(this.RamTotalGb)}",
                            this.FormatRamFree(),
                        };
                        break;

                    default:
                        data.Lines = new[] { "Hardware" };
                        break;
                }

                return data;
            }

            private String FormatPercent(Double? value) => value.HasValue ? $"{value.Value.ToString("F0", CultureInfo.InvariantCulture)}%" : "--%";

            private String FormatTemp(Double? value) => value.HasValue ? $"{value.Value.ToString("F0", CultureInfo.InvariantCulture)}°C" : "--°C";

            private String FormatGhz(Double? value) => value.HasValue ? $"{value.Value.ToString("F1", CultureInfo.InvariantCulture)}GHz" : "--GHz";

            private String FormatMhz(Double? valueGhZ) => valueGhZ.HasValue ? $"{(valueGhZ.Value * 1000.0).ToString("F0", CultureInfo.InvariantCulture)} MHz" : "-- MHz";

            private String FormatGb(Double? value) => value.HasValue ? value.Value.ToString("F1", CultureInfo.InvariantCulture) : "--";

            private String FormatTotalGb(Double? value) => value.HasValue ? $"{Math.Round(value.Value, 0).ToString("F0", CultureInfo.InvariantCulture)}GB" : "--GB";

            private Double? RamAvailableGb() =>
                (this.RamTotalGb.HasValue && this.RamUsedGb.HasValue)
                    ? this.RamTotalGb.Value - this.RamUsedGb.Value
                    : (Double?)null;

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

            private String FormatVram()
            {
                if (!this.GpuVramUsedMb.HasValue)
                {
                    return "-- GB";
                }

                var usedGb = (this.GpuVramUsedMb.Value / 1024.0).ToString("F1", CultureInfo.InvariantCulture);
                if (this.GpuVramTotalMb.HasValue)
                {
                    var totalGb = Math.Round(this.GpuVramTotalMb.Value / 1024.0, 0).ToString("F0", CultureInfo.InvariantCulture);
                    return $"{usedGb} / {totalGb}GB";
                }

                return $"{usedGb} GB";
            }

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
        public String[] Lines { get; set; }
        public Double? LoadPercent { get; set; }
        public Double? ColorValue { get; set; }
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
