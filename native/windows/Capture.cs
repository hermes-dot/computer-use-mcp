// 截图层：抓取目标窗口的位图并落盘为 JPEG。
//
// 策略（按优先级）：
//   1. 目标窗口已在前台：直接抓屏 + 按窗口矩形裁剪 —— 最快，且内容一定是最新的合成结果；
//   2. 目标窗口不在前台：先试 PrintWindow(PW_RENDERFULLCONTENT)，它能在不打扰用户的前提下
//      拿到部分（尤其是原生 GDI/DWM 应用）窗口内容；
//   3. PrintWindow 失败或明显是空白/全黑：激活窗口后回到策略 1。
//
// 关于坐标：截图默认按最长边缩放到 MaxDimension 以内，返回的 scale 表示
// "图像像素 / 物理像素"。模型基于图像给出的坐标必须除以 scale 才能变成物理坐标，
// 这一步在 helper 内统一处理，避免上层算错。
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

namespace CuHelper
{
    internal sealed class ShotRecord
    {
        public string Id = "";
        public long Hwnd;
        public int ImageWidth;
        public int ImageHeight;
        public int SourceWidth;
        public int SourceHeight;
        public double Scale = 1.0;
        public int WindowLeft;
        public int WindowTop;
        public string Path = "";
        public string Method = "";
        public DateTime CreatedUtc = DateTime.UtcNow;
    }

    internal static class Capture
    {
        // 传给模型前的最长边上限：再大只会增加传输与 token 成本，对可读性没有帮助。
        private const int MaxDimension = 1600;
        private const long JpegQuality = 78L;

        private static readonly object Gate = new object();
        private static readonly Dictionary<long, ShotRecord> LastShots = new Dictionary<long, ShotRecord>();
        private static int _counter;

        public static ShotRecord LastShot(long hwnd)
        {
            lock (Gate)
            {
                ShotRecord record;
                return LastShots.TryGetValue(hwnd, out record) ? record : null;
            }
        }

        /// <summary>抓取窗口截图并写入临时目录。</summary>
        public static ShotRecord CaptureWindow(WindowInfo window, bool allowActivate)
        {
            IntPtr hwnd = new IntPtr(window.Hwnd);
            if (!NativeMethods.IsWindow(hwnd))
                throw new HelperException("window_gone", $"Window {window.Hwnd} no longer exists.");

            if (window.IsMinimized && allowActivate) Win32.ActivateWindow(hwnd);

            RECT bounds = Win32.WindowBounds(hwnd);
            if (bounds.IsEmpty)
                throw new HelperException("window_not_capturable", $"Window {window.Hwnd} has an empty bounding box.");

            bool isForeground = NativeMethods.GetForegroundWindow() == hwnd;
            Bitmap bitmap = null;
            string method;

            if (isForeground)
            {
                bitmap = GrabScreen(bounds);
                method = "screen";
            }
            else
            {
                bitmap = TryPrintWindow(hwnd, bounds);
                method = "printwindow";
                if (bitmap == null && allowActivate)
                {
                    // 被遮挡且 PrintWindow 无效：把窗口带到前台再抓屏。
                    Win32.ActivateWindow(hwnd);
                    System.Threading.Thread.Sleep(120);
                    bounds = Win32.WindowBounds(hwnd);
                    bitmap = GrabScreen(bounds);
                    method = "screen-after-activate";
                }
            }

            if (bitmap == null)
                throw new HelperException("capture_failed",
                    $"Could not capture window {window.Hwnd} (tried {(isForeground ? "screen" : "printwindow")}). " +
                    "Activate the window first, or check whether it is minimized.");

            try
            {
                return Persist(bitmap, window, bounds, method);
            }
            finally
            {
                bitmap.Dispose();
            }
        }

