namespace Loupedeck.HardwareMonitorPlugin
{
    using System;
    using System.Collections.Generic;
    using System.Timers;

    // Card layout: title / main metric (left: load, right: big value) / load-history
    // sparkline / footer line(s). Colors follow the metric itself: the big value is
    // colored by ColorValue (temp for CPU/GPU, load% for RAM); the sparkline by load%.
    //
    // Note: the Logi SDK auto-registers PluginDynamicCommand types found by its
    // reflection scan ONLY if they expose a public parameterless constructor. This
    // base class is abstract; the three concrete subclasses below (each with a
    // parameterless ctor) are what the SDK discovers and shows as CPU/GPU/RAM buttons.
    internal abstract class HardwareStatsCommand : PluginDynamicCommand, IDisposable
    {
        private readonly HardwareStatKind _kind;
        private readonly HardwareMonitorClient _client;
        private readonly Timer _timer;
        private readonly List<Double> _history = new();
        private readonly Object _historyLock = new();

        private const Int32 HistoryLength = 48;

        private Boolean _disposed;

        protected HardwareStatsCommand(HardwareStatKind kind)
            : base(displayName: GetDisplayName(kind), description: GetDescription(kind), groupName: "Hardware Monitor")
        {
            this._kind = kind;
            this._client = new HardwareMonitorClient();

            // Make these display-only widgets: widgets are image tiles shown without
            // the command-name caption (the "CPU" label under the button). The name
            // caption is drawn by the Logi Plugin Service from the DisplayName and
            // cannot be hidden separately (GetCommandDisplayName is ignored for
            // single-instance commands). Widgets are not for interaction, which is
            // exactly what a monitoring card needs.
            this.SetWidget(true);

            this._timer = new Timer(1500)
            {
                AutoReset = true,
                Enabled = true,
            };

            this._timer.Elapsed += this.OnTimerElapsed;
            this._timer.Start();
        }

        public void Dispose()
        {
            if (this._disposed)
            {
                return;
            }

            this._disposed = true;
            this._timer.Stop();
            this._timer.Elapsed -= this.OnTimerElapsed;
            this._timer.Dispose();
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

            var data = this._client.GetDisplay(this._kind);
            var builder = new BitmapBuilder(imageSize);
            try
            {
                builder.Clear(BitmapColor.Black);

                if (!data.Available)
                {
                    DrawTextAt(builder, data.Title, 6, 6, 20, BitmapColor.White);
                    DrawTextAt(builder, data.Message, 6, 34, 14, Gray);
                    return builder.ToImage();
                }

                this.PushHistory(data.LoadPercent);
                this.RenderCard(builder, data, imageSize);

                return builder.ToImage();
            }
            catch (Exception ex)
            {
                PluginLog.Warning(ex, $"Failed to render {this._kind} button image");
                return null;
            }
            finally
            {
                builder.Dispose();
            }
        }

        private void PushHistory(Double? loadPercent)
        {
            if (!loadPercent.HasValue)
            {
                return;
            }

            lock (this._historyLock)
            {
                this._history.Add(loadPercent.Value);
                if (this._history.Count > HistoryLength)
                {
                    this._history.RemoveRange(0, this._history.Count - HistoryLength);
                }
            }
        }

