using System.Diagnostics;
using System.Runtime.InteropServices;
using OakSoul.Capture;
using OakSoul.World;

namespace OakSoul.Detect;

/// <summary>
/// The own character's skill effects (data/sprites/skill-effect, exported for its jobs: Cold Beam's ice shards, Thunder Bolt, ...)
/// recognised by an exact match of their frames, so that they are labelled effects with the skill's name and the species search
/// and the foothold search under unmatched blobs do not run on them. The opaque pixels of an effect frame are drawn as stored (its
/// translucent inside blends with what is behind and is not compared): on fx-combat-exact's keyframe 346 the shards of Cold Beam's
/// hit/0/1 agree on 495 of 512 compared pixels. A blob is tried only when most of its pixels show colours of the effects'
/// palette; frames are placed by votes of its pixels' colours (each opaque, coloured pixel of a frame names a placement), and the
/// best few placements are scored strictly.
/// </summary>
public sealed partial class EntityDetector
{
    /// <summary>The own skills' effect frames, or null.</summary>
    public SpriteSet? SkillEffects { get; init; }

    /// <summary>A blob is tried as an effect when this share of its foreground pixels show a colour of the effects' coloured opaque pixels.</summary>
    public const double EffectPaletteShare = 0.2;

    /// <summary>An effect frame needs this share of its compared opaque pixels (its edges and anything in front disagree), and this many compared.</summary>
    public const double EffectThreshold = 0.6;
    public const int EffectMinCompared = 40;

    /// <summary>Smallest blob tried as an effect (the shards of Cold Beam are a few hundred pixels each).</summary>
    public const int EffectMinPixels = 120;

    /// <summary>Placements scored per blob, the most voted first.</summary>
    public const int EffectPlacements = 6;

    private HashSet<int>? _effectColours;
    private Dictionary<int, List<(MobSpecies Group, SpriteFrame Frame, int Dx, int Dy)>>? _effectAnchors;

    /// <summary>Effects recognised, blobs tried, and the time spent, since construction.</summary>
    public int EffectsRecognised { get; private set; }
    public int EffectBlobsTried { get; private set; }
    public double EffectMs { get; private set; }

    /// <summary>Anchor pixels per effect frame: its coloured opaque pixels, spread, whose colour is rare in the frame.</summary>
    public const int EffectAnchorsPerFrame = 24;

    /// <summary>Builds the effects' palette and anchor index (once, before the first frame, so no frame pays for it).</summary>
    internal void BuildEffectIndex()
    {
        if (SkillEffects is null || _effectAnchors is not null) return;
        _effectColours = [];
        _effectAnchors = [];
        foreach (var g in SkillEffects!.Groups.Values)
            foreach (var f in g.Frames)
            {
                var counts = f.PixelPacked.GroupBy(c => c).ToDictionary(x => x.Key, x => x.Count());
                // Only coloured pixels name an effect: white and greys are everywhere (steam, tags, outlines).
                foreach (var c in f.PixelPacked) if (Chroma(c) >= 34) _effectColours.Add(c);
                var chosen = new List<(int X, int Y)>();
                for (var i = 0; i < f.PixelX.Length && chosen.Count < EffectAnchorsPerFrame; i++)
                {
                    int b = f.PixelB[i], gg = f.PixelG[i], r = f.PixelR[i];
                    if (Math.Max(b, Math.Max(gg, r)) - Math.Min(b, Math.Min(gg, r)) < 34 || counts[f.PixelPacked[i]] > f.PixelX.Length / 4) continue;
                    if (chosen.Any(p => Math.Abs(p.X - f.PixelX[i]) + Math.Abs(p.Y - f.PixelY[i]) < 4)) continue;
                    chosen.Add((f.PixelX[i], f.PixelY[i]));
                    if (!_effectAnchors.TryGetValue(f.PixelPacked[i], out var list)) _effectAnchors[f.PixelPacked[i]] = list = [];
                    list.Add((g, f, f.PixelX[i], f.PixelY[i]));
                }
            }
    }