        /// <summary>抓取整个虚拟桌面（full-desktop 目标用）。</summary>
        public static ShotRecord CaptureDesktop()
        {
            int x = NativeMethods.GetSystemMetrics(76);  // SM_XVIRTUALSCREEN
            int y = NativeMethods.GetSystemMetrics(77);  // SM_YVIRTUALSCREEN
            int w = NativeMethods.GetSystemMetrics(78);  // SM_CXVIRTUALSCREEN
            int h = NativeMethods.GetSystemMetrics(79);  // SM_CYVIRTUALSCREEN
            var bounds = new RECT { Left = x, Top = y, Right = x + w, Bottom = y + h };
            using (Bitmap bitmap = GrabScreen(bounds))
            {
                if (bitmap == null) throw new HelperException("capture_failed", "Desktop capture failed.");
                var window = new WindowInfo { Hwnd = 0, Title = "Desktop", AppName = "desktop", Rect = bounds };
                return Persist(bitmap, window, bounds, "screen");
            }
        }

        /// <summary>从屏幕 DC 位块传输指定矩形区域。</summary>
        private static Bitmap GrabScreen(RECT bounds)
        {
            if (bounds.IsEmpty) return null;
            IntPtr screenDc = NativeMethods.GetDC(IntPtr.Zero);
            if (screenDc == IntPtr.Zero) return null;
            IntPtr memDc = NativeMethods.CreateCompatibleDC(screenDc);
            IntPtr hBitmap = NativeMethods.CreateCompatibleBitmap(screenDc, bounds.Width, bounds.Height);
            IntPtr old = NativeMethods.SelectObject(memDc, hBitmap);
            try
            {
                bool ok = NativeMethods.BitBlt(memDc, 0, 0, bounds.Width, bounds.Height, screenDc, bounds.Left, bounds.Top,
                    NativeMethods.SRCCOPY);
                if (!ok) return null;
                // FromHbitmap 复制像素，因此随后可以安全释放 GDI 对象。
                using (var raw = Image.FromHbitmap(hBitmap))
                {
                    var copy = new Bitmap(raw.Width, raw.Height, PixelFormat.Format24bppRgb);
                    using (var g = Graphics.FromImage(copy))
                    {
                        g.DrawImageUnscaled(raw, 0, 0);
                    }
                    return copy;
                }
            }
            finally
            {
                NativeMethods.SelectObject(memDc, old);
                NativeMethods.DeleteObject(hBitmap);
                NativeMethods.DeleteDC(memDc);
                NativeMethods.ReleaseDC(IntPtr.Zero, screenDc);
            }
        }

        /// <summary>尝试 PrintWindow；返回 null 表示不可用或结果明显无效。</summary>
        private static Bitmap TryPrintWindow(IntPtr hwnd, RECT bounds)
        {
            IntPtr screenDc = NativeMethods.GetDC(IntPtr.Zero);
            if (screenDc == IntPtr.Zero) return null;
            IntPtr memDc = NativeMethods.CreateCompatibleDC(screenDc);
            IntPtr hBitmap = NativeMethods.CreateCompatibleBitmap(screenDc, bounds.Width, bounds.Height);
            IntPtr old = NativeMethods.SelectObject(memDc, hBitmap);
            try
            {
                if (!NativeMethods.PrintWindow(hwnd, memDc, NativeMethods.PW_RENDERFULLCONTENT)) return null;
                using (var raw = Image.FromHbitmap(hBitmap))
                {
                    var copy = new Bitmap(raw.Width, raw.Height, PixelFormat.Format24bppRgb);
                    using (var g = Graphics.FromImage(copy)) g.DrawImageUnscaled(raw, 0, 0);
                    // 纯色结果通常意味着 PrintWindow 返回了空白（UWP/Chromium 常见），不可信。
                    return LooksBlank(copy) ? null : copy;
                }
            }
            catch (Exception)
            {
                return null;
            }
            finally
            {
                NativeMethods.SelectObject(memDc, old);
                NativeMethods.DeleteObject(hBitmap);
                NativeMethods.DeleteDC(memDc);
                NativeMethods.ReleaseDC(IntPtr.Zero, screenDc);
            }
        }

