using OakSoul.Capture;
using OakSoul.World;

namespace OakSoul.Detect;

/// <summary>
/// Meso coins and the drops of a kill. A coin on the ground spins through its frames (the client's <c>iconRaw</c> 0 to 3 of
/// 0900.img, face-on to edge-on), all drawn at one anchor (each frame's own origin), so a meso track follows its animation:
/// every frame of its coin is tried at the anchor, the next one first, and verified exactly; edge-on (5 columns) or partly behind
/// the own character it is still the same coin. A kill's drops pop out of the dying mob and fly in an arc before landing, so
/// small candidates near a recent death are tried as drops wherever they are, not only on a surface.
/// </summary>
public sealed partial class EntityDetector
{
    /// <summary>The meso coins' spinning frames with their origins (data/sprites/item/meso), or null.</summary>
    public SpriteSet? Coins { get; init; }

    /// <summary>A coin's frame at its anchor needs this share of its pixels (as an icon does), a quarter of them comparable (the own character may stand in front).</summary>
    public const double CoinMinComparable = 0.25;

    /// <summary>Window around the anchor: coins bob a pixel or two as they settle.</summary>
    public const int CoinSlackX = 1, CoinSlackY = 3;

    /// <summary>Meso tracks followed by their animation, since construction.</summary>
    public int CoinsFollowed { get; private set; }
    public int CoinsLost { get; private set; }

    /// <summary>Drops matched in the air near a recent kill, and small blobs touching the own character kept because an icon matched them.</summary>
    public int DropsInFlight { get; private set; }
    public int DropsBesideOwn { get; private set; }

    /// <summary>Deaths seen in the last seconds (world position of the dying mob's feet, its body's top, and when), for the drops they leave.</summary>
    private readonly List<(PointD Feet, double Top, double Time)> _deaths = [];
    private readonly HashSet<int> _dyingSeen = [];

    /// <summary>A kill's drops are looked for this long after the death was first seen, this far sideways and up from the mob's feet (world units).</summary>
    public const double DeathDropSeconds = 3, DeathDropSideways = 220, DeathDropUp = 260, DeathDropDown = 40;

    /// <summary>
    /// Follows every live meso track's coin at its anchor through its animation; a coin found leaves the foreground (on a temporal
    /// frame every later comparison too) and becomes a candidate bound to its track.
    /// </summary>
    private List<Candidate> FollowCoins(ReadOnlySpan<byte> frame, int frameWidth, DetectionContext ctx, int camX, int camY, bool clear)
    {
        var result = new List<Candidate>();
        if (Coins is null || _drops is null) return result;
        var vp = _mask.Viewport;
        foreach (var coin in _tracker.LiveCoins(ctx.Time, CoinMemorySeconds))
        {
            if (!Coins.Groups.TryGetValue(coin.Last.ItemId, out var group) || coin.Last.AnchorWorld is not { } anchor) continue;
            int ax = (int)Math.Round(anchor.X - camX), ay = (int)Math.Round(anchor.Y - camY);
            _matcher.PrepareRegion(frame, frameWidth, _mask, ax - 48, ay - 48, ax + 48, ay + 12);
            var frames = group.Frames.OrderBy(f => (f.Index - coin.Last.IconFrame - 1 + 64) % 64).ToList();
            SpriteMatch? best = null;
            var allow = _matcher.AllowFreeze;
            _matcher.AllowFreeze = false;
            foreach (var f in frames)
            {
                if (_matcher.Best(new CaptureRect(ax, ay, 1, 1), new PointD(ax, ay), group, f, DropMatcher.Threshold, CoinSlackX, CoinSlackY, minComparable: CoinMinComparable) is { } m
                    && m.Score >= DropMatcher.Threshold && (best is null || m.Score > best.Value.Score)) best = m;
                if (best is { } b && b.Matches == b.Compared) break;
            }
            _matcher.AllowFreeze = allow;
            if (best is not { } bm) { CoinsLost++; continue; }
            var (pixels, extent) = _matcher.Explain(bm, _fg.Mask, _fg.Width, _fg.Height, vp, clear: true, clear ? _explained : null);
            var box = extent ?? new CaptureRect(bm.Left + bm.Frame.X0, bm.Top + bm.Frame.Y0, bm.Frame.ExtentWidth, bm.Frame.ExtentHeight);
            var c = MakeCandidate(ctx, camX, camY, box, new PointD(bm.Feet.X, box.Y + box.Height), Math.Max(1, pixels), null, false, false, null, _ownWorld);
            c.Drop = new DropMatch(coin.Last.ItemId, coin.Last.Category, bm.Frame.Index, bm.Left, bm.Top, bm.Matches, bm.Compared)
            {
                Anchor = bm.Feet, AnchorWorld = new PointD(bm.Feet.X + camX, bm.Feet.Y + camY),
            };
            c.Found = FoundBy.Prediction;
            c.PredictedTrack = coin.TrackId;
            result.Add(c);
            CoinsFollowed++;
        }
        return result;
    }

