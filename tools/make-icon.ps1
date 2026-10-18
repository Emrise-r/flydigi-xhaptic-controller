# Draws the X-Haptics Guard icon (a shield with a haptic pulse on a rounded tile) and writes
#   assets\app.ico          switch ON: 16, 20, 24, 32, 40, 48, 64 px as 32-bit bitmaps + 256 px as PNG
#   assets\app-off.ico      the same in grey, shown in the tray while the switch is OFF
#   assets\icon-256.png     preview
# The artwork is generated here from simple shapes; it does not use any Flydigi artwork.
# Usage: powershell -ExecutionPolicy Bypass -File tools\make-icon.ps1

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$assets = Join-Path $root 'assets'
New-Item -ItemType Directory -Force $assets | Out-Null

Add-Type -ReferencedAssemblies System.Drawing -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

public static class GuardIcon
{
    // ON: deep indigo -> teal tile; OFF: grey tile. Symbol: white shield, pulse line in the tile colour.
    static readonly Color[] Top = { Color.FromArgb(255, 49, 46, 129), Color.FromArgb(255, 75, 85, 99) };      // #312E81, #4B5563
    static readonly Color[] Bottom = { Color.FromArgb(255, 14, 116, 144), Color.FromArgb(255, 107, 114, 128) }; // #0E7490, #6B7280
    static readonly Color[] Pulse = { Color.FromArgb(255, 20, 90, 140), Color.FromArgb(255, 90, 98, 112) };

    public static Bitmap Draw(int size, bool off)
    {
        int v = off ? 1 : 0;
        var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.Clear(Color.Transparent);
            float s = size / 256f;
            bool small = size <= 24;

            // rounded tile
            using (var tile = RoundedRect(new RectangleF(4 * s, 4 * s, 248 * s, 248 * s), 56 * s))
            using (var fill = new LinearGradientBrush(new PointF(0, 0), new PointF(0, size), Top[v], Bottom[v]))
                g.FillPath(fill, tile);

            // shield
            using (var shield = Shield(s, small))
            using (var white = new SolidBrush(Color.White))
                g.FillPath(white, shield);

            // haptic pulse across the shield
            float w = (small ? 30f : 20f) * s;
            using (var pen = new Pen(Pulse[v], w) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                PointF[] pts = small
                    ? new[] { P(70, 128, s), P(106, 128, s), P(128, 88, s), P(150, 168, s), P(186, 128, s) }
                    : new[] { P(64, 132, s), P(96, 132, s), P(114, 98, s), P(138, 172, s), P(160, 112, s), P(174, 132, s), P(192, 132, s) };
                g.DrawLines(pen, pts);
            }

            // rumble arcs beside the shield (only where there is room)
            if (size >= 48)
                using (var arc = new Pen(Color.FromArgb(200, 255, 255, 255), 10 * s) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                {
                    g.DrawArc(arc, 14 * s, 92 * s, 40 * s, 80 * s, 120, 120);
                    g.DrawArc(arc, 202 * s, 92 * s, 40 * s, 80 * s, -60, 120);
                }
        }
        return bmp;
    }

    static PointF P(float x, float y, float s) { return new PointF(x * s, y * s); }

    static GraphicsPath Shield(float s, bool small)
    {
        float l = (small ? 52 : 60) * s, r = (small ? 204 : 196) * s, top = (small ? 40 : 46) * s, mid = 140 * s, bottom = (small ? 224 : 218) * s, cx = 128 * s;
        var p = new GraphicsPath();
        p.AddBezier(cx, top, cx - 30 * s, top + 16 * s, l + 26 * s, top + 22 * s, l, top + 20 * s);
        p.AddLine(l, top + 20 * s, l, mid - 20 * s);
        p.AddBezier(l, mid - 20 * s, l, mid + 40 * s, cx - 40 * s, bottom - 24 * s, cx, bottom);
        p.AddBezier(cx, bottom, cx + 40 * s, bottom - 24 * s, r, mid + 40 * s, r, mid - 20 * s);
        p.AddLine(r, mid - 20 * s, r, top + 20 * s);
        p.AddBezier(r, top + 20 * s, r - 26 * s, top + 22 * s, cx + 30 * s, top + 16 * s, cx, top);
        p.CloseFigure();
        return p;
    }

    static GraphicsPath RoundedRect(RectangleF r, float radius)
    {
        float d = radius * 2;
        var p = new GraphicsPath();
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    // ICO with 32-bit DIB frames (what NotifyIcon and Explorer read best) and a PNG frame for 256 px.
    public static void WriteIco(string path, int[] sizes, bool off)
    {
        var frames = new List<byte[]>();
        foreach (int size in sizes)
            using (var bmp = Draw(size, off))
                frames.Add(size >= 256 ? Png(bmp) : Dib(bmp));
        using (var w = new BinaryWriter(File.Create(path)))
        {
            w.Write((short)0); w.Write((short)1); w.Write((short)sizes.Length);
            int offset = 6 + 16 * sizes.Length;
            for (int i = 0; i < sizes.Length; i++)
            {
                w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i])); w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
                w.Write((byte)0); w.Write((byte)0); w.Write((short)1); w.Write((short)32);
                w.Write(frames[i].Length); w.Write(offset);
                offset += frames[i].Length;
            }
            foreach (var f in frames) w.Write(f);
        }
    }

    static byte[] Png(Bitmap bmp) { using (var m = new MemoryStream()) { bmp.Save(m, ImageFormat.Png); return m.ToArray(); } }

    static byte[] Dib(Bitmap bmp)
    {
        int n = bmp.Width, maskStride = ((n + 31) / 32) * 4;
        using (var m = new MemoryStream())
        using (var w = new BinaryWriter(m))
        {
            w.Write(40); w.Write(n); w.Write(n * 2); w.Write((short)1); w.Write((short)32);
            w.Write(0); w.Write(n * n * 4 + maskStride * n); w.Write(0); w.Write(0); w.Write(0); w.Write(0);
            for (int y = n - 1; y >= 0; y--)
                for (int x = 0; x < n; x++)
                {
                    Color c = bmp.GetPixel(x, y);
                    w.Write(c.B); w.Write(c.G); w.Write(c.R); w.Write(c.A);
                }
            w.Write(new byte[maskStride * n]);   // AND mask unused: the alpha channel decides
            return m.ToArray();
        }
    }
}
'@

$sizes = @(16, 20, 24, 32, 40, 48, 64, 256)
[GuardIcon]::WriteIco((Join-Path $assets 'app.ico'), $sizes, $false)
[GuardIcon]::WriteIco((Join-Path $assets 'app-off.ico'), $sizes, $true)
$preview = [GuardIcon]::Draw(256, $false); $preview.Save((Join-Path $assets 'icon-256.png'), [System.Drawing.Imaging.ImageFormat]::Png); $preview.Dispose()
Write-Host "written: assets\app.ico, assets\app-off.ico, assets\icon-256.png"
