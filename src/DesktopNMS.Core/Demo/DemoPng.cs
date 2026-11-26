using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace DesktopNMS.Core.Demo;

/// <summary>
/// Draws a <see cref="DemoChart"/> as a PNG, laid out as its SVG is: rrdtool's
/// dotted grid, the areas and lines, values up the side, times along the bottom
/// and the legend under it. A small rasteriser with a 5×7 pixel font, so Core
/// needs no imaging package for a demo.
/// </summary>
internal static class DemoPng
{
    private static readonly byte[] Signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private static readonly uint[] CrcTable = BuildCrcTable();
    private static readonly (byte R, byte G, byte B) Black = (0, 0, 0);
    private static readonly (byte R, byte G, byte B) MinorGrid = (165, 165, 165);
    private static readonly (byte R, byte G, byte B) MajorGrid = (255, 153, 153);
    private static readonly (byte R, byte G, byte B) Axis = (31, 31, 31);

    public static byte[] Draw(DemoChart chart, DateTime now, bool legend)
    {
        // Bounded, so a request for an enormous graph can't ask for gigabytes.
        var canvas = new Canvas(Math.Clamp(chart.Width, 200, 2400), Math.Clamp(chart.Height, 100, 1200));
        var left = DemoGraphs.Left;
        var plotW = canvas.Width - left - 12;
        var plotH = canvas.Height - DemoGraphs.Top - DemoGraphs.Bottom;
        var bottom = DemoGraphs.Top + plotH;

        foreach (var line in chart.Series.Where(l => l.Area is not null))
        {
            var colour = Parse(line.Area!);
            for (var x = 0; x <= plotW; x++)
            {
                var top = (int)Math.Round(bottom - plotH * ValueAt(line.Values, x / (double)plotW));
                canvas.VerticalLine(left + x, top, bottom, colour);
            }
        }

        for (var g = 0; g <= 8; g++)
        {
            canvas.DottedHorizontal(left, left + plotW, (int)Math.Round(bottom - plotH * g / 8.0), g % 2 == 0 ? MajorGrid : MinorGrid);
        }

        foreach (var hour in new[] { 6, 12, 18 })
        {
            canvas.DottedVertical((int)Math.Round(left + plotW * hour / 24.0), DemoGraphs.Top, bottom, MajorGrid);
        }

        canvas.Line(left, bottom, left + plotW, bottom, Axis, 1);

        foreach (var line in chart.Series)
        {
            var colour = Parse(line.Stroke);
            for (var i = 1; i < line.Values.Length; i++)
            {
                double x0 = left + plotW * (i - 1) / (double)(line.Values.Length - 1), x1 = left + plotW * i / (double)(line.Values.Length - 1);
                canvas.Line(x0, bottom - plotH * line.Values[i - 1], x1, bottom - plotH * line.Values[i], colour, 2);
            }
        }

        for (var g = 0; g <= 4; g++)
        {
            var label = DemoGraphs.ValueLabel(chart.Unit, chart.Max, g);
            canvas.Text(left - 6 - Canvas.TextWidth(label), (int)Math.Round(bottom - plotH * g / 4.0) - 3, label, Black);
        }

        foreach (var hour in DemoGraphs.TimeHours)
        {
            var text = DemoGraphs.TimeLabel(now, hour);
            // Centred under its hour, but the last one stays inside the image.
            var x = Math.Min((int)Math.Round(left + plotW * hour / 24.0) - Canvas.TextWidth(text) / 2, canvas.Width - 1 - Canvas.TextWidth(text));
            canvas.Text(x, bottom + 6, text, Black);
        }

        if (legend)
        {
            var x = left;
            foreach (var line in chart.Series)
            {
                var colour = Parse(line.Stroke);
                canvas.Fill(x, canvas.Height - 11, 8, 8, colour);
                canvas.Text(x + 12, canvas.Height - 10, line.Label, Black, maxWidth: 46);
                x += 60;
            }
        }

        return canvas.Encode();
    }

    /// <summary>A series' value at a fraction of the way across, between its samples.</summary>
    private static double ValueAt(double[] values, double fraction)
    {
        var at = fraction * (values.Length - 1);
        var i = Math.Clamp((int)Math.Floor(at), 0, values.Length - 1);
        var next = Math.Min(i + 1, values.Length - 1);
        return values[i] + (values[next] - values[i]) * (at - i);
    }

    private static (byte R, byte G, byte B) Parse(string hex)
    {
        byte Channel(int at) => byte.Parse(hex.AsSpan(at, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return (Channel(1), Channel(3), Channel(5));
    }

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            }

            table[n] = c;
        }