    /// <summary>A small piece of foreground is tried as a coin this far either side of its centre, the anchor on the foothold under it.</summary>
    public const int CoinPieceSlackX = 12;

    /// <summary>The share of a blob's foreground pixels that are light and warm, as a coin seen through steam is.</summary>
    private double SteamedWarmShare(ReadOnlySpan<byte> frame, int frameWidth, Blob b)
    {
        var vp = _mask.Viewport;
        var mask = _fg.Mask;
        int w = _fg.Width, n = 0, hit = 0;
        for (var y = b.Y0; y <= b.Y1; y++)
            for (var x = b.X0; x <= b.X1; x++)
            {
                if ((uint)x >= (uint)w || (uint)y >= (uint)_fg.Height || mask[y * w + x] == 0) continue;
                n++;
                var o = ((y + vp.Y) * frameWidth + x + vp.X) * 4;
                int bl = frame[o], g = frame[o + 1], r = frame[o + 2];
                if (Math.Min(bl, Math.Min(g, r)) >= 0x80 && r >= bl + SpriteMatcher.FogMinScreenChroma && g >= bl) hit++;
            }
        return n == 0 ? 0 : (double)hit / n;
    }

    /// <summary>A coin lying on a foothold has its anchor a few rows below the line (2 to 4 measured on fx-combat-exact): the search is centred there.</summary>
    public const int CoinAnchorBelow = 3;

    /// <summary>A piece is tried as a coin when this share of its foreground shows colours of the coins' frames.</summary>
    public const double CoinPieceColours = 0.3;

    private HashSet<int>? _coinColours;

    /// <summary>A coin not found at its anchor for this long is gone (looted) and is not followed any more.</summary>
    public const double CoinMemorySeconds = 0.6;

    /// <summary>Coins found from a piece of them on a foothold, since construction.</summary>
    public int CoinPieces { get; private set; }