        /// <summary>采样网格判断是否为单色空图。</summary>
        private static bool LooksBlank(Bitmap bitmap)
        {
            int stepX = Math.Max(1, bitmap.Width / 12);
            int stepY = Math.Max(1, bitmap.Height / 12);
            int first = 0;
            bool hasFirst = false;
            for (int y = stepY / 2; y < bitmap.Height; y += stepY)
            {
                for (int x = stepX / 2; x < bitmap.Width; x += stepX)
                {
                    int argb = bitmap.GetPixel(x, y).ToArgb();
                    if (!hasFirst) { first = argb; hasFirst = true; continue; }
                    if (argb != first) return false;
                }
            }
            return true;
        }

        /// <summary>缩放 + JPEG 编码 + 落盘，并登记 screenshotId。</summary>
        private static ShotRecord Persist(Bitmap source, WindowInfo window, RECT bounds, string method)
        {
            int sourceWidth = source.Width, sourceHeight = source.Height;
            double scale = 1.0;
            int longest = Math.Max(sourceWidth, sourceHeight);
            if (longest > MaxDimension) scale = (double)MaxDimension / longest;

            int targetWidth = Math.Max(1, (int)Math.Round(sourceWidth * scale));
            int targetHeight = Math.Max(1, (int)Math.Round(sourceHeight * scale));

            string directory = HelperPaths.ShotDirectory();
            try
            {
                Directory.CreateDirectory(directory);
            }
            catch (Exception ex)
            {
                throw new HelperException("cache_dir_unwritable",
                    $"Cannot write screenshots to '{directory}': {ex.Message} " +
                    "Set the CU_HELPER_CACHE_DIR environment variable to a writable directory.");
            }

            ShotRecord record;
            lock (Gate)
            {
                _counter++;
                record = new ShotRecord
                {
                    Id = $"shot-{_counter}",
                    Hwnd = window.Hwnd,
                    ImageWidth = targetWidth,
                    ImageHeight = targetHeight,
                    SourceWidth = sourceWidth,
                    SourceHeight = sourceHeight,
                    Scale = scale,
                    WindowLeft = bounds.Left,
                    WindowTop = bounds.Top,
                    Path = Path.Combine(directory, $"shot-{_counter}-{DateTime.UtcNow:HHmmssfff}.jpg"),
                };
            }

            using (var target = new Bitmap(targetWidth, targetHeight, PixelFormat.Format24bppRgb))
            {
                using (var g = Graphics.FromImage(target))
                {
                    g.InterpolationMode = scale < 1.0 ? InterpolationMode.HighQualityBicubic : InterpolationMode.NearestNeighbor;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    g.DrawImage(source, 0, 0, targetWidth, targetHeight);
                }
                ImageCodecInfo codec = FindCodec("image/jpeg");
                if (codec != null)
                {
                    using (var parameters = new EncoderParameters(1))
                    {
                        parameters.Param[0] = new EncoderParameter(Encoder.Quality, JpegQuality);
                        target.Save(record.Path, codec, parameters);
                    }
                }
                else
                {
                    record.Path = Path.ChangeExtension(record.Path, ".png");
                    target.Save(record.Path, ImageFormat.Png);
                }
            }

            lock (Gate) LastShots[window.Hwnd] = record;
            record.Method = method;
            return record;
        }

        private static ImageCodecInfo FindCodec(string mimeType)
        {
            foreach (ImageCodecInfo codec in ImageCodecInfo.GetImageEncoders())
                if (string.Equals(codec.MimeType, mimeType, StringComparison.OrdinalIgnoreCase)) return codec;
            return null;
        }
    }

    internal static class HelperPaths
    {
        /// <summary>
        /// 截图与中断标记的落盘目录。
        /// 默认放在用户本地缓存下；允许用环境变量 CU_HELPER_CACHE_DIR 覆盖，
        /// 因为某些宿主（例如带写入白名单的沙箱）只允许写到指定目录。
        /// </summary>
        public static string Root()
        {
            string configured = Environment.GetEnvironmentVariable("CU_HELPER_CACHE_DIR");
            if (!string.IsNullOrWhiteSpace(configured))
            {
                try { return Path.GetFullPath(configured); }
                catch (Exception) { /* 配置非法时退回默认目录 */ }
            }
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "cu-helper");
        }

        public static string ShotDirectory()
        {
            return Path.Combine(Root(), "shots");
        }
    }
}
