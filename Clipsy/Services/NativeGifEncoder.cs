using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Threading.Tasks;
using Windows.Media.Editing;
using Windows.Media;
using Windows.Storage;

namespace Clipsy.Services;

/// <summary>Dependency-free animated GIF encoder (fallback when FFmpeg is absent):
/// MediaComposition frames → median-cut palette → optional dither → hand-written LZW.</summary>
public static class NativeGifEncoder
{
    // Cap on extracted frames to bound memory/time for long clips.
    private const int MaxFrames = 600;

    public static async Task<bool> ConvertMp4ToGifAsync(string inputMp4, string outputGif)
    {
        try
        {
            var s      = SettingsService.Instance.Settings;
            int fps    = Math.Clamp(s.GifFps, 1, 50);
            int colors = Math.Clamp(s.GifColors, 2, 256);
            bool dither = s.GifDither;

            var frames = await ExtractFramesAsync(inputMp4, fps);
            if (frames.Count == 0)
            {
                Debug.WriteLine("[Clipsy] NativeGif: no frames extracted");
                return false;
            }

            try
            {
                await Task.Run(() => Encode(frames, outputGif, fps, colors, dither));
            }
            finally
            {
                foreach (var f in frames) f.Dispose();
            }

            return File.Exists(outputGif) && new FileInfo(outputGif).Length > 0;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Clipsy] NativeGif failed: {ex.Message}");
            return false;
        }
    }

    // ─── Frame extraction ─────────────────────────────────────────────────────

    private static async Task<List<Bitmap>> ExtractFramesAsync(string inputMp4, int fps)
    {
        var frames = new List<Bitmap>();

        var file        = await StorageFile.GetFileFromPathAsync(inputMp4);
        var clip        = await MediaClip.CreateFromFileAsync(file);
        var composition = new MediaComposition();
        composition.Clips.Add(clip);

        var duration = composition.Duration;
        var step     = TimeSpan.FromSeconds(1.0 / fps);

        for (var t = TimeSpan.Zero; t < duration; t += step)
        {
            try
            {
                var thumb = await composition.GetThumbnailAsync(
                    t, 0, 0, VideoFramePrecision.NearestFrame);
                if (thumb == null) continue;

                using var stream = thumb.AsStreamForRead();
                frames.Add(new Bitmap(stream));
            }
            catch
            {
                // Skip frames the decoder can't seek to.
            }

            if (frames.Count >= MaxFrames) break;
        }

        return frames;
    }

    // ─── Encode ─────────────────────────────────────────────────────────────

    private static void Encode(List<Bitmap> frames, string outputGif, int fps, int maxColors, bool dither)
    {
        int width  = frames[0].Width;
        int height = frames[0].Height;

        // Pull raw RGB pixels for every frame (resampled to the first frame's
        // size so the canvas is uniform).
        var pixelFrames = new List<byte[]>(frames.Count); // each: width*height*3 (RGB)
        foreach (var bmp in frames)
            pixelFrames.Add(ReadRgb(bmp, width, height));

        // Global palette across all frames.
        var palette = MedianCut.BuildPalette(pixelFrames, maxColors);

        // GIF delay is in centiseconds (1/100 s). Round and keep >=2 so players
        // don't treat 0 as "as fast as possible".
        int delayCs = Math.Max(2, (int)Math.Round(100.0 / fps));

        using var fs = new FileStream(outputGif, FileMode.Create, FileAccess.Write);
        using var w  = new BinaryWriter(fs);

        WriteHeader(w, width, height, palette);
        WriteLoopExtension(w);

        foreach (var rgb in pixelFrames)
        {
            var indices = dither
                ? Quantizer.MapDithered(rgb, width, height, palette)
                : Quantizer.MapNearest(rgb, palette);

            WriteFrame(w, width, height, indices, delayCs, palette.Count);
        }

        w.Write((byte)0x3B); // trailer
    }

    /// <summary>Decode a bitmap into tightly packed RGB bytes at the target size.</summary>
    private static byte[] ReadRgb(Bitmap src, int width, int height)
    {
        using var canvas = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(canvas))
        {
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.DrawImage(src, 0, 0, width, height);
        }

        var data = canvas.LockBits(
            new Rectangle(0, 0, width, height),
            ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);

        var rgb = new byte[width * height * 3];
        try
        {
            unsafe
            {
                byte* basePtr = (byte*)data.Scan0;
                int stride = data.Stride;
                for (int y = 0; y < height; y++)
                {
                    byte* row = basePtr + y * stride;
                    int dst = y * width * 3;
                    for (int x = 0; x < width; x++)
                    {
                        // BGRA in memory.
                        rgb[dst++] = row[x * 4 + 2]; // R
                        rgb[dst++] = row[x * 4 + 1]; // G
                        rgb[dst++] = row[x * 4 + 0]; // B
                    }
                }
            }
        }
        finally
        {
            canvas.UnlockBits(data);
        }
        return rgb;
    }

    // ─── GIF stream writers ───────────────────────────────────────────────────

    private static void WriteHeader(BinaryWriter w, int width, int height, List<(byte R, byte G, byte B)> palette)
    {
        w.Write(new[] { (byte)'G', (byte)'I', (byte)'F', (byte)'8', (byte)'9', (byte)'a' });

        w.Write((ushort)width);
        w.Write((ushort)height);

        int gctSize = PaletteSizeExponent(palette.Count); // 2^(n+1) entries
        // Global Color Table flag (0x80) | color resolution (7<<4) | GCT size
        byte packed = (byte)(0x80 | (0x7 << 4) | gctSize);
        w.Write(packed);
        w.Write((byte)0);  // background color index
        w.Write((byte)0);  // pixel aspect ratio

        WriteColorTable(w, palette, gctSize);
    }

    private static void WriteLoopExtension(BinaryWriter w)
    {
        w.Write((byte)0x21);           // extension introducer
        w.Write((byte)0xFF);           // application extension
        w.Write((byte)11);             // block size
        w.Write(new[] { (byte)'N', (byte)'E', (byte)'T', (byte)'S', (byte)'C',
                        (byte)'A', (byte)'P', (byte)'E', (byte)'2', (byte)'.', (byte)'0' });
        w.Write((byte)3);              // sub-block size
        w.Write((byte)1);              // loop sub-block id
        w.Write((ushort)0);            // loop count: 0 = forever
        w.Write((byte)0);              // block terminator
    }

    private static void WriteFrame(BinaryWriter w, int width, int height, byte[] indices, int delayCs, int paletteCount)
    {
        // Graphic Control Extension (animation delay).
        w.Write((byte)0x21);           // extension introducer
        w.Write((byte)0xF9);           // graphic control label
        w.Write((byte)4);              // block size
        w.Write((byte)0x00);           // no transparency, disposal = 0
        w.Write((ushort)delayCs);
        w.Write((byte)0);              // transparent color index
        w.Write((byte)0);              // block terminator

        // Image descriptor.
        w.Write((byte)0x2C);
        w.Write((ushort)0);            // left
        w.Write((ushort)0);            // top
        w.Write((ushort)width);
        w.Write((ushort)height);
        w.Write((byte)0);              // no local color table

        // LZW-compressed image data.
        int minCodeSize = Math.Max(2, PaletteSizeExponent(paletteCount) + 1);
        var lzw = LzwEncoder.Encode(indices, minCodeSize);

        w.Write((byte)minCodeSize);
        WriteSubBlocks(w, lzw);
        w.Write((byte)0);              // block terminator
    }

    // ─── Palette: median cut ──────────────────────────────────────────────────

    private static class MedianCut
    {
        public static List<(byte R, byte G, byte B)> BuildPalette(List<byte[]> frames, int maxColors)
        {
            // Subsample pixels across all frames so the cut is fast on long clips.
            var samples = new List<(byte R, byte G, byte B)>();
            long totalPixels = 0;
            foreach (var f in frames) totalPixels += f.Length / 3;

            int target = 40_000;
            int stride = (int)Math.Max(1, totalPixels / target);

            int counter = 0;
            foreach (var f in frames)
            {
                for (int i = 0; i + 2 < f.Length; i += 3)
                {
                    if (counter++ % stride == 0)
                        samples.Add((f[i], f[i + 1], f[i + 2]));
                }
            }

            if (samples.Count == 0) return new() { (0, 0, 0) };

            var boxes = new List<Box> { new Box(samples, 0, samples.Count) };
            while (boxes.Count < maxColors)
            {
                // Split the box with the largest color spread.
                int best = -1;
                int bestRange = -1;
                for (int i = 0; i < boxes.Count; i++)
                {
                    if (boxes[i].Count < 2) continue;
                    int range = boxes[i].LongestAxisRange();
                    if (range > bestRange) { bestRange = range; best = i; }
                }
                if (best < 0) break;

                var (a, b) = boxes[best].Split();
                boxes[best] = a;
                boxes.Add(b);
            }

            var palette = new List<(byte R, byte G, byte B)>(boxes.Count);
            foreach (var box in boxes) palette.Add(box.Average());
            return palette;
        }

        private sealed class Box
        {
            private readonly List<(byte R, byte G, byte B)> _all;
            private int _start;
            private int _count;

            public Box(List<(byte R, byte G, byte B)> all, int start, int count)
            {
                _all = all; _start = start; _count = count;
            }

            public int Count => _count;

            private (int axis, int range) WidestAxis()
            {
                byte rMin = 255, rMax = 0, gMin = 255, gMax = 0, bMin = 255, bMax = 0;
                for (int i = _start; i < _start + _count; i++)
                {
                    var (r, g, b) = _all[i];
                    if (r < rMin) rMin = r; if (r > rMax) rMax = r;
                    if (g < gMin) gMin = g; if (g > gMax) gMax = g;
                    if (b < bMin) bMin = b; if (b > bMax) bMax = b;
                }
                int dr = rMax - rMin, dg = gMax - gMin, db = bMax - bMin;
                if (dr >= dg && dr >= db) return (0, dr);
                if (dg >= db) return (1, dg);
                return (2, db);
            }

            public int LongestAxisRange() => WidestAxis().range;

            public (Box, Box) Split()
            {
                int axis = WidestAxis().axis;
                _all.Sort(_start, _count, Comparer<(byte R, byte G, byte B)>.Create((p, q) =>
                    axis switch
                    {
                        0 => p.R.CompareTo(q.R),
                        1 => p.G.CompareTo(q.G),
                        _ => p.B.CompareTo(q.B),
                    }));

                int mid = _count / 2;
                return (new Box(_all, _start, mid),
                        new Box(_all, _start + mid, _count - mid));
            }

            public (byte R, byte G, byte B) Average()
            {
                long r = 0, g = 0, b = 0;
                for (int i = _start; i < _start + _count; i++)
                {
                    r += _all[i].R; g += _all[i].G; b += _all[i].B;
                }
                int n = Math.Max(1, _count);
                return ((byte)(r / n), (byte)(g / n), (byte)(b / n));
            }
        }
    }

    // ─── Quantize frame → palette indices ─────────────────────────────────────

    private static class Quantizer
    {
        public static byte[] MapNearest(byte[] rgb, List<(byte R, byte G, byte B)> palette)
        {
            int px = rgb.Length / 3;
            var indices = new byte[px];
            var cache = new Dictionary<int, byte>();
            for (int i = 0; i < px; i++)
            {
                int r = rgb[i * 3], g = rgb[i * 3 + 1], b = rgb[i * 3 + 2];
                indices[i] = Nearest(r, g, b, palette, cache);
            }
            return indices;
        }

        public static byte[] MapDithered(byte[] rgb, int width, int height, List<(byte R, byte G, byte B)> palette)
        {
            // Floyd–Steinberg on a float working copy of the RGB plane.
            var work = new float[rgb.Length];
            for (int i = 0; i < rgb.Length; i++) work[i] = rgb[i];

            var indices = new byte[width * height];
            var cache = new Dictionary<int, byte>();

            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int p = (y * width + x) * 3;
                    int r = Clamp(work[p]);
                    int g = Clamp(work[p + 1]);
                    int b = Clamp(work[p + 2]);

                    byte idx = Nearest(r, g, b, palette, cache);
                    indices[y * width + x] = idx;

                    var pal = palette[idx];
                    float er = r - pal.R, eg = g - pal.G, eb = b - pal.B;

                    Spread(work, width, height, x + 1, y,     er, eg, eb, 7f / 16f);
                    Spread(work, width, height, x - 1, y + 1, er, eg, eb, 3f / 16f);
                    Spread(work, width, height, x,     y + 1, er, eg, eb, 5f / 16f);
                    Spread(work, width, height, x + 1, y + 1, er, eg, eb, 1f / 16f);
                }
            }
            return indices;
        }

        private static void Spread(float[] work, int width, int height, int x, int y, float er, float eg, float eb, float f)
        {
            if (x < 0 || x >= width || y < 0 || y >= height) return;
            int p = (y * width + x) * 3;
            work[p]     += er * f;
            work[p + 1] += eg * f;
            work[p + 2] += eb * f;
        }

        private static byte Nearest(int r, int g, int b, List<(byte R, byte G, byte B)> palette, Dictionary<int, byte> cache)
        {
            int key = (r << 16) | (g << 8) | b;
            if (cache.TryGetValue(key, out var hit)) return hit;

            int best = 0, bestDist = int.MaxValue;
            for (int i = 0; i < palette.Count; i++)
            {
                int dr = r - palette[i].R, dg = g - palette[i].G, db = b - palette[i].B;
                int dist = dr * dr + dg * dg + db * db;
                if (dist < bestDist) { bestDist = dist; best = i; if (dist == 0) break; }
            }
            cache[key] = (byte)best;
            return (byte)best;
        }

        private static int Clamp(float v) => v < 0 ? 0 : v > 255 ? 255 : (int)(v + 0.5f);
    }

    // ─── LZW (GIF variant) ────────────────────────────────────────────────────

    private static class LzwEncoder
    {
        public static byte[] Encode(byte[] indices, int minCodeSize)
        {
            int clearCode = 1 << minCodeSize;
            int eoiCode   = clearCode + 1;

            var output = new List<byte>();
            var bits = new BitWriter(output);

            int codeSize = minCodeSize + 1;
            var table = new Dictionary<string, int>();

            void ResetTable()
            {
                table.Clear();
                for (int i = 0; i < clearCode; i++)
                    table[((char)i).ToString()] = i;
                codeSize = minCodeSize + 1;
            }

            ResetTable();
            int nextCode = eoiCode + 1;

            bits.Write(clearCode, codeSize);

            if (indices.Length > 0)
            {
                string current = ((char)indices[0]).ToString();
                for (int i = 1; i < indices.Length; i++)
                {
                    char c = (char)indices[i];
                    string combined = current + c;
                    if (table.ContainsKey(combined))
                    {
                        current = combined;
                    }
                    else
                    {
                        bits.Write(table[current], codeSize);
                        table[combined] = nextCode++;

                        if (nextCode > (1 << codeSize) && codeSize < 12)
                            codeSize++;

                        if (nextCode > 4095)
                        {
                            bits.Write(clearCode, codeSize);
                            ResetTable();
                            nextCode = eoiCode + 1;
                        }

                        current = c.ToString();
                    }
                }
                bits.Write(table[current], codeSize);
            }

            bits.Write(eoiCode, codeSize);
            bits.Flush();
            return output.ToArray();
        }

        private sealed class BitWriter
        {
            private readonly List<byte> _out;
            private int _buffer;
            private int _bits;

            public BitWriter(List<byte> output) => _out = output;

            public void Write(int code, int codeSize)
            {
                _buffer |= code << _bits;
                _bits += codeSize;
                while (_bits >= 8)
                {
                    _out.Add((byte)(_buffer & 0xFF));
                    _buffer >>= 8;
                    _bits -= 8;
                }
            }

            public void Flush()
            {
                if (_bits > 0)
                {
                    _out.Add((byte)(_buffer & 0xFF));
                    _buffer = 0;
                    _bits = 0;
                }
            }
        }
    }

    private static void WriteColorTable(BinaryWriter w, List<(byte R, byte G, byte B)> palette, int sizeExponent)
    {
        int entries = 1 << (sizeExponent + 1);
        for (int i = 0; i < entries; i++)
        {
            if (i < palette.Count)
            {
                w.Write(palette[i].R);
                w.Write(palette[i].G);
                w.Write(palette[i].B);
            }
            else
            {
                w.Write((byte)0);
                w.Write((byte)0);
                w.Write((byte)0);
            }
        }
    }

    private static void WriteSubBlocks(BinaryWriter w, byte[] data)
    {
        int offset = 0;
        while (offset < data.Length)
        {
            int chunk = Math.Min(255, data.Length - offset);
            w.Write((byte)chunk);
            w.Write(data, offset, chunk);
            offset += chunk;
        }
    }

    /// <summary>Smallest n where 2^(n+1) >= count, clamped to GIF's 1..7 range.</summary>
    private static int PaletteSizeExponent(int count)
    {
        int n = 0;
        while ((1 << (n + 1)) < count && n < 7) n++;
        return n;
    }
}