    /// <summary>
    /// Recognises the own skill effects among the candidates nothing verified: such a candidate becomes the effect (named by its
    /// skill), the other candidates whose foreground its frame covers are taken into it, and on a temporal frame its pixels leave
    /// every later comparison.
    /// </summary>
    private void RecogniseEffects(List<Candidate> candidates, ReadOnlySpan<byte> frame, int frameWidth)
    {
        if (SkillEffects is null) return;
        var t0 = Stopwatch.GetTimestamp();
        var vp = _mask.Viewport;
        var px = MemoryMarshal.Cast<byte, int>(frame);
        var fgMask = _fg.Mask;
        int w = _fg.Width;
        for (var ci = 0; ci < candidates.Count; ci++)
        {
            var c = candidates[ci];
            if (c.Pixels <= 0 || c.Found != FoundBy.Search || c.Sprite is not null || c.PetSprite is not null || c.Tag is not null || c.Drop is not null || c.Skill is not null) continue;
            var blob = new Blob(c.Box.X - vp.X, c.Box.Y - vp.Y, c.Box.X + c.Box.Width - 1 - vp.X, c.Box.Y + c.Box.Height - 1 - vp.Y, c.Pixels, 0, 0);
            if (blob.X0 < 0 || blob.Y0 < 0 || blob.X1 >= w || blob.Y1 >= _fg.Height || c.Pixels < EffectMinPixels) continue;
            if (PaletteShare(frame, frameWidth, blob, _effectColours!) < EffectPaletteShare) continue;
            EffectBlobsTried++;
            // Votes: every foreground pixel of the blob whose colour anchors a frame names that frame's placement.
            var votes = new Dictionary<(SpriteFrame, int, int), (MobSpecies Group, int Votes)>();
            // Every other pixel of every other row is enough to name the placement.
            for (var y = blob.Y0; y <= blob.Y1; y += 2)
                for (var x = blob.X0 + (y & 1); x <= blob.X1; x += 2)
                {
                    if (fgMask[y * w + x] == 0) continue;
                    int sx = x + vp.X, sy = y + vp.Y;
                    if (!_effectAnchors!.TryGetValue(px[sy * frameWidth + sx] & 0xFFFFFF, out var list)) continue;
                    foreach (var (g, f, dx, dy) in list)
                    {
                        var key = (f, sx - dx, sy - dy);
                        votes[key] = (g, votes.GetValueOrDefault(key).Votes + 1);
                    }
                }
            if (votes.Count == 0) continue;
            SpriteMatch? best = null;
            foreach (var ((f, left, top), (g, n)) in votes.OrderByDescending(v => v.Value.Votes).Take(EffectPlacements))
            {
                if (n < 2) break;
                if (!_matcher.PatchCovers(left, top, left + f.Width - 1, top + f.Height - 1))
                    _matcher.PrepareRegion(frame, frameWidth, _mask, left - 2, top - 2, left + f.Width + 2, top + f.Height + 2);
                if (_matcher.At(g, f, left, top) is { } m && m.Compared >= EffectMinCompared && m.Score >= EffectThreshold && (best is null || m.Score > best.Value.Score)) best = m;
            }
            // The effect must explain the blob: most of its foreground inside the frame's silhouette. A mob with the shards drawn over
            // it is mostly mob, and stays a candidate for the species search.
            if (best is not { } em || Silhouette(em, c.Box) < EffectSilhouetteShare) continue;
            if (!_matcher.PatchCovers(em.Left, em.Top, em.Left + em.Frame.Width - 1, em.Top + em.Frame.Height - 1))
                _matcher.PrepareRegion(frame, frameWidth, _mask, em.Left - 2, em.Top - 2, em.Left + em.Frame.Width + 2, em.Top + em.Frame.Height + 2);
            if (_matcher.Explained is { } explained) _matcher.Explain(em, _fg.Mask, _fg.Width, _fg.Height, vp, clear: false, explained);
            c.Skill = em;
            EffectsRecognised++;
            // The frame's other pieces (Cold Beam's two shards are one frame and two blobs) are this effect.
            var extent = new CaptureRect(em.Left + em.Frame.X0, em.Top + em.Frame.Y0, em.Frame.ExtentWidth, em.Frame.ExtentHeight);
            for (var j = candidates.Count - 1; j >= 0; j--)
            {
                var o = candidates[j];
                if (j == ci || o.Pixels <= 0 || o.Found != FoundBy.Search || o.Sprite is not null || o.PetSprite is not null || o.Tag is not null || o.Drop is not null || o.Skill is not null) continue;
                if (Covers(em, o.Box) < 0.6 || Silhouette(em, o.Box) < EffectSilhouetteShare) continue;
                c.Box = Union(c.Box, o.Box);
                c.Pixels += o.Pixels;
                candidates.RemoveAt(j);
                if (j < ci) ci--;
            }
        }
        EffectMs += Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
    }

    /// <summary>Share of a blob's foreground the effect's frame silhouette must hold for the blob to be the effect.</summary>
    public const double EffectSilhouetteShare = 0.6;

    /// <summary>The share of the foreground pixels in a box that lie inside a match's visible silhouette.</summary>
    private double Silhouette(SpriteMatch m, CaptureRect box)
    {
        var vp = _mask.Viewport;
        var mask = _fg.Mask;
        int w = _fg.Width, h = _fg.Height, total = 0, inside = 0;
        var f = m.Frame;
        for (var y = box.Y; y < box.Y + box.Height; y++)
        {
            var vy = y - vp.Y;
            if ((uint)vy >= (uint)h) continue;
            for (var x = box.X; x < box.X + box.Width; x++)
            {
                var vx = x - vp.X;
                if ((uint)vx >= (uint)w || mask[vy * w + vx] == 0) continue;
                total++;
                int tx = x - m.Left, ty = y - m.Top;
                if ((uint)tx < (uint)f.Width && (uint)ty < (uint)f.Height && f.Bgra[(ty * f.Width + tx) * 4 + 3] >= SpriteFrame.VisibleAlpha) inside++;
            }
        }
        return total == 0 ? 0 : (double)inside / total;
    }

    private static int OwnEffectsChroma(int c) => Chroma(c);

    private static int Chroma(int c)
    {
        int b = c & 0xFF, g = c >> 8 & 0xFF, r = c >> 16 & 0xFF;
        return Math.Max(b, Math.Max(g, r)) - Math.Min(b, Math.Min(g, r));
    }

    private static CaptureRect Union(CaptureRect a, CaptureRect b)
    {
        int x0 = Math.Min(a.X, b.X), y0 = Math.Min(a.Y, b.Y), x1 = Math.Max(a.X + a.Width, b.X + b.Width), y1 = Math.Max(a.Y + a.Height, b.Y + b.Height);
        return new CaptureRect(x0, y0, x1 - x0, y1 - y0);
    }
}
