using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Windows.Media.Editing;
using Windows.Storage;

namespace Clipsy.Services;

/// <summary>Dependency-free animated GIF encoder (fallback when FFmpeg is absent). Streams frames:
/// a few sampled frames build one median-cut palette, then each frame is decoded, quantized,
/// LZW-compressed and written before the next is read, so memory stays at one frame.</summary>
public static class NativeGifEncoder
{
    // Frame grabbing through MediaComposition is slow; bound the work for very long clips.
    public const int MaxFrames = 3000;
    private const int PaletteSampleFrames = 12;

    /// <returns>Number of frames written (0 on failure); <paramref name="truncated"/> when MaxFrames cut the clip.</returns>
    public static async Task<(bool Ok, bool Truncated)> ConvertMp4ToGifAsync(string inputMp4, string outputGif, CancellationToken ct = default)
    {
        try
        {
            var s = SettingsService.Instance.Settings;
            int fps = Math.Clamp(s.GifFps, 1, 50);
            int colors = Math.Clamp(s.GifColors, 2, 256);
            bool dither = s.GifDither;

            var file = await StorageFile.GetFileFromPathAsync(inputMp4);
            var clip = await MediaClip.CreateFromFileAsync(file);
            var composition = new MediaComposition();
            composition.Clips.Add(clip);

            int total = (int)Math.Floor(composition.Duration.TotalSeconds * fps);
            bool truncated = total > MaxFrames;
            int frameCount = Math.Max(1, Math.Min(total, MaxFrames));
            TimeSpan At(int i) => TimeSpan.FromSeconds(i / (double)fps);

            // Pass 1: palette from evenly spaced frames.
            var samples = new List<byte[]>();
            int width = 0, height = 0;
            for (int k = 0; k < Math.Min(PaletteSampleFrames, frameCount); k++)
            {
                int index = (int)((long)k * frameCount / Math.Min(PaletteSampleFrames, frameCount));
                using var bmp = await GrabFrameAsync(composition, At(index));
                if (bmp == null) continue;
                if (width == 0) { width = bmp.Width; height = bmp.Height; }
                samples.Add(ReadRgb(bmp, width, height));
            }
            if (samples.Count == 0)
            {
                Diagnostics.Log("NativeGif: no frames extracted");
                return (false, false);
            }
            var palette = MedianCut.BuildPalette(samples, colors);
            samples.Clear();

            // Pass 2: stream every frame.
            await using var fs = new FileStream(outputGif, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
            var gif = new GifWriter(fs, width, height, palette, dither);
            int written = 0;
            for (int i = 0; i < frameCount; i++)
            {
                ct.ThrowIfCancellationRequested();
                using var bmp = await GrabFrameAsync(composition, At(i));
                if (bmp == null) continue;
                var rgb = ReadRgb(bmp, width, height);
                int delay = FrameDelayCs(i, fps);
                await Task.Run(() => gif.AddFrame(rgb, delay), ct);
                written++;
            }
            gif.Finish();
            if (truncated) Diagnostics.Log($"NativeGif: clip truncated to {MaxFrames} frames");
            return (written > 0, truncated);
        }
        catch (OperationCanceledException)
        {
            return (false, false);
        }
        catch (Exception ex)
        {
            Diagnostics.Log("NativeGif failed", ex);
            return (false, false);
        }
    }

    private static async Task<Bitmap?> GrabFrameAsync(MediaComposition composition, TimeSpan at)
    {
        try
        {
            var thumb = await composition.GetThumbnailAsync(at, 0, 0, VideoFramePrecision.NearestFrame);
            if (thumb == null) return null;
            using var stream = thumb.AsStreamForRead();
            return new Bitmap(stream);
        }
        catch
        {
            return null; // skip frames the decoder can't seek to
        }
    }

    // Centisecond delays rounded cumulatively so playback speed doesn't drift (12 fps = 8,8,9,...).
    internal static int FrameDelayCs(int index, int fps)
        => Math.Max(2, (int)Math.Round((index + 1) * 100.0 / fps) - (int)Math.Round(index * 100.0 / fps));

    internal static List<(byte R, byte G, byte B)> BuildPalette(List<byte[]> rgbFrames, int maxColors)
        => MedianCut.BuildPalette(rgbFrames, maxColors);

    /// <summary>Writes a looping GIF frame by frame with one global palette.</summary>
    internal sealed class GifWriter
    {
        private readonly BinaryWriter _w;
        private readonly int _width, _height, _paletteCount;
        private readonly bool _dither;
        private readonly Quantizer _quantizer;

        public GifWriter(Stream output, int width, int height, List<(byte R, byte G, byte B)> palette, bool dither)
        {
            _w = new BinaryWriter(output, System.Text.Encoding.ASCII, leaveOpen: true);
            _width = width;
            _height = height;
            _paletteCount = palette.Count;
            _dither = dither;
            _quantizer = new Quantizer(palette);
            WriteHeader(_w, width, height, palette);
            WriteLoopExtension(_w);
        }

        public void AddFrame(byte[] rgb, int delayCs)
        {
            var indices = _dither ? _quantizer.MapDithered(rgb, _width, _height) : _quantizer.MapNearest(rgb);
            WriteFrame(_w, _width, _height, indices, delayCs, _paletteCount);
        }

        public void Finish()
        {
            _w.Write((byte)0x3B); // trailer
            _w.Flush();
        }
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

        var data = canvas.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
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
                        rgb[dst++] = row[x * 4 + 2]; // BGRA in memory
                        rgb[dst++] = row[x * 4 + 1];
                        rgb[dst++] = row[x * 4 + 0];
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
        w.Write("GIF89a"u8.ToArray());
        w.Write((ushort)width);
        w.Write((ushort)height);
        int gctSize = PaletteSizeExponent(palette.Count); // 2^(n+1) entries
        w.Write((byte)(0x80 | (0x7 << 4) | gctSize));      // global table | color resolution | size
        w.Write((byte)0);  // background color index
        w.Write((byte)0);  // pixel aspect ratio
        WriteColorTable(w, palette, gctSize);
    }

    private static void WriteLoopExtension(BinaryWriter w)
    {
        w.Write((byte)0x21);
        w.Write((byte)0xFF);
        w.Write((byte)11);
        w.Write("NETSCAPE2.0"u8.ToArray());
        w.Write((byte)3);
        w.Write((byte)1);
        w.Write((ushort)0); // loop forever
        w.Write((byte)0);
    }

    private static void WriteFrame(BinaryWriter w, int width, int height, byte[] indices, int delayCs, int paletteCount)
    {
        // Graphic Control Extension (animation delay).
        w.Write((byte)0x21);
        w.Write((byte)0xF9);
        w.Write((byte)4);
        w.Write((byte)0x00);   // no transparency, disposal = 0
        w.Write((ushort)delayCs);
        w.Write((byte)0);
        w.Write((byte)0);

        // Image descriptor.
        w.Write((byte)0x2C);
        w.Write((ushort)0);
        w.Write((ushort)0);
        w.Write((ushort)width);
        w.Write((ushort)height);
        w.Write((byte)0);      // no local color table

        int minCodeSize = Math.Max(2, PaletteSizeExponent(paletteCount) + 1);
        w.Write((byte)minCodeSize);
        var blocks = new SubBlockWriter(w);
        LzwEncoder.Encode(indices, minCodeSize, blocks.Add, blocks.Flush);
        w.Write((byte)0);      // block terminator
    }

    private static void WriteColorTable(BinaryWriter w, List<(byte R, byte G, byte B)> palette, int sizeExponent)
    {
        int entries = 1 << (sizeExponent + 1);
        for (int i = 0; i < entries; i++)
        {
            var c = i < palette.Count ? palette[i] : ((byte)0, (byte)0, (byte)0);
            w.Write(c.Item1);
            w.Write(c.Item2);
            w.Write(c.Item3);
        }
    }

    /// <summary>Smallest n where 2^(n+1) >= count, clamped to GIF's 0..7 range.</summary>
    private static int PaletteSizeExponent(int count)
    {
        int n = 0;
        while ((1 << (n + 1)) < count && n < 7) n++;
        return n;
    }

    /// <summary>Buffers LZW bytes into GIF's 255-byte sub-blocks.</summary>
    private sealed class SubBlockWriter(BinaryWriter w)
    {
        private readonly byte[] _block = new byte[255];
        private int _count;

        public void Add(byte b)
        {
            _block[_count++] = b;
            if (_count == 255) Flush();
        }

        public void Flush()
        {
            if (_count == 0) return;
            w.Write((byte)_count);
            w.Write(_block, 0, _count);
            _count = 0;
        }
    }

    // ─── LZW (GIF variant) ────────────────────────────────────────────────────

    internal static class LzwEncoder
    {
        // Dictionary keyed by (prefix code << 8 | next index): no string allocations per pixel.
        internal static void Encode(byte[] indices, int minCodeSize, Action<byte> emit, Action flush)
        {
            int clearCode = 1 << minCodeSize;
            int eoiCode = clearCode + 1;
            int codeSize = minCodeSize + 1;
            int nextCode = eoiCode + 1;
            var table = new Dictionary<int, int>(4096);
            int buffer = 0, bits = 0;

            void Write(int code)
            {
                buffer |= code << bits;
                bits += codeSize;
                while (bits >= 8)
                {
                    emit((byte)buffer);
                    buffer >>= 8;
                    bits -= 8;
                }
            }

            Write(clearCode);
            if (indices.Length > 0)
            {
                int prefix = indices[0];
                for (int i = 1; i < indices.Length; i++)
                {
                    int c = indices[i];
                    int key = (prefix << 8) | c;
                    if (table.TryGetValue(key, out int code))
                    {
                        prefix = code;
                        continue;
                    }
                    Write(prefix);
                    table[key] = nextCode++;
                    if (nextCode > (1 << codeSize) && codeSize < 12) codeSize++;
                    if (nextCode > 4095)
                    {
                        Write(clearCode);
                        table.Clear();
                        codeSize = minCodeSize + 1;
                        nextCode = eoiCode + 1;
                    }
                    prefix = c;
                }
                Write(prefix);
            }
            Write(eoiCode);
            if (bits > 0) emit((byte)buffer);
            flush();
        }
    }

    // ─── Palette: median cut ──────────────────────────────────────────────────

    private static class MedianCut
    {
        public static List<(byte R, byte G, byte B)> BuildPalette(List<byte[]> frames, int maxColors)
        {
            // Subsample pixels across the sampled frames so the cut stays fast.
            long totalPixels = 0;
            foreach (var f in frames) totalPixels += f.Length / 3;
            int stride = (int)Math.Max(1, totalPixels / 40_000);

            var samples = new List<(byte R, byte G, byte B)>();
            long counter = 0;
            foreach (var f in frames)
                for (int i = 0; i + 2 < f.Length; i += 3)
                    if (counter++ % stride == 0) samples.Add((f[i], f[i + 1], f[i + 2]));
            if (samples.Count == 0) return new() { (0, 0, 0) };

            var boxes = new List<Box> { new(samples, 0, samples.Count) };
            while (boxes.Count < maxColors)
            {
                int best = -1, bestRange = -1;
                for (int i = 0; i < boxes.Count; i++)
                {
                    if (boxes[i].Count < 2) continue;
                    int range = boxes[i].LongestAxisRange();
                    if (range > bestRange) { bestRange = range; best = i; }
                }
                if (best < 0 || bestRange == 0) break;
                var (a, b) = boxes[best].Split();
                boxes[best] = a;
                boxes.Add(b);
            }

            var palette = new List<(byte R, byte G, byte B)>(boxes.Count);
            foreach (var box in boxes) palette.Add(box.Average());
            return palette;
        }

        private sealed class Box(List<(byte R, byte G, byte B)> all, int start, int count)
        {
            public int Count => count;

            private (int axis, int range) WidestAxis()
            {
                byte rMin = 255, rMax = 0, gMin = 255, gMax = 0, bMin = 255, bMax = 0;
                for (int i = start; i < start + count; i++)
                {
                    var (r, g, b) = all[i];
                    if (r < rMin) rMin = r; if (r > rMax) rMax = r;
                    if (g < gMin) gMin = g; if (g > gMax) gMax = g;
                    if (b < bMin) bMin = b; if (b > bMax) bMax = b;
                }
                int dr = rMax - rMin, dg = gMax - gMin, db = bMax - bMin;
                if (dr >= dg && dr >= db) return (0, dr);
                return dg >= db ? (1, dg) : (2, db);
            }

            public int LongestAxisRange() => WidestAxis().range;

            public (Box, Box) Split()
            {
                int axis = WidestAxis().axis;
                all.Sort(start, count, Comparer<(byte R, byte G, byte B)>.Create((p, q) => axis switch
                {
                    0 => p.R.CompareTo(q.R),
                    1 => p.G.CompareTo(q.G),
                    _ => p.B.CompareTo(q.B),
                }));
                int mid = count / 2;
                return (new Box(all, start, mid), new Box(all, start + mid, count - mid));
            }

            public (byte R, byte G, byte B) Average()
            {
                long r = 0, g = 0, b = 0;
                for (int i = start; i < start + count; i++) { r += all[i].R; g += all[i].G; b += all[i].B; }
                int n = Math.Max(1, count);
                return ((byte)(r / n), (byte)(g / n), (byte)(b / n));
            }
        }
    }

    // ─── Quantize frame → palette indices ─────────────────────────────────────

    private sealed class Quantizer(List<(byte R, byte G, byte B)> palette)
    {
        // Palette is fixed for the whole GIF, so nearest-colour lookups are shared across frames.
        private readonly Dictionary<int, byte> _cache = new();

        public byte[] MapNearest(byte[] rgb)
        {
            var indices = new byte[rgb.Length / 3];
            for (int i = 0; i < indices.Length; i++)
                indices[i] = Nearest(rgb[i * 3], rgb[i * 3 + 1], rgb[i * 3 + 2]);
            return indices;
        }

        public byte[] MapDithered(byte[] rgb, int width, int height)
        {
            // Floyd–Steinberg on a float working copy of the RGB plane.
            var work = new float[rgb.Length];
            for (int i = 0; i < rgb.Length; i++) work[i] = rgb[i];
            var indices = new byte[width * height];
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int p = (y * width + x) * 3;
                    int r = Clamp(work[p]), g = Clamp(work[p + 1]), b = Clamp(work[p + 2]);
                    byte idx = Nearest(r, g, b);
                    indices[y * width + x] = idx;
                    var pal = palette[idx];
                    float er = r - pal.R, eg = g - pal.G, eb = b - pal.B;
                    Spread(work, width, height, x + 1, y, er, eg, eb, 7f / 16f);
                    Spread(work, width, height, x - 1, y + 1, er, eg, eb, 3f / 16f);
                    Spread(work, width, height, x, y + 1, er, eg, eb, 5f / 16f);
                    Spread(work, width, height, x + 1, y + 1, er, eg, eb, 1f / 16f);
                }
            }
            return indices;
        }

        private static void Spread(float[] work, int width, int height, int x, int y, float er, float eg, float eb, float f)
        {
            if (x < 0 || x >= width || y < 0 || y >= height) return;
            int p = (y * width + x) * 3;
            work[p] += er * f;
            work[p + 1] += eg * f;
            work[p + 2] += eb * f;
        }

        private byte Nearest(int r, int g, int b)
        {
            int key = (r << 16) | (g << 8) | b;
            if (_cache.TryGetValue(key, out var hit)) return hit;
            int best = 0, bestDist = int.MaxValue;
            for (int i = 0; i < palette.Count; i++)
            {
                int dr = r - palette[i].R, dg = g - palette[i].G, db = b - palette[i].B;
                int dist = dr * dr + dg * dg + db * db;
                if (dist < bestDist) { bestDist = dist; best = i; if (dist == 0) break; }
            }
            if (_cache.Count > 1_000_000) _cache.Clear(); // dithering can produce millions of distinct colours
            _cache[key] = (byte)best;
            return (byte)best;
        }

        private static int Clamp(float v) => v < 0 ? 0 : v > 255 ? 255 : (int)(v + 0.5f);
    }
}