    /// <summary>
    /// A small candidate on a surface that no icon fits (a piece of a coin: in steam most of it is whitened background and only
    /// slices of it differ, as the coin under the ladder on fx-combat-exact's frames 491 to 506, 12x6) is tried as the small coins'
    /// frames with their anchor on the foothold under it, through steam as the matcher allows.
    /// </summary>
    private DropMatch? CoinOnFoothold(Candidate c, ReadOnlySpan<byte> frame, int frameWidth, DetectionContext ctx, int camX, int camY)
    {
        if (Coins is null || c.Pixels < 24 || c.Box.Width > EntityTracker.DropMaxSize || c.Box.Height > EntityTracker.DropMaxSize) return null;
        // Only a piece showing the coins' colours (bronze, silver, gold) is worth the search.
        _coinColours ??= Coins.Groups.Values.SelectMany(g => g.Frames).SelectMany(f => f.PixelPacked).Where(c => Chroma(c) >= 34).ToHashSet();
        var vp0 = _mask.Viewport;
        var piece = new Blob(c.Box.X - vp0.X, c.Box.Y - vp0.Y, c.Box.X + c.Box.Width - 1 - vp0.X, c.Box.Y + c.Box.Height - 1 - vp0.Y, c.Pixels, 0, 0);
        // ... or one in steam: whitened, but warm (the coins' red stays above their blue through a light layer).
        if (PaletteShare(frame, frameWidth, piece, _coinColours) < CoinPieceColours && SteamedWarmShare(frame, frameWidth, piece) < CoinPieceColours) return null;
        var cx = c.Box.X + c.Box.Width / 2.0;
        if (ctx.Footholds.Nearest(new PointD(camX + cx, camY + c.Box.Y + c.Box.Height), 12) is not { } hit) return null;
        int ax = (int)Math.Round(cx), ay = (int)Math.Round(hit.SurfaceY - camY) + CoinAnchorBelow;
        _matcher.PrepareRegion(frame, frameWidth, _mask, ax - 40, ay - 48, ax + 40, ay + 8);
        var allow = _matcher.AllowFreeze;
        _matcher.AllowFreeze = false;
        SpriteMatch? best = null;
        foreach (var g in SmallCoins())
            foreach (var f in g.Frames)
                if (_matcher.Best(new CaptureRect(ax, ay, 1, 1), new PointD(ax, ay), g, f, DropMatcher.Threshold, CoinPieceSlackX, CoinSlackY, minComparable: CoinMinComparable) is { } m
                    && m.Score >= DropMatcher.Threshold && (best is null || m.Score > best.Value.Score)) best = m;
        _matcher.AllowFreeze = allow;
        if (best is not { } bm) return null;
        CoinPieces++;
        c.Box = new CaptureRect(bm.Left + bm.Frame.X0, bm.Top + bm.Frame.Y0, bm.Frame.ExtentWidth, bm.Frame.ExtentHeight);
        return new DropMatch(bm.Species.Name, "meso", bm.Frame.Index, bm.Left, bm.Top, bm.Matches, bm.Compared)
        {
            Anchor = bm.Feet, AnchorWorld = new PointD(bm.Feet.X + camX, bm.Feet.Y + camY),
        };
    }

    private List<MobSpecies>? _smallCoins;

    /// <summary>The coins of the small amounts (their frames drop-sized): the ones a map's mobs drop.</summary>
    private List<MobSpecies> SmallCoins() =>
        _smallCoins ??= Coins!.Groups.Values.Where(g => g.Frames.All(f => f.ExtentWidth <= EntityTracker.DropMaxSize && f.ExtentHeight <= EntityTracker.DropMaxSize)).ToList();

    /// <summary>A meso match's anchor, from the coin's frame of that index (its client origin).</summary>
    private DropMatch WithAnchor(DropMatch d, int camX, int camY)
    {
        if (!d.IsMeso || d.Anchor is not null || Coins is null || !Coins.Groups.TryGetValue(d.ItemId, out var group)) return d;
        var f = group.Frames.FirstOrDefault(f => f.Index == d.IconFrame);
        return f is null ? d : d with { Anchor = new PointD(d.Left + f.OriginX, d.Top + f.OriginY), AnchorWorld = new PointD(d.Left + f.OriginX + camX, d.Top + f.OriginY + camY) };
    }

    /// <summary>Whether a candidate stands where a recent kill's drops fly (see <see cref="DeathDropSeconds"/>).</summary>
    private bool NearDeath(Candidate c, double time) =>
        _deaths.Any(d => time - d.Time <= DeathDropSeconds && Math.Abs(c.World.X - d.Feet.X) <= DeathDropSideways
            && c.World.Y >= d.Feet.Y - DeathDropUp && c.World.Y <= d.Feet.Y + DeathDropDown);

    /// <summary>Remembers the deaths of this frame (a track dying for the first time) and forgets old ones.</summary>
    private void RememberDeaths(List<DetectedEntity> entities, double time)
    {
        foreach (var e in entities)
            if (e.Kind == EntityKind.Mob && e.State == MobState.Dying && _dyingSeen.Add(e.TrackId))
                _deaths.Add((e.World, e.DisplayBox.Y, time));
        _deaths.RemoveAll(d => time - d.Time > DeathDropSeconds);
    }
}
