namespace Loupedeck.HardwareMonitorPlugin
{
    using System;
    using System.Collections.Generic;

    // The Logi SDK auto-registers PluginDynamicCommand types found by its reflection scan ONLY if
    // they expose a public parameterless constructor. The bases here are abstract; every button is
    // its own sealed type at the bottom of this file (see AGENTS.md before changing that).
    internal abstract class HardwareCardCommand : PluginDynamicCommand, IDisposable
    {
        // The SDK's reflection scan creates command instances directly, so nothing in the
        // plugin lifecycle would otherwise get a chance to Dispose() them. Each instance
        // registers itself here so HardwareMonitorPlugin.Unload() can unsubscribe them.
        private static readonly List<HardwareCardCommand> _instances = new();
        private static readonly Object _instancesLock = new();

        private Boolean _disposed;

        protected HardwareCardCommand(String displayName, String description)
            : base(displayName: displayName, description: description, groupName: "Hardware Monitor")
        {
            // Display-only widgets: image tiles shown without the command-name caption. The caption
            // is drawn by the Logi Plugin Service from the DisplayName and cannot be hidden
            // separately (GetCommandDisplayName is ignored for single-instance commands).
            this.SetWidget(true);

            HardwareMonitorClient.Instance.SnapshotUpdated += this.OnSnapshotUpdated;

            lock (_instancesLock)
            {
                _instances.Add(this);
            }
        }

        public static void DisposeAll()
        {
            List<HardwareCardCommand> snapshot;
            lock (_instancesLock)
            {
                snapshot = new List<HardwareCardCommand>(_instances);
                _instances.Clear();
            }

            foreach (var instance in snapshot)
            {
                instance.Dispose();
            }
        }

        public void Dispose()
        {
            if (this._disposed)
            {
                return;
            }

            this._disposed = true;
            HardwareMonitorClient.Instance.SnapshotUpdated -= this.OnSnapshotUpdated;

            lock (_instancesLock)
            {
                _instances.Remove(this);
            }
        }

        private void OnSnapshotUpdated(Object sender, EventArgs e)
        {
            try
            {
                this.ActionImageChanged();
            }
            catch (Exception ex)
            {
                PluginLog.Warning(ex, $"Failed to refresh {this.DisplayName} button");
            }
        }

        protected override void RunCommand(String actionParameter)
        {
            this.ActionImageChanged();
        }

        protected override String GetCommandDisplayName(String actionParameter, PluginImageSize imageSize) => String.Empty;

        protected override BitmapImage GetCommandImage(String actionParameter, PluginImageSize imageSize)
        {
            if (imageSize == PluginImageSize.None)
            {
                return null;
            }

            var builder = new BitmapBuilder(imageSize);
            try
            {
                builder.Clear(BitmapColor.Black);
                this.Render(builder);
                return builder.ToImage();
            }
            catch (Exception ex)
            {
                PluginLog.Warning(ex, $"Failed to render {this.DisplayName} button image");
                return null;
            }
            finally
            {
                builder.Dispose();
            }
        }

        protected abstract void Render(BitmapBuilder builder);
    }

    // One metric (CPU, GPU or RAM) drawn in one card style.
    internal abstract class HardwareStatsCommand : HardwareCardCommand
    {
        private readonly HardwareStatKind _kind;
        private readonly CardStyle _style;

        protected HardwareStatsCommand(HardwareStatKind kind, CardStyle style)
            : base(GetDisplayName(kind, style), GetDescription(kind, style))
        {
            this._kind = kind;
            this._style = style;
        }

        protected override void Render(BitmapBuilder builder)
        {
            var client = HardwareMonitorClient.Instance;
            var data = client.GetDisplay(this._kind);
            if (!data.Available)
            {
                CardRenderer.DrawOffline(builder, data.Title, data.Message);
                return;
            }

            switch (this._style)
            {
                case CardStyle.Ring:
                    CardRenderer.DrawRing(builder, data);
                    break;
                case CardStyle.Fill:
                    CardRenderer.DrawFill(builder, data);
                    break;
                case CardStyle.BigNumber:
                    CardRenderer.DrawBigNumber(builder, data);
                    break;
                case CardStyle.Graph:
                    CardRenderer.DrawGraph(builder, data, client.GetHistory(this._kind));
                    break;
                default:
                    CardRenderer.DrawClassic(builder, data, client.GetHistory(this._kind));
                    break;
            }
        }

        private static String KindName(HardwareStatKind kind) => kind.ToString().ToUpper();

        // Classic keeps the original bare "CPU"/"GPU"/"RAM" names so existing assignments look unchanged.
        private static String GetDisplayName(HardwareStatKind kind, CardStyle style) => style switch
        {
            CardStyle.Ring => $"{KindName(kind)} · Ring",
            CardStyle.Fill => $"{KindName(kind)} · Fill",
            CardStyle.BigNumber => $"{KindName(kind)} · Big number",
            CardStyle.Graph => $"{KindName(kind)} · Graph",
            _ => KindName(kind),
        };

        private static String GetDescription(HardwareStatKind kind, CardStyle style)
        {
            var what = kind == HardwareStatKind.Ram ? "RAM usage" : $"{KindName(kind)} load and temperature";
            var how = style switch
            {
                CardStyle.Ring => " as a ring gauge",
                CardStyle.Fill => " as a fill level",
                CardStyle.BigNumber => " as one big number",
                CardStyle.Graph => " over a full-width history graph",
                _ => String.Empty,
            };
            return $"Shows {what}{how} from LibreHardwareMonitor";
        }
    }