        private void RenderCard(BitmapBuilder builder, HardwareDisplayData data, PluginImageSize imageSize)
        {
            var width = builder.Width;
            var height = builder.Height;
            var margin = 6;

            // Shared CPU/GPU/RAM card layout: title row (name + load%), big value,
            // load-history sparkline, and centered footer line(s). The big value is
            // colored by the metric itself (CPU/GPU temperature, RAM load%); the
            // sparkline and the title load% by load%.
            // Fonts are kept in a narrow band for a consistent look: title/load%
            // row ~16-18, big value ~20-24, footer ~13-15. Long footers (GPU
            // "8.0 / 12.0 GB", RAM used/total) use the smaller end so they don't
            // run off the button; short ones (CPU "4800 MHz") stay a bit bigger.
            var titleFont = Math.Max(16, Math.Min(18, (Int32)(height * 0.15)));
            var primaryFont = Math.Max(20, Math.Min(24, (Int32)(height * 0.20)));
            var hasFooter2 = !String.IsNullOrEmpty(data.Footer2);
            var footerFont = (hasFooter2 || (data.Footer1?.Length ?? 0) > 10)
                ? Math.Max(12, Math.Min(13, (Int32)(height * 0.11)))
                : Math.Max(13, Math.Min(15, (Int32)(height * 0.13)));

            var titleY = 4;
            var primaryY = Math.Max(30, (Int32)(height * 0.26));
            var sparkY = Math.Max(58, (Int32)(height * 0.50));
            var sparkHeight = Math.Max(24, (Int32)(height * 0.22));
            var footerY = height - Math.Max(20, (Int32)(height * 0.17));

            var loadColor = ColorForValue(data.LoadPercent);

            DrawTextPairCentered(builder, data.Title, BitmapColor.White, data.LeftValue, loadColor, width, titleY, titleFont);

            DrawTextCenter(builder, data.RightValue, width, primaryY, primaryFont, ColorForValue(data.ColorValue));

            DrawSparkline(builder, margin, sparkY, width - margin * 2, sparkHeight, loadColor);

            if (hasFooter2)
            {
                DrawTextCenter(builder, data.Footer1, width, footerY - footerFont - 2, footerFont, BitmapColor.White);
                DrawTextCenter(builder, data.Footer2, width, footerY, footerFont, BitmapColor.White);
            }
            else
            {
                DrawTextCenter(builder, data.Footer1, width, footerY, footerFont, BitmapColor.White);
            }
        }

        private void DrawSparkline(BitmapBuilder builder, Int32 x, Int32 y, Int32 width, Int32 height, BitmapColor color)
        {
            List<Double> history;
            lock (this._historyLock)
            {
                history = new List<Double>(this._history);
            }

            if (history.Count == 0)
            {
                return;
            }

            var count = history.Count;
            var innerWidth = width - 1;
            var innerHeight = Math.Max(1, height - 1);
            var prevX = 0f;
            var prevY = 0f;
            for (var i = 0; i < count; i++)
            {
                var t = count > 1 ? (Single)i / (count - 1) : 0f;
                var pointX = x + t * innerWidth;
                var pointValue = Math.Min(100.0, Math.Max(0.0, history[i]));
                var pointY = y + (1f - (Single)(pointValue / 100.0)) * innerHeight;
                if (i > 0)
                {
                    builder.DrawLine(prevX, prevY, pointX, pointY, color, 1.5f);
                }

                prevX = pointX;
                prevY = pointY;
            }
        }

        // Empirical: the box overload paints glyph tops ~(14 - 0.2*fontSize) px below the requested y.
        private static Int32 GlyphOffset(Int32 fontSize) => (Int32)Math.Round(14.0 - 0.2 * fontSize);

        private static void DrawTextAt(BitmapBuilder builder, String text, Int32 x, Int32 yTop, Int32 fontSize, BitmapColor color)
        {
            if (String.IsNullOrEmpty(text))
            {
                return;
            }

            var width = (Int32)(fontSize * 0.52 * text.Length) + 4;
            var height = fontSize + 8;
            var boxY = Math.Max(0, yTop - GlyphOffset(fontSize));
            builder.DrawText(text, x, boxY, width, height, color, fontSize, fontSize, 0, null);
        }

        private static void DrawTextRight(BitmapBuilder builder, String text, Int32 rightX, Int32 yTop, Int32 fontSize, BitmapColor color)
        {
            if (String.IsNullOrEmpty(text))
            {
                return;
            }

            var width = (Int32)(fontSize * 0.52 * text.Length) + 4;
            var x = Math.Max(0, rightX - width);
            var height = fontSize + 8;
            var boxY = Math.Max(0, yTop - GlyphOffset(fontSize));
            builder.DrawText(text, x, boxY, width, height, color, fontSize, fontSize, 0, null);
        }

