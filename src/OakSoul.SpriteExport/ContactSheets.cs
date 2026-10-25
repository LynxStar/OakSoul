using OakSoul.Wz;

namespace OakSoul.SpriteExport;

/// <summary>Contact sheets for checking the decoder by eye: sprites on a checkerboard, labelled with their ids.</summary>
internal static class ContactSheets
{
    private const uint Background = 0xFF202020;
    private const uint CheckA = 0xFFC8C8C8, CheckB = 0xFF9A9A9A;
    private const uint Label = 0xFFFFFFFF;
    private const int Pad = 6, LabelScale = 2, LabelHeight = 5 * LabelScale + 6;

    /// <summary>One cell per mob in the given (id) order: its preview frame bottom-centred, the id and animation below.</summary>
    public static void Mobs(IReadOnlyList<(string Id, string Anim, int W, int H, byte[] Bgra)> mobs, string path)
    {
        const int maxCell = 160, columns = 10;
        int cellW = Math.Clamp(mobs.Max(m => m.W), 64, maxCell);
        int cellH = Math.Clamp(mobs.Max(m => m.H), 64, maxCell);
        cellW = Math.Max(cellW, Raster.TextWidth("0000000 STAND", LabelScale));
        int rows = (mobs.Count + columns - 1) / columns;
        var sheet = new Raster(Pad + columns * (cellW + Pad), Pad + rows * (cellH + LabelHeight + Pad));
        sheet.Fill(0, 0, sheet.Width, sheet.Height, Background);

        for (int i = 0; i < mobs.Count; i++)
        {
            var m = mobs[i];
            int x0 = Pad + (i % columns) * (cellW + Pad);
            int y0 = Pad + (i / columns) * (cellH + LabelHeight + Pad);
            sheet.Checker(x0, y0, cellW, cellH, CheckA, CheckB);
            double scale = Math.Min(1.0, Math.Min((double)cellW / m.W, (double)cellH / m.H));
            int dw = (int)Math.Round(m.W * scale), dh = (int)Math.Round(m.H * scale);
            sheet.Blit(m.Bgra, m.W, m.H, x0 + (cellW - dw) / 2, y0 + cellH - dh, scale);
            sheet.Text($"{m.Id} {m.Anim}", x0, y0 + cellH + 3, Label, LabelScale);
        }
        PngWriter.Save(path, sheet.Width, sheet.Height, sheet.Pixels);
    }

    /// <summary>One row per meso bag id: the id, then each iconRaw frame at 2x.</summary>
    public static void Drops(IReadOnlyList<(string Id, IReadOnlyList<(int W, int H, byte[] Bgra)> Frames)> rows, string path)
    {
        const int scale = 2;
        int cell = Math.Max(rows.SelectMany(r => r.Frames).Max(f => Math.Max(f.W, f.H)) * scale, 32);
        int labelW = Raster.TextWidth("00000000", LabelScale) + Pad;
        int columns = rows.Max(r => r.Frames.Count);
        var sheet = new Raster(Pad + labelW + columns * (cell + Pad), Pad + rows.Count * (cell + Pad));
        sheet.Fill(0, 0, sheet.Width, sheet.Height, Background);

        for (int r = 0; r < rows.Count; r++)
        {
            int y0 = Pad + r * (cell + Pad);
            sheet.Text(rows[r].Id, Pad, y0 + (cell - 5 * LabelScale) / 2, Label, LabelScale);
            for (int c = 0; c < rows[r].Frames.Count; c++)
            {
                var f = rows[r].Frames[c];
                int x0 = Pad + labelW + c * (cell + Pad);
                sheet.Checker(x0, y0, cell, cell, CheckA, CheckB);
                sheet.Blit(f.Bgra, f.W, f.H, x0 + (cell - f.W * scale) / 2, y0 + (cell - f.H * scale) / 2, scale);
            }
        }
        PngWriter.Save(path, sheet.Width, sheet.Height, sheet.Pixels);
    }
}