    // CPU, GPU and RAM together on one button.
    internal abstract class SystemStatsCommand : HardwareCardCommand
    {
        private readonly SystemCardStyle _style;

        protected SystemStatsCommand(SystemCardStyle style)
            : base(GetDisplayName(style), GetDescription(style))
        {
            this._style = style;
        }

        protected override void Render(BitmapBuilder builder)
        {
            var client = HardwareMonitorClient.Instance;
            var rows = new[]
            {
                client.GetDisplay(HardwareStatKind.Cpu),
                client.GetDisplay(HardwareStatKind.Gpu),
                client.GetDisplay(HardwareStatKind.Ram),
            };

            // All three come from the same snapshot, so they are available or offline together.
            if (!rows[0].Available)
            {
                CardRenderer.DrawOffline(builder, "SYSTEM", rows[0].Message);
                return;
            }

            switch (this._style)
            {
                case SystemCardStyle.Columns:
                    CardRenderer.DrawSystemColumns(builder, rows);
                    break;
                case SystemCardStyle.Rings:
                    CardRenderer.DrawSystemRings(builder, rows);
                    break;
                default:
                    CardRenderer.DrawSystemBars(builder, rows);
                    break;
            }
        }

        private static String GetDisplayName(SystemCardStyle style) => style switch
        {
            SystemCardStyle.Columns => "System · Columns",
            SystemCardStyle.Rings => "System · Rings",
            _ => "System · Bars",
        };

        private static String GetDescription(SystemCardStyle style) => style switch
        {
            SystemCardStyle.Columns => "Shows CPU, GPU and RAM load as three columns on one button",
            SystemCardStyle.Rings => "Shows CPU, GPU and RAM load as three small rings on one button",
            _ => "Shows CPU, GPU and RAM load as three bars on one button",
        };
    }

    // ---------------------------------------------------------------- Classic (original names, keep them)

    internal sealed class CpuStatsCommand : HardwareStatsCommand
    {
        public CpuStatsCommand() : base(HardwareStatKind.Cpu, CardStyle.Classic) { }
    }

    internal sealed class GpuStatsCommand : HardwareStatsCommand
    {
        public GpuStatsCommand() : base(HardwareStatKind.Gpu, CardStyle.Classic) { }
    }

    internal sealed class RamStatsCommand : HardwareStatsCommand
    {
        public RamStatsCommand() : base(HardwareStatKind.Ram, CardStyle.Classic) { }
    }

    // ---------------------------------------------------------------- A: ring gauge

    internal sealed class CpuRingCommand : HardwareStatsCommand
    {
        public CpuRingCommand() : base(HardwareStatKind.Cpu, CardStyle.Ring) { }
    }

    internal sealed class GpuRingCommand : HardwareStatsCommand
    {
        public GpuRingCommand() : base(HardwareStatKind.Gpu, CardStyle.Ring) { }
    }

    internal sealed class RamRingCommand : HardwareStatsCommand
    {
        public RamRingCommand() : base(HardwareStatKind.Ram, CardStyle.Ring) { }
    }

    // ---------------------------------------------------------------- B: fill level

    internal sealed class CpuFillCommand : HardwareStatsCommand
    {
        public CpuFillCommand() : base(HardwareStatKind.Cpu, CardStyle.Fill) { }
    }

    internal sealed class GpuFillCommand : HardwareStatsCommand
    {
        public GpuFillCommand() : base(HardwareStatKind.Gpu, CardStyle.Fill) { }
    }

    internal sealed class RamFillCommand : HardwareStatsCommand
    {
        public RamFillCommand() : base(HardwareStatKind.Ram, CardStyle.Fill) { }
    }

    // ---------------------------------------------------------------- C: one big number

    internal sealed class CpuBigNumberCommand : HardwareStatsCommand
    {
        public CpuBigNumberCommand() : base(HardwareStatKind.Cpu, CardStyle.BigNumber) { }
    }

    internal sealed class GpuBigNumberCommand : HardwareStatsCommand
    {
        public GpuBigNumberCommand() : base(HardwareStatKind.Gpu, CardStyle.BigNumber) { }
    }

    internal sealed class RamBigNumberCommand : HardwareStatsCommand
    {
        public RamBigNumberCommand() : base(HardwareStatKind.Ram, CardStyle.BigNumber) { }
    }

    // ---------------------------------------------------------------- D: full-width history graph

    internal sealed class CpuGraphCommand : HardwareStatsCommand
    {
        public CpuGraphCommand() : base(HardwareStatKind.Cpu, CardStyle.Graph) { }
    }

    internal sealed class GpuGraphCommand : HardwareStatsCommand
    {
        public GpuGraphCommand() : base(HardwareStatKind.Gpu, CardStyle.Graph) { }
    }

    internal sealed class RamGraphCommand : HardwareStatsCommand
    {
        public RamGraphCommand() : base(HardwareStatKind.Ram, CardStyle.Graph) { }
    }

    // ---------------------------------------------------------------- E: everything on one button

    internal sealed class SystemBarsCommand : SystemStatsCommand
    {
        public SystemBarsCommand() : base(SystemCardStyle.Bars) { }
    }

    internal sealed class SystemColumnsCommand : SystemStatsCommand
    {
        public SystemColumnsCommand() : base(SystemCardStyle.Columns) { }
    }

    internal sealed class SystemRingsCommand : SystemStatsCommand
    {
        public SystemRingsCommand() : base(SystemCardStyle.Rings) { }
    }
}