        return table;
    }

    private sealed class Canvas
    {
        private const int GlyphWidth = 5;
        private const int Advance = 6;
        private readonly byte[] _pixels;

        public Canvas(int width, int height)
        {
            Width = width;
            Height = height;
            _pixels = new byte[width * height * 3];
            Array.Fill(_pixels, (byte)255);
        }

        public int Width { get; }

        public int Height { get; }

        public static int TextWidth(string text) => text.Length == 0 ? 0 : text.Length * Advance - 1;

        public void Set(int x, int y, (byte R, byte G, byte B) colour)
        {
            if (x < 0 || y < 0 || x >= Width || y >= Height) return;
            var at = (y * Width + x) * 3;
            _pixels[at] = colour.R;
            _pixels[at + 1] = colour.G;
            _pixels[at + 2] = colour.B;
        }

        public void Fill(int x, int y, int width, int height, (byte R, byte G, byte B) colour)
        {
            for (var dy = 0; dy < height; dy++)
            {
                for (var dx = 0; dx < width; dx++)
                {
                    Set(x + dx, y + dy, colour);
                }
            }
        }

        public void VerticalLine(int x, int top, int bottom, (byte R, byte G, byte B) colour)
        {
            for (var y = Math.Min(top, bottom); y <= Math.Max(top, bottom); y++)
            {
                Set(x, y, colour);
            }
        }

        public void DottedHorizontal(int x0, int x1, int y, (byte R, byte G, byte B) colour)
        {
            for (var x = x0; x <= x1; x += 2)
            {
                Set(x, y, colour);
            }
        }

        public void DottedVertical(int x, int y0, int y1, (byte R, byte G, byte B) colour)
        {
            for (var y = y0; y <= y1; y += 2)
            {
                Set(x, y, colour);
            }
        }

        /// <summary>A line <paramref name="thickness"/> pixels deep, stepped along its longer axis.</summary>
        public void Line(double x0, double y0, double x1, double y1, (byte R, byte G, byte B) colour, int thickness)
        {
            var steps = (int)Math.Ceiling(Math.Max(Math.Abs(x1 - x0), Math.Abs(y1 - y0)));
            for (var s = 0; s <= Math.Max(steps, 1); s++)
            {
                var t = steps == 0 ? 0 : s / (double)steps;
                var x = (int)Math.Round(x0 + (x1 - x0) * t);
                var y = (int)Math.Round(y0 + (y1 - y0) * t);
                for (var d = 0; d < thickness; d++)
                {
                    Set(x, y + d - thickness / 2, colour);
                }
            }
        }

        /// <summary>Text in the 5×7 font, from its top-left corner; letters print in capitals.</summary>
        public void Text(int x, int y, string text, (byte R, byte G, byte B) colour, int maxWidth = int.MaxValue)
        {
            var start = x;
            foreach (var c in text.ToUpperInvariant())
            {
                if (x - start + GlyphWidth > maxWidth) break;
                if (Font.TryGetValue(c, out var rows))
                {
                    for (var row = 0; row < rows.Length; row++)
                    {
                        for (var col = 0; col < GlyphWidth; col++)
                        {
                            if ((rows[row] & (1 << (GlyphWidth - 1 - col))) != 0) Set(x + col, y + row, colour);
                        }
                    }
                }

                x += Advance;
            }
        }

        public byte[] Encode()
        {
            // Each row starts with filter type 0: the bytes as they are.
            var raw = new byte[Height * (Width * 3 + 1)];
            for (var y = 0; y < Height; y++)
            {
                Buffer.BlockCopy(_pixels, y * Width * 3, raw, y * (Width * 3 + 1) + 1, Width * 3);
            }

            using var compressed = new MemoryStream();
            using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
            {
                zlib.Write(raw);
            }

            var header = new byte[13];
            BinaryPrimitives.WriteInt32BigEndian(header, Width);
            BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), Height);
            header[8] = 8; // bits per channel
            header[9] = 2; // RGB

            using var png = new MemoryStream();
            png.Write(Signature);
            Chunk(png, "IHDR", header);
            Chunk(png, "IDAT", compressed.ToArray());
            Chunk(png, "IEND", []);
            return png.ToArray();
        }

        private static void Chunk(Stream png, string type, byte[] data)
        {
            Span<byte> number = stackalloc byte[4];
            BinaryPrimitives.WriteInt32BigEndian(number, data.Length);
            png.Write(number);
            var typeBytes = Encoding.ASCII.GetBytes(type);
            png.Write(typeBytes);
            png.Write(data);
            var crc = Crc(Crc(0xFFFFFFFFu, typeBytes), data);

            BinaryPrimitives.WriteUInt32BigEndian(number, crc ^ 0xFFFFFFFFu);
            png.Write(number);
        }

        private static uint Crc(uint crc, byte[] bytes)
        {
            foreach (var b in bytes)
            {
                crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
            }

            return crc;
        }

        // Five bits a row, seven rows a glyph: digits, capitals and the few signs the labels use.
        private static readonly Dictionary<char, byte[]> Font = new()
        {
            ['0'] = [0x0E, 0x11, 0x13, 0x15, 0x19, 0x11, 0x0E],
            ['1'] = [0x04, 0x0C, 0x04, 0x04, 0x04, 0x04, 0x0E],
            ['2'] = [0x0E, 0x11, 0x01, 0x02, 0x04, 0x08, 0x1F],
            ['3'] = [0x1F, 0x02, 0x04, 0x02, 0x01, 0x11, 0x0E],
            ['4'] = [0x02, 0x06, 0x0A, 0x12, 0x1F, 0x02, 0x02],
            ['5'] = [0x1F, 0x10, 0x1E, 0x01, 0x01, 0x11, 0x0E],
            ['6'] = [0x06, 0x08, 0x10, 0x1E, 0x11, 0x11, 0x0E],
            ['7'] = [0x1F, 0x01, 0x02, 0x04, 0x08, 0x08, 0x08],
            ['8'] = [0x0E, 0x11, 0x11, 0x0E, 0x11, 0x11, 0x0E],
            ['9'] = [0x0E, 0x11, 0x11, 0x0F, 0x01, 0x02, 0x0C],
            [':'] = [0x00, 0x0C, 0x0C, 0x00, 0x0C, 0x0C, 0x00],
            ['.'] = [0x00, 0x00, 0x00, 0x00, 0x00, 0x0C, 0x0C],
            ['%'] = [0x18, 0x19, 0x02, 0x04, 0x08, 0x13, 0x03],
            ['-'] = [0x00, 0x00, 0x00, 0x1F, 0x00, 0x00, 0x00],
            ['/'] = [0x00, 0x01, 0x02, 0x04, 0x08, 0x10, 0x00],
            ['A'] = [0x0E, 0x11, 0x11, 0x1F, 0x11, 0x11, 0x11],
            ['B'] = [0x1E, 0x11, 0x11, 0x1E, 0x11, 0x11, 0x1E],
            ['C'] = [0x0E, 0x11, 0x10, 0x10, 0x10, 0x11, 0x0E],
            ['D'] = [0x1C, 0x12, 0x11, 0x11, 0x11, 0x12, 0x1C],
            ['E'] = [0x1F, 0x10, 0x10, 0x1E, 0x10, 0x10, 0x1F],
            ['F'] = [0x1F, 0x10, 0x10, 0x1E, 0x10, 0x10, 0x10],
            ['G'] = [0x0E, 0x11, 0x10, 0x17, 0x11, 0x11, 0x0F],
            ['H'] = [0x11, 0x11, 0x11, 0x1F, 0x11, 0x11, 0x11],
            ['I'] = [0x0E, 0x04, 0x04, 0x04, 0x04, 0x04, 0x0E],
            ['J'] = [0x07, 0x02, 0x02, 0x02, 0x02, 0x12, 0x0C],
            ['K'] = [0x11, 0x12, 0x14, 0x18, 0x14, 0x12, 0x11],
            ['L'] = [0x10, 0x10, 0x10, 0x10, 0x10, 0x10, 0x1F],
            ['M'] = [0x11, 0x1B, 0x15, 0x15, 0x11, 0x11, 0x11],
            ['N'] = [0x11, 0x11, 0x19, 0x15, 0x13, 0x11, 0x11],
            ['O'] = [0x0E, 0x11, 0x11, 0x11, 0x11, 0x11, 0x0E],
            ['P'] = [0x1E, 0x11, 0x11, 0x1E, 0x10, 0x10, 0x10],
            ['Q'] = [0x0E, 0x11, 0x11, 0x11, 0x15, 0x12, 0x0D],
            ['R'] = [0x1E, 0x11, 0x11, 0x1E, 0x14, 0x12, 0x11],
            ['S'] = [0x0F, 0x10, 0x10, 0x0E, 0x01, 0x01, 0x1E],
            ['T'] = [0x1F, 0x04, 0x04, 0x04, 0x04, 0x04, 0x04],
            ['U'] = [0x11, 0x11, 0x11, 0x11, 0x11, 0x11, 0x0E],
            ['V'] = [0x11, 0x11, 0x11, 0x11, 0x11, 0x0A, 0x04],
            ['W'] = [0x11, 0x11, 0x11, 0x15, 0x15, 0x15, 0x0A],
            ['X'] = [0x11, 0x11, 0x0A, 0x04, 0x0A, 0x11, 0x11],
            ['Y'] = [0x11, 0x11, 0x0A, 0x04, 0x04, 0x04, 0x04],
            ['Z'] = [0x1F, 0x01, 0x02, 0x04, 0x08, 0x10, 0x1F],
        };
    }
}
