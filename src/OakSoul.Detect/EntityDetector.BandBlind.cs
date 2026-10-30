using System.Globalization;
using OakSoul.World;

namespace OakSoul.Detect;

/// <summary>
/// DIAGNOSTICS ONLY (OAKSOUL_BAND_BLIND=path): per classified frame, how much of the floor band (the rows
/// <see cref="FootholdSlices.Above"/> above to <see cref="FootholdSlices.Below"/> below every floor foothold's line, placed as
/// <see cref="FootholdSlices.Build"/> places it) the pixel classes leave Ignored or call fog. Reads the classes, the mask and
/// the camera only; changes nothing the detector does. Off (one null check per frame) when the variable is unset.
/// </summary>
public sealed partial class EntityDetector
{
    private static readonly StreamWriter? _bandBlind = OpenBandBlind();
    private static readonly object _bandBlindLock = new();
    private (int X, int Y)? _bandLastCamera;
    private MapData? _bandMap;
    private (int Wx, Foothold Fh)[] _bandColumns = [];
    private int[] _bandStamp = [];
    private int _bandGeneration;

    private static StreamWriter? OpenBandBlind()
    {
        if (Environment.GetEnvironmentVariable("OAKSOUL_BAND_BLIND") is not { Length: > 0 } path) return null;
        var w = new StreamWriter(path, append: false) { AutoFlush = true };
        w.WriteLine("frame,sequence,map,cam_x,cam_y,moved,dx,dy,band_pixels,band_ignored,band_fog,viewport_pixels,viewport_ignored");
        return w;
    }

    private void CountBandBlind(DetectionContext ctx, int camX, int camY)
    {
        if (!ReferenceEquals(_bandMap, ctx.Map))
        {
            _bandMap = ctx.Map;
            // Every column of every floor, as FootholdSlices.Build walks them (a foothold from its left end to its right end exclusive).
            _bandColumns = FootholdSlices.Floors(ctx.Map.Footholds)
                .SelectMany(floor => floor.SelectMany(fh => Enumerable.Range(fh.MinX, fh.MaxX - fh.MinX).Select(wx => (wx, fh)))).ToArray();
        }
        var vp = _mask.Viewport;
        int w = _mask.Width, h = _mask.Height;
        var bits = _mask.Bits;
        var cls = _fg.Classes;
        if (_bandStamp.Length != w * h) { _bandStamp = new int[w * h]; _bandGeneration = 0; }
        var gen = ++_bandGeneration;
        int band = 0, bandIgnored = 0, bandFog = 0;
        foreach (var (wx, fh) in _bandColumns)
        {
            var x = wx - camX - vp.X;
            if ((uint)x >= (uint)w || fh.YAt(wx) is not { } wy) continue;
            var line = (int)Math.Round(wy) - camY - vp.Y;
            for (int y = Math.Max(0, line - FootholdSlices.Above), y1 = Math.Min(h - 1, line + FootholdSlices.Below); y <= y1; y++)
            {
                var p = y * w + x;
                // A pixel in two floors' bands (overlapping floors) counts once.
                if (bits[p] != 0 || _bandStamp[p] == gen) continue;
                _bandStamp[p] = gen;
                band++;
                var c = (PixelClass)cls[p];
                if (c == PixelClass.Ignored) bandIgnored++;
                else if (c is PixelClass.Fog or PixelClass.BrightFog) bandFog++;
            }
        }
        int vpPixels = 0, vpIgnored = 0;
        for (var p = 0; p < bits.Length; p++)
        {
            if (bits[p] != 0) continue;
            vpPixels++;
            if (cls[p] == (byte)PixelClass.Ignored) vpIgnored++;
        }
        var last = _bandLastCamera;
        _bandLastCamera = (camX, camY);
        string moved = "1", dx = "", dy = "";
        if (last is { } l)
        {
            moved = l.X == camX && l.Y == camY ? "0" : "1";
            dx = (camX - l.X).ToString(CultureInfo.InvariantCulture);
            dy = (camY - l.Y).ToString(CultureInfo.InvariantCulture);
        }
        var row = string.Create(CultureInfo.InvariantCulture,
            $"{ctx.Frame},{ctx.Sequence},{ctx.Map.MapId},{camX},{camY},{moved},{dx},{dy},{band},{bandIgnored},{bandFog},{vpPixels},{vpIgnored}");
        lock (_bandBlindLock) _bandBlind!.WriteLine(row);
    }
}