        private static void DrawTextCenter(BitmapBuilder builder, String text, Int32 canvasWidth, Int32 yTop, Int32 fontSize, BitmapColor color)
        {
            if (String.IsNullOrEmpty(text))
            {
                return;
            }

            // Use a per-char factor a bit above the narrow average: digits and wide
            // glyphs like 'M' (in "MHz") otherwise overflow the box and wrap to a
            // second line. 0.62 matches the widest common glyphs (M, digits, degree).
            var width = (Int32)(fontSize * 0.62 * text.Length) + 4;
            var x = Math.Max(0, (canvasWidth - width) / 2);
            var height = fontSize + 8;
            var boxY = Math.Max(0, yTop - GlyphOffset(fontSize));
            builder.DrawText(text, x, boxY, width, height, color, fontSize, fontSize, 0, null);
        }

        private static void DrawTextPairCentered(BitmapBuilder builder, String left, BitmapColor leftColor, String right, BitmapColor rightColor, Int32 canvasWidth, Int32 yTop, Int32 fontSize)
        {
            var leftWidth = String.IsNullOrEmpty(left) ? 0 : (Int32)(fontSize * 0.52 * left.Length) + 4;
            var rightWidth = String.IsNullOrEmpty(right) ? 0 : (Int32)(fontSize * 0.52 * right.Length) + 4;
            var gap = 8;
            var totalWidth = leftWidth + rightWidth + (leftWidth > 0 && rightWidth > 0 ? gap : 0);
            var startX = Math.Max(0, (canvasWidth - totalWidth) / 2);

            if (leftWidth > 0)
            {
                DrawTextAt(builder, left, startX, yTop, fontSize, leftColor);
            }

            if (rightWidth > 0)
            {
                DrawTextAt(builder, right, startX + leftWidth + gap, yTop, fontSize, rightColor);
            }
        }

        private static readonly BitmapColor Gray = new(0x9E, 0x9E, 0x9E);

        private static BitmapColor ColorForValue(Double? value)
        {
            if (!value.HasValue)
            {
                return Gray;
            }

            if (value.Value < 60.0)
            {
                return new BitmapColor(0x33, 0xC4, 0x4C);
            }

            if (value.Value < 85.0)
            {
                return new BitmapColor(0xFF, 0xC1, 0x07);
            }

            return new BitmapColor(0xFF, 0x2D, 0x2D);
        }

        private static String GetDisplayName(HardwareStatKind kind) => kind switch
        {
            HardwareStatKind.Cpu => "CPU",
            HardwareStatKind.Gpu => "GPU",
            HardwareStatKind.Ram => "RAM",
            _ => "Hardware",
        };

        private static String GetDescription(HardwareStatKind kind) => kind switch
        {
            HardwareStatKind.Cpu => "Shows CPU load and temperature from LibreHardwareMonitor",
            HardwareStatKind.Gpu => "Shows GPU load and temperature from LibreHardwareMonitor",
            HardwareStatKind.Ram => "Shows RAM usage from LibreHardwareMonitor",
            _ => "Shows hardware stats from LibreHardwareMonitor",
        };

        private void OnTimerElapsed(Object sender, ElapsedEventArgs e)
        {
            try
            {
                this.ActionImageChanged();
            }
            catch (Exception ex)
            {
                PluginLog.Warning(ex, $"Failed to refresh {this._kind} button");
            }
        }
    }

    internal sealed class CpuStatsCommand : HardwareStatsCommand
    {
        public CpuStatsCommand() : base(HardwareStatKind.Cpu)
        {
        }
    }

    internal sealed class GpuStatsCommand : HardwareStatsCommand
    {
        public GpuStatsCommand() : base(HardwareStatKind.Gpu)
        {
        }
    }

    internal sealed class RamStatsCommand : HardwareStatsCommand
    {
        public RamStatsCommand() : base(HardwareStatKind.Ram)
        {
        }
    }
}
