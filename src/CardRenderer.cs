namespace Loupedeck.HardwareMonitorPlugin
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;

    internal enum CardStyle
    {
        Classic,
        Ring,
        Fill,
        BigNumber,
        Graph,
    }

    internal enum SystemCardStyle
    {
        Bars,
        Columns,
        Rings,
    }

    // Draws every card style. The non-classic styles are laid out on a 180x180 design grid
    // (the size of the mockups), scaled to the real button through Frame, and stack their
    // text blocks from measured glyph metrics (see Label) so nothing overlaps at any key size.
    internal static class CardRenderer
    {
        private static readonly BitmapColor Green = new(0x33, 0xC4, 0x4C);
        private static readonly BitmapColor Amber = new(0xFF, 0xC1, 0x07);
        private static readonly BitmapColor Red = new(0xFF, 0x2D, 0x2D);
        private static readonly BitmapColor Gray = new(0x9E, 0x9E, 0x9E);
        private static readonly BitmapColor Light = new(0xC9, 0xCE, 0xD6);
        private static readonly BitmapColor Track = new(0x23, 0x27, 0x2E);
        private static readonly BitmapColor White = BitmapColor.White;

        private const Int32 MinFont = 10;
        private const Int32 MinFitFont = 8;

        public static BitmapColor ColorForValue(Double? value)
        {
            if (!value.HasValue)
            {
                return Gray;
            }

            if (value.Value < 60.0)
            {
                return Green;
            }

            return value.Value < 85.0 ? Amber : Red;
        }

        public static void DrawOffline(BitmapBuilder builder, String title, String message)
        {
            LegacyDrawTextAt(builder, title, 6, 6, 20, White);
            LegacyDrawTextAt(builder, message, 6, 34, 14, Gray);
        }

        // ---------------------------------------------------------------- Classic (original layout, untouched)

        public static void DrawClassic(BitmapBuilder builder, HardwareDisplayData data, Double[] history)
        {
            var width = builder.Width;
            var height = builder.Height;
            var margin = 6;

            // Fonts are kept in a narrow band for a consistent look: title/load% row ~16-18,
            // big value ~20-24, footer ~13-15. Long footers (GPU "8.0 / 12.0 GB", RAM used/total)
            // use the smaller end so they don't run off the button.
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

            LegacyDrawTextPairCentered(builder, data.Title, White, data.LeftValue, loadColor, width, titleY, titleFont);
            LegacyDrawTextCenter(builder, data.RightValue, width, primaryY, primaryFont, ColorForValue(data.ColorValue));
            DrawSparkline(builder, history, margin, sparkY, width - margin * 2, sparkHeight, loadColor);

            if (hasFooter2)
            {
                LegacyDrawTextCenter(builder, data.Footer1, width, footerY - footerFont - 2, footerFont, White);
                LegacyDrawTextCenter(builder, data.Footer2, width, footerY, footerFont, White);
            }
            else
            {
                LegacyDrawTextCenter(builder, data.Footer1, width, footerY, footerFont, White);
            }
        }

        private static void DrawSparkline(BitmapBuilder builder, Double[] history, Int32 x, Int32 y, Int32 width, Int32 height, BitmapColor color)
        {
            var count = history.Length;
            if (count == 0)
            {
                return;
            }

            var innerWidth = width - 1;
            var innerHeight = Math.Max(1, height - 1);
            var prevX = 0f;
            var prevY = 0f;
            for (var i = 0; i < count; i++)
            {
                var t = count > 1 ? (Single)i / (count - 1) : 0f;
                var pointX = x + t * innerWidth;
                var pointY = y + (1f - Clamp(history[i]) / 100f) * innerHeight;
                if (i > 0)
                {
                    builder.DrawLine(prevX, prevY, pointX, pointY, color, 1.5f);
                }

                prevX = pointX;
                prevY = pointY;
            }
        }

        // ---------------------------------------------------------------- A: ring gauge

        public static void DrawRing(BitmapBuilder builder, HardwareDisplayData data)
        {
            var f = new Frame(builder);
            var load = Clamp(data.LoadPercent);
            var cx = f.X(90);
            var cy = f.Y(86);
            var radius = f.L(62);
            var stroke = Math.Max(3f, 12f * f.S);

            builder.DrawArc(cx, cy, radius, 135f, 270f, Track, stroke);
            if (load > 0f)
            {
                builder.DrawArc(cx, cy, radius, 135f, 270f * load / 100f, ColorForValue(data.LoadPercent), stroke);
            }

            // Text inside the ring stays within its inner diameter, minus a little air.
            var inner = 2f * (radius - stroke / 2f) - f.L(26);
            var loadFont = FitFont(data.LoadText, f.Font(40), inner);
            var secondaryFont = FitFont(data.Secondary, f.Font(24), inner * 0.9f);
            var gap = f.L(9);
            var blockHeight = Cap(loadFont) + gap + Cap(secondaryFont);
            var loadTop = cy - blockHeight / 2 - f.L(4);

            Label(builder, data.LoadText, cx, loadTop, loadFont, White, Align.Center);
            Label(builder, data.Secondary, cx, loadTop + Cap(loadFont) + gap, secondaryFont, SecondaryColor(data), Align.Center);

            var titleFont = f.Font(22);
            Label(builder, data.Title, cx, f.Y(168) - Cap(titleFont), titleFont, Gray, Align.Center);
        }

        // ---------------------------------------------------------------- B: fill level

        public static void DrawFill(BitmapBuilder builder, HardwareDisplayData data)
        {
            var f = new Frame(builder);
            var load = Clamp(data.LoadPercent);
            var color = ColorForValue(data.LoadPercent);
            var left = f.X(12);
            var right = f.X(168);

            var titleFont = f.Font(22);
            var titleTop = f.Y(14);
            var titleBottom = titleTop + Cap(titleFont);

            var numberTop = titleBottom + f.L(14);
            var numberBottom = numberTop + Cap(f.Font(60));

            var top = f.Y(180f - 180f * load / 100f);
            var bottom = f.Y(180);
            if (bottom > top)
            {
                builder.FillRectangle(0, top, builder.Width, bottom - top, new BitmapColor(color, 82));

                // The bright edge is skipped where it would cut through the title or the big number.
                if (top < titleTop - f.L(4) || top > numberBottom + f.L(6))
                {
                    builder.FillRectangle(0, top, builder.Width, Math.Min(f.L(3), bottom - top), color);
                }
            }

            Label(builder, data.Title, left, titleTop, titleFont, Light, Align.Left);
            DrawNumberWithPercent(builder, data.LoadPercent, left, numberTop, f.Font(60), f.Font(26), right - left, White, Light);

            var line1 = data.TempText ?? data.Footer1;
            var line2 = data.TempText != null ? data.Footer1 : data.Footer2;
            var line1Font = FitFont(line1, f.Font(23), right - left);
            var line2Font = FitFont(line2, f.Font(23), right - left);
            var line2Top = f.Y(168) - Cap(line2Font);
            var line1Top = line2Top - f.L(10) - Cap(line1Font);
            Label(builder, line1, left, line1Top, line1Font, White, Align.Left);
            Label(builder, line2, left, line2Top, line2Font, White, Align.Left);
        }

        // ---------------------------------------------------------------- C: one big number

        public static void DrawBigNumber(BitmapBuilder builder, HardwareDisplayData data)
        {
            var f = new Frame(builder);
            var load = Clamp(data.LoadPercent);
            var color = ColorForValue(data.LoadPercent);
            var left = f.X(14);
            var right = f.X(166);

            var titleFont = f.Font(22);
            var titleTop = f.Y(16);
            Label(builder, data.Title, left, titleTop, titleFont, Gray, Align.Left);

            var numberTop = titleTop + Cap(titleFont) + f.L(14);
            var numberFont = DrawNumberWithPercent(builder, data.LoadPercent, left, numberTop, f.Font(72), f.Font(26), right - left, color, color);

            var barHeight = f.L(8);
            var barTop = f.Y(164) - barHeight;
            var secondaryTop = numberTop + Cap(numberFont) + f.L(14);
            if (data.TempText != null)
            {
                var font = FitFont(data.TempText, f.Font(28), right - left);
                Label(builder, data.TempText, left, secondaryTop, font, ColorForValue(data.TempC), Align.Left);
            }
            else
            {
                var font = FitFont(data.Footer1, f.Font(24), right - left);
                Label(builder, data.Footer1, left, secondaryTop, font, White, Align.Left);
            }

            DrawBar(builder, left, barTop, right - left, barHeight, load, color);
        }

        // ---------------------------------------------------------------- D: history graph under the numbers

        public static void DrawGraph(BitmapBuilder builder, HardwareDisplayData data, Double[] history)
        {
            var f = new Frame(builder);
            var color = ColorForValue(data.LoadPercent);
            var left = f.X(12);
            var right = f.X(168);

            var titleFont = f.Font(21);
            var titleTop = f.Y(12);
            Label(builder, data.Title, left, titleTop, titleFont, Light, Align.Left);
            if (data.TempText != null)
            {
                Label(builder, data.TempText, right, titleTop, titleFont, ColorForValue(data.TempC), Align.Right);
            }

            var valueFont = FitFont(data.LoadText, f.Font(40), right - left);
            var valueTop = titleTop + Cap(titleFont) + f.L(9);
            Label(builder, data.LoadText, left, valueTop, valueFont, White, Align.Left);

            var footerFont = FitFont(data.Footer1, f.Font(20), right - left);
            var footerTop = valueTop + Cap(valueFont) + f.L(9);
            Label(builder, data.Footer1, left, footerTop, footerFont, White, Align.Left);

            var graphTop = footerTop + Cap(footerFont) + f.L(10);
            DrawArea(builder, history, f.X(0), graphTop, f.X(180), f.Y(180), color, Math.Max(1.5f, 2.5f * f.S));
        }

        // The SDK has no polygon fill, so the area under the line is filled one pixel column at a time.
        private static void DrawArea(BitmapBuilder builder, Double[] history, Int32 left, Int32 top, Int32 right, Int32 bottom, BitmapColor color, Single stroke)
        {
            var count = history.Length;
            var width = right - left;
            var height = bottom - top;
            if (count < 2 || width < 2 || height < 2)
            {
                return;
            }

            var fill = new BitmapColor(color, 77);
            for (var px = 0; px < width; px++)
            {
                var t = (Single)px / (width - 1) * (count - 1);
                var i = Math.Min((Int32)t, count - 2);
                var value = history[i] + (history[i + 1] - history[i]) * (t - i);
                var y = top + (Int32)Math.Round(height * (1f - Clamp(value) / 100f));
                if (y < bottom)
                {
                    builder.FillRectangle(left + px, y, 1, bottom - y, fill);
                }
            }

            for (var i = 1; i < count; i++)
            {
                var x1 = left + (Single)(i - 1) / (count - 1) * (width - 1);
                var x2 = left + (Single)i / (count - 1) * (width - 1);
                var y1 = top + height * (1f - Clamp(history[i - 1]) / 100f);
                var y2 = top + height * (1f - Clamp(history[i]) / 100f);
                builder.DrawLine(x1, y1, x2, y2, color, stroke);
            }
        }

        // ---------------------------------------------------------------- E: all three metrics on one button

        public static void DrawSystemBars(BitmapBuilder builder, HardwareDisplayData[] rows)
        {
            var f = new Frame(builder);
            var left = f.X(12);
            var right = f.X(168);
            var titleFont = f.Font(20);
            var valueFont = f.Font(22);
            var barHeight = f.L(8);
            var textHeight = Math.Max(Cap(titleFont), Cap(valueFont));
            var barGap = f.L(7);
            var rowHeight = textHeight + barGap + barHeight;

            var areaTop = f.Y(14);
            var areaBottom = f.Y(166);
            var rowGap = Math.Max(f.L(4), (areaBottom - areaTop - 3 * rowHeight) / 2);
            var top = areaTop + (areaBottom - areaTop - (3 * rowHeight + 2 * rowGap)) / 2;

            foreach (var row in rows)
            {
                var textBottom = top + textHeight;
                var valueColor = ColorForValue(row.LoadPercent);

                Label(builder, row.Title, left, textBottom - Cap(titleFont), titleFont, White, Align.Left);
                Label(builder, row.LoadText, right, textBottom - Cap(valueFont), valueFont, valueColor, Align.Right);

                // The secondary value fills the gap between title and value only if it fits there.
                var secondaryLeft = left + Measure(row.Title, titleFont) + f.L(7);
                var secondaryRoom = right - Measure(row.LoadText, valueFont) - f.L(7) - secondaryLeft;
                var secondaryFont = FitFont(row.Secondary, f.Font(18), secondaryRoom);
                if (Measure(row.Secondary, secondaryFont) <= secondaryRoom)
                {
                    Label(builder, row.Secondary, secondaryLeft, textBottom - Cap(secondaryFont), secondaryFont, SecondaryColor(row), Align.Left);
                }

                DrawBar(builder, left, textBottom + barGap, right - left, barHeight, Clamp(row.LoadPercent), valueColor);
                top += rowHeight + rowGap;
            }
        }

        public static void DrawSystemColumns(BitmapBuilder builder, HardwareDisplayData[] columns)
        {
            var f = new Frame(builder);
            var centers = new[] { 38f, 90f, 142f };
            var numberFont = f.Font(23);
            var labelFont = f.Font(19);
            var numberTop = f.Y(16);
            var labelTop = f.Y(166) - Cap(labelFont);
            var trackTop = numberTop + Cap(numberFont) + f.L(9);
            var trackBottom = labelTop - f.L(9);
            var trackHeight = Math.Max(1, trackBottom - trackTop);
            var trackWidth = f.L(40);

            for (var i = 0; i < columns.Length; i++)
            {
                var column = columns[i];
                var color = ColorForValue(column.LoadPercent);
                var cx = f.X(centers[i]);
                var trackX = cx - trackWidth / 2;
                var fillHeight = (Int32)Math.Round(trackHeight * Clamp(column.LoadPercent) / 100f);

                // Bare numbers: "100%" three times does not fit side by side on a small key.
                var number = LoadNumber(column.LoadPercent);
                Label(builder, number, cx, numberTop, FitFont(number, numberFont, f.L(48)), color, Align.Center);

                builder.FillRectangle(trackX, trackTop, trackWidth, trackHeight, Track);
                if (fillHeight > 0)
                {
                    builder.FillRectangle(trackX, trackTop + trackHeight - fillHeight, trackWidth, fillHeight, color);
                }

                Label(builder, column.Title, cx, labelTop, FitFont(column.Title, labelFont, f.L(44)), Gray, Align.Center);
            }
        }

        public static void DrawSystemRings(BitmapBuilder builder, HardwareDisplayData[] rings)
        {
            var f = new Frame(builder);
            var centers = new[] { 36f, 90f, 144f };
            var radius = f.L(22);
            var stroke = Math.Max(2f, 6f * f.S);
            var ringOuter = 2 * radius + (Int32)Math.Ceiling(stroke);
            var columnWidth = f.L(52);

            var headerFont = f.Font(16);
            var labelFont = f.Font(17);
            var secondaryFont = f.Font(19);
            foreach (var ring in rings)
            {
                labelFont = Math.Min(labelFont, FitFont(ring.Title, f.Font(17), columnWidth - f.L(8)));
                secondaryFont = Math.Min(secondaryFont, FitFont(ring.Secondary, f.Font(19), columnWidth));
            }

            var headerGap = f.L(12);
            var ringGap = f.L(9);
            var lineGap = f.L(6);
            var total = Cap(headerFont) + headerGap + ringOuter + ringGap + Cap(labelFont) + lineGap + Cap(secondaryFont);
            var top = (builder.Height - total) / 2;

            Label(builder, "SYSTEM", f.X(90), top, headerFont, Gray, Align.Center);
            var cy = top + Cap(headerFont) + headerGap + ringOuter / 2;
            var labelTop = cy + ringOuter / 2 + ringGap;
            var secondaryTop = labelTop + Cap(labelFont) + lineGap;
            var numberRoom = 2f * (radius - stroke / 2f) - 2f;

            for (var i = 0; i < rings.Length; i++)
            {
                var ring = rings[i];
                var load = Clamp(ring.LoadPercent);
                var cx = f.X(centers[i]);

                builder.DrawArc(cx, cy, radius, 0f, 360f, Track, stroke);
                if (load > 0f)
                {
                    builder.DrawArc(cx, cy, radius, -90f, 360f * load / 100f, ColorForValue(ring.LoadPercent), stroke);
                }

                var number = LoadNumber(ring.LoadPercent);
                var numberFont = FitFont(number, f.Font(18), numberRoom, 7);
                Label(builder, number, cx, cy - Cap(numberFont) / 2, numberFont, White, Align.Center);
                Label(builder, ring.Title, cx, labelTop, labelFont, Gray, Align.Center);
                Label(builder, ring.Secondary, cx, secondaryTop, secondaryFont, SecondaryColor(ring), Align.Center);
            }
        }

        // ---------------------------------------------------------------- shared pieces

        private static void DrawBar(BitmapBuilder builder, Int32 x, Int32 y, Int32 width, Int32 height, Single percent, BitmapColor color)
        {
            builder.FillRectangle(x, y, width, height, Track);
            var filled = (Int32)Math.Round(width * percent / 100f);
            if (filled > 0)
            {
                builder.FillRectangle(x, y, filled, height, color);
            }
        }

        // Big number with a smaller "%" sitting on its baseline; shrinks both to fit maxWidth. Returns the number's font.
        private static Int32 DrawNumberWithPercent(BitmapBuilder builder, Double? value, Int32 left, Int32 top, Int32 numberFont, Int32 percentFont, Single maxWidth, BitmapColor numberColor, BitmapColor percentColor)
        {
            var number = LoadNumber(value);
            var gap = Math.Max(1, numberFont / 24);
            while (numberFont > MinFitFont && Measure(number, numberFont) + gap + Measure("%", percentFont) > maxWidth)
            {
                numberFont--;
                percentFont = Math.Max(MinFitFont, Math.Min(percentFont, numberFont / 2));
            }

            Label(builder, number, left, top, numberFont, numberColor, Align.Left);
            Label(builder, "%", left + Measure(number, numberFont) + gap, top + Cap(numberFont) - Cap(percentFont), percentFont, percentColor, Align.Left);
            return numberFont;
        }

        private static String LoadNumber(Double? value)
            => value.HasValue ? value.Value.ToString("F0", CultureInfo.InvariantCulture) : "--";

        private static BitmapColor SecondaryColor(HardwareDisplayData data)
            => data.TempC.HasValue ? ColorForValue(data.TempC) : Light;

        private static Single Clamp(Double? value)
            => value.HasValue ? (Single)Math.Min(100.0, Math.Max(0.0, value.Value)) : 0f;

        // ---------------------------------------------------------------- measured text placement

        private enum Align
        {
            Left,
            Center,
            Right,
        }

        // Advance widths of the SDK's default font as a fraction of the font size, measured by
        // rendering through BitmapBuilder itself. Unlisted characters fall back to DefaultAdvance.
        private static readonly Dictionary<Char, Single> Advance = new()
        {
            ['0'] = 0.620f, ['1'] = 0.329f, ['2'] = 0.560f, ['3'] = 0.547f, ['4'] = 0.589f,
            ['5'] = 0.540f, ['6'] = 0.556f, ['7'] = 0.504f, ['8'] = 0.571f, ['9'] = 0.556f,
            ['%'] = 0.809f, ['°'] = 0.413f, ['.'] = 0.291f, [':'] = 0.300f, ['/'] = 0.447f,
            ['-'] = 0.404f, [' '] = 0.260f,
            ['A'] = 0.669f, ['B'] = 0.627f, ['C'] = 0.704f, ['D'] = 0.744f, ['E'] = 0.542f,
            ['F'] = 0.507f, ['G'] = 0.744f, ['H'] = 0.773f, ['I'] = 0.262f, ['K'] = 0.602f,
            ['L'] = 0.518f, ['M'] = 0.847f, ['N'] = 0.773f, ['O'] = 0.800f, ['P'] = 0.591f,
            ['Q'] = 0.804f, ['R'] = 0.620f, ['S'] = 0.524f, ['T'] = 0.511f, ['U'] = 0.744f,
            ['V'] = 0.649f, ['W'] = 0.956f, ['X'] = 0.580f, ['Y'] = 0.600f, ['Z'] = 0.564f,
            ['a'] = 0.591f, ['b'] = 0.591f, ['c'] = 0.520f, ['d'] = 0.591f, ['e'] = 0.533f,
            ['f'] = 0.347f, ['g'] = 0.591f, ['h'] = 0.584f, ['i'] = 0.231f, ['j'] = 0.240f,
            ['k'] = 0.493f, ['l'] = 0.231f, ['m'] = 0.924f, ['n'] = 0.584f, ['o'] = 0.576f,
            ['p'] = 0.591f, ['q'] = 0.591f, ['r'] = 0.344f, ['s'] = 0.427f, ['t'] = 0.373f,
            ['u'] = 0.578f, ['v'] = 0.507f, ['w'] = 0.744f, ['x'] = 0.469f, ['y'] = 0.529f,
            ['z'] = 0.476f,
        };

        private const Single DefaultAdvance = 0.6f;

        private static Single Measure(String text, Int32 fontSize)
        {
            if (String.IsNullOrEmpty(text))
            {
                return 0f;
            }

            var sum = 0f;
            foreach (var c in text)
            {
                sum += Advance.TryGetValue(c, out var a) ? a : DefaultAdvance;
            }

            return sum * fontSize;
        }

        // Height of digits/capitals in px (measured: 0.75 of the font size).
        private static Int32 Cap(Int32 fontSize) => (Int32)Math.Round(fontSize * 0.75);

        private static Int32 FitFont(String text, Int32 fontSize, Single maxWidth, Int32 minFont = MinFitFont)
        {
            while (fontSize > minFont && Measure(text, fontSize) > maxWidth)
            {
                fontSize--;
            }

            return fontSize;
        }

        // Places text by the top of its digits/capitals. DrawText centers the text inside its box
        // both ways, so the box is centered on where the text should be and given slack so it never
        // wraps; with a box height of font+8 the glyph top lands 4 + 0.27*font px below the box top.
        private static void Label(BitmapBuilder builder, String text, Single x, Int32 inkTop, Int32 fontSize, BitmapColor color, Align align)
        {
            if (String.IsNullOrEmpty(text))
            {
                return;
            }

            var width = Measure(text, fontSize);
            var center = align switch
            {
                Align.Left => x + width / 2f,
                Align.Right => x - width / 2f,
                _ => x,
            };

            var boxWidth = (Int32)Math.Ceiling(width) + fontSize;
            var boxX = (Int32)Math.Round(center - boxWidth / 2f);
            var boxY = inkTop - (Int32)Math.Round(4 + 0.27 * fontSize);
            builder.DrawText(text, boxX, boxY, boxWidth, fontSize + 8, color, fontSize, fontSize, 0, null);
        }

        // ---------------------------------------------------------------- legacy text helpers (Classic and offline only)

        private static Int32 LegacyWidth(String text, Int32 fontSize)
            => String.IsNullOrEmpty(text) ? 0 : (Int32)(fontSize * 0.52 * text.Length);

        private static Int32 LegacyGlyphOffset(Int32 fontSize) => (Int32)Math.Round(14.0 - 0.2 * fontSize);

        private static void LegacyDrawTextAt(BitmapBuilder builder, String text, Int32 x, Int32 yTop, Int32 fontSize, BitmapColor color)
        {
            if (String.IsNullOrEmpty(text))
            {
                return;
            }

            var width = LegacyWidth(text, fontSize) + 4;
            var boxY = Math.Max(0, yTop - LegacyGlyphOffset(fontSize));
            builder.DrawText(text, x, boxY, width, fontSize + 8, color, fontSize, fontSize, 0, null);
        }

        private static void LegacyDrawTextCenter(BitmapBuilder builder, String text, Int32 canvasWidth, Int32 yTop, Int32 fontSize, BitmapColor color)
        {
            if (String.IsNullOrEmpty(text))
            {
                return;
            }

            var width = (Int32)(fontSize * 0.62 * text.Length) + 4;
            var x = Math.Max(0, (canvasWidth - width) / 2);
            var boxY = Math.Max(0, yTop - LegacyGlyphOffset(fontSize));
            builder.DrawText(text, x, boxY, width, fontSize + 8, color, fontSize, fontSize, 0, null);
        }

        private static void LegacyDrawTextPairCentered(BitmapBuilder builder, String left, BitmapColor leftColor, String right, BitmapColor rightColor, Int32 canvasWidth, Int32 yTop, Int32 fontSize)
        {
            var leftWidth = String.IsNullOrEmpty(left) ? 0 : LegacyWidth(left, fontSize) + 4;
            var rightWidth = String.IsNullOrEmpty(right) ? 0 : LegacyWidth(right, fontSize) + 4;
            var gap = 8;
            var totalWidth = leftWidth + rightWidth + (leftWidth > 0 && rightWidth > 0 ? gap : 0);
            var startX = Math.Max(0, (canvasWidth - totalWidth) / 2);

            if (leftWidth > 0)
            {
                LegacyDrawTextAt(builder, left, startX, yTop, fontSize, leftColor);
            }

            if (rightWidth > 0)
            {
                LegacyDrawTextAt(builder, right, startX + leftWidth + gap, yTop, fontSize, rightColor);
            }
        }

        // Maps the 180x180 design grid onto the real button, centered if it isn't square.
        private readonly struct Frame
        {
            public Frame(BitmapBuilder builder)
            {
                this.S = Math.Min(builder.Width, builder.Height) / 180f;
                this.OffsetX = (builder.Width - 180f * this.S) / 2f;
                this.OffsetY = (builder.Height - 180f * this.S) / 2f;
            }

            public Single S { get; }
            private Single OffsetX { get; }
            private Single OffsetY { get; }

            public Int32 X(Single v) => (Int32)Math.Round(this.OffsetX + v * this.S);
            public Int32 Y(Single v) => (Int32)Math.Round(this.OffsetY + v * this.S);
            public Int32 L(Single v) => Math.Max(1, (Int32)Math.Round(v * this.S));
            public Int32 Font(Single v) => Math.Max(MinFont, (Int32)Math.Round(v * this.S));
        }
    }
}
