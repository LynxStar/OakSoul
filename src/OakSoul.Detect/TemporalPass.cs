using System.Diagnostics;
using OakSoul.Capture;
using OakSoul.World;

namespace OakSoul.Detect;

/// <summary>A live track's prediction verified in a frame: the match, how it was found, and the foreground it explains.</summary>
/// <param name="Pixels">Foreground pixels the match explains (its agreeing opaque pixels and its edge), 0 over background the mosaic cannot vouch for.</param>
/// <param name="Box">Screen extent of those pixels, or the frame's visible extent when there are none.</param>
/// <param name="Precise">Predicted pose by pose from the previous frame (a video), rather than searched along a stretch of the foothold (keyframes, or a track missed for a few frames).</param>
public sealed record Prediction(int TrackId, SpriteMatch Match, FoundBy Found, int Pixels, CaptureRect Box, bool Precise);

/// <summary>
/// The temporal pass: most of a frame is the previous frame moved on by a few pixels and a pose, so each live identified
/// mob track predicts where it stands now and in what pose, the strict matcher verifies that prediction, plausible
/// transitions are tried when it fails, and the full search runs on what the verified predictions do not explain.
/// </summary>
public sealed partial class EntityDetector
{
    /// <summary>
    /// A track matched within this many seconds is predicted pose by pose: the frame it showed and the next one of its
    /// animation (by the delay table and the elapsed pts), at its last feet moved on by its velocity, in a window of a few
    /// pixels. Three video frames: every client frame lasts at least 70 ms (median 180, none under 34), so at 30 frames per
    /// second each pose is on screen for two frames or more and a pose is never skipped. Beyond that, and on keyframes half
    /// a second apart, the track's species is searched along a reach-limited stretch of its foothold instead.
    /// </summary>
    public const double PreciseSeconds = 0.1;

    /// <summary>Tracks whose sprite matched within this long are predicted (the tracker's own memory for a missed track).</summary>
    public const double PredictMemorySeconds = EntityTracker.MaxMissedSeconds;

    /// <summary>
    /// Score a prediction needs, and the share of its pixels that must be comparable: the foothold search's, which also
    /// places frames by their origin without a blob to vouch for them (its finds score a median 1.00, p5 0.66 on
    /// fx-combat-exact). Above the matcher's 0.2 for a blob, so a prediction never verifies on less than a blob would.
    /// </summary>
    public const double PredictThreshold = FootholdSearch.Threshold, PredictMinComparable = FootholdSearch.MinComparable;

    /// <summary>A pose of the predicted group scoring this much ends the search for that track: no transition can do much better.</summary>
    public const double ConvincingScore = 0.9;

    /// <summary>A transition replaces an earlier group's best only when it scores this much more (the predicted pose wins ties).</summary>
    public const double TransitionMargin = 0.02;

    /// <summary>Window of the precise prediction: this many pixels around the feet plus the reach of 1.5 times the walking speed over the time since the last match; rows likewise (more for jumpers).</summary>
    public const int PreciseSlackX = 2, PreciseSlackY = 2, JumpSlackY = 6;

    /// <summary>
    /// A track whose kill was credited stops being predicted once none of its poses verified for this long: the EXP can come before
    /// the die animation (the frozen Wild Boar of fx-combat-exact's frames 772 to 778 showed exact moves after its kill's EXP and died
    /// on 779), so one failed frame is not the end.
    /// </summary>
    public const double FinishedSeconds = 0.3;

    /// <summary>Score a die pose needs to replace a living pose by a transition while no kill was credited.</summary>
    public const double DieTransitionScore = 0.8;

    /// <summary>Slack after the last die frame's delay before the mob counts as gone.</summary>
    public const double DieGraceSeconds = 0.15;

    /// <summary>Extra columns a hit or die pose may have moved: the knock-back that starts with the hit.</summary>
    public const int KnockSlackX = 6;

    /// <summary>
    /// The full search (every blob through the species search, the whole foothold search, no pixel taken as explained) runs on
    /// every this many frames, with the predictions verified beside it: what it finds that the predictions did not explain is
    /// kept, and a prediction it contradicts is counted as drift. See the README's Temporal pass for the measurement.
    /// </summary>
    public const int DefaultFullEvery = 10;

    /// <summary>When false, no prediction is made and every frame runs the full search alone (the pipeline before the temporal pass).</summary>
    public bool Temporal { get; set; } = true;

    /// <summary>Frames between full searches (<see cref="DefaultFullEvery"/>; 1 runs the full search on every frame, with the predictions verified beside it).</summary>
    public int FullEvery { get; set; } = DefaultFullEvery;

    private int _framesSinceFull = int.MaxValue / 2;
    private bool _lastDetected;
    private double? _lastPts;
    /// <summary>Per track: the pose it was last verified in (species, anim, frame, facing), since when and when last seen (pts).</summary>
    private readonly Dictionary<int, ((int MobId, string Anim, int FrameIndex, int Facing) Key, double Since, double Seen)> _poses = [];
    private byte[] _explained = [];
    private readonly Dictionary<(int Species, string Key), SpriteFrame?> _byKey = [];

    private (int L, int T, int R, int B)? _mapExtent;

    /// <summary>A species' frame with the given key (animation/index/facing), or null.</summary>
    private SpriteFrame? FrameByKey(MobSpecies species, string key)
    {
        if (_byKey.TryGetValue((species.Id, key), out var f)) return f;
        return _byKey[(species.Id, key)] = species.Frames.FirstOrDefault(x => x.Key == key);
    }

    /// <summary>Counters of the temporal pass since construction, for the report.</summary>
    public sealed class TemporalStats
    {
        public int TemporalFrames, FullFrames;
        public int Tracks, Precise, Stretch;
        public int Predicted, Transitions, Failed, Contested, Conflicts;
        public int FullAgree, FullDrift, FullOnly;
        /// <summary>Mob tracks verified for the first time (their first match), on full and on temporal frames: what each kind of frame discovers.</summary>
        public int FirstMatchFull, FirstMatchTemporal;
        public long PixelsExplained;
        public double Ms;
        public readonly Dictionary<string, int> TransitionsByKind = [];
    }

    public TemporalStats Temporals { get; } = new();

    /// <summary>
    /// Whether this frame runs the full search (every <see cref="FullEvery"/> frames, and after a frame the detector did not
    /// run on, a registration failure or a map change: there is nothing recent to predict from).
    /// </summary>
    private bool NextIsFull()
    {
        var full = !Temporal || !_lastDetected || _framesSinceFull + 1 >= FullEvery;
        _framesSinceFull = full ? 0 : _framesSinceFull + 1;
        return full;
    }

    /// <summary>
    /// Starts the frame's explained pixels: on a temporal frame every pixel the pet, a verified prediction or an own skill effect
    /// explains is left out of all matching that follows (the later predictions and the full search on the rest).
    /// </summary>
    private void BeginExplained(bool clear)
    {
        if (!clear) return;
        if (_explained.Length != _fg.Width * _fg.Height) _explained = new byte[_fg.Width * _fg.Height];
        else Array.Clear(_explained);
        _matcher.Explained = _explained;
    }

    /// <summary>
    /// Tracks no longer predicted: their kill was credited and their die pose is gone (a prediction of it failed), or their die
    /// animation ran to its last frame and past that frame's delay. Before, a vanished track was predicted until its memory ran
    /// out (1.2 s; about 12,000 failed predictions on fx-combat-exact's video).
    /// </summary>
    private readonly HashSet<int> _finished = [];

    /// <summary>Predictions not made because the track's mob was finished (see <see cref="_finished"/>), since construction.</summary>
    public int FinishedSkips { get; private set; }

    /// <summary>
    /// Verifies the predictions of the live identified tracks. On a temporal frame (<paramref name="clear"/>) the pixels each
    /// verified prediction explains are cleared from the foreground mask, so the full search that follows sees only the
    /// rest; on a full frame they are left in place.
    /// </summary>
    private List<Prediction> Predict(ReadOnlySpan<byte> frame, int frameWidth, DetectionContext ctx, int camX, int camY, bool clear)
    {
        var t0 = Stopwatch.GetTimestamp();
        var result = new List<Prediction>();
        var stretch = new List<(EntityTracker.LiveTrack Track, MobSpecies Species)>();
        var pts = ctx.Pts ?? ctx.Time;
        var step = _lastPts is { } lp && pts > lp ? pts - lp : 1 / 30.0;
        var vp = _mask.Viewport;
        // Each verified prediction faces the species its pixels could also be (recolours, and a shape another species shares,
        // as the Dark Stump's body is the Dark Axe Stump's): when another species explains them better the prediction does not
        // stand and the full search decides. Then the pixels it explains are taken out of the foreground.
        void Accept(EntityTracker.LiveTrack t, SpriteMatch m0, FoundBy found, bool precise, ReadOnlySpan<byte> px)
        {
            int x0 = m0.Left + m0.Frame.X0 - ConfirmMargin, y0 = m0.Top + m0.Frame.Y0 - ConfirmMargin;
            int x1 = m0.Left + m0.Frame.X1 + ConfirmMargin, y1 = m0.Top + m0.Frame.Y1 + ConfirmMargin;
            if (!_matcher.PatchCovers(m0.Left, m0.Top, m0.Left + m0.Frame.Width - 1, m0.Top + m0.Frame.Height - 1))
                _matcher.PrepareRegion(px, frameWidth, _mask, x0, y0, x1, y1);
            if (Confirm(t, m0) is not { } m) { Temporals.Contested++; return; }
            var (pixels, extent) = _matcher.Explain(m, _fg.Mask, _fg.Width, _fg.Height, vp, clear, clear ? _explained : null);
            Temporals.PixelsExplained += pixels;
            var box = extent ?? new CaptureRect(m.Left + m.Frame.X0, m.Top + m.Frame.Y0, m.Frame.ExtentWidth, m.Frame.ExtentHeight);
            result.Add(new Prediction(t.TrackId, m, found, pixels, box, precise));
            if (found == FoundBy.Prediction) Temporals.Predicted++;
            else
            {
                Temporals.Transitions++;
                var kind = TransitionKind(t.Last.Frame, m.Frame);
                Temporals.TransitionsByKind[kind] = Temporals.TransitionsByKind.GetValueOrDefault(kind) + 1;
            }
        }
        foreach (var t in _tracker.Live(ctx.Time, PredictMemorySeconds))
        {
            if (_mobs!.ById(t.Last.Species.Id)?.Species is not { } species) continue;
            if (_finished.Contains(t.TrackId)) { FinishedSkips++; continue; }
            // A die animation that ran to its last frame and past its delay is over: the mob is gone.
            if (t.Last.Frame.Anim.StartsWith("die", StringComparison.Ordinal) && _poses.TryGetValue(t.TrackId, out var shown)
                && !species.Frames.Any(f => f.Anim == t.Last.Frame.Anim && f.Facing == t.Last.Frame.Facing && f.Index > t.Last.Frame.Index)
                && pts - shown.Since > t.Last.Frame.DelayMs / 1000.0 + DieGraceSeconds)
            {
                _finished.Add(t.TrackId);
                FinishedSkips++;
                continue;
            }
            Temporals.Tracks++;
            var dtm = ctx.Time - t.LastMatchTime;
            if (dtm <= PreciseSeconds && _poses.TryGetValue(t.TrackId, out var pose))
            {
                Temporals.Precise++;
                if (Precise(t, species, frame, frameWidth, ctx, camX, camY, dtm, pts - pose.Since, step) is not { } p)
                {
                    Temporals.Failed++;
                    if (t.Dead && dtm > FinishedSeconds) _finished.Add(t.TrackId);
                    continue;
                }
                // Two tracks on one mob (a track broken earlier and its successor): the more verified one keeps it.
                if (result.Any(a => SameMob(a.Match, p.Match))) { Temporals.Conflicts++; continue; }
                Accept(t, p.Match, p.Found, true, frame);
            }
            else stretch.Add((t, species));
        }
        // Along the foothold, the most verified tracks first: the find nearest where the track was predicted that no other track took.
        foreach (var (t, species) in stretch)
        {
            Temporals.Stretch++;
            if (Stretch(t, species, frame, frameWidth, ctx, camX, camY, result) is not { } m)
            {
                Temporals.Failed++;
                if (t.Dead && ctx.Time - t.LastMatchTime > FinishedSeconds) _finished.Add(t.TrackId);
                continue;
            }
            Accept(t, m, m.Frame.Anim == t.Last.Frame.Anim && m.Frame.Facing == t.Last.Frame.Facing ? FoundBy.Prediction : FoundBy.Transition, false, frame);
        }
        Temporals.Ms += Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
        return result;
    }

    /// <summary>What kind of transition a verified pose was from the track's last one, for the report.</summary>
    private static string TransitionKind(SpriteFrame last, SpriteFrame now) =>
        now.Facing != last.Facing ? "facing" : now.Anim == last.Anim ? "frame" : now.Anim.StartsWith("hit", StringComparison.Ordinal) ? "hit"
        : now.Anim.StartsWith("die", StringComparison.Ordinal) ? "die" : now.Anim is "move" or "stand" ? "stand/move" : "other";

    /// <summary>
    /// The precise prediction (a video, or any track matched within <see cref="PreciseSeconds"/>): the poses in a fixed order,
    /// each placed by its origin at the predicted feet within a window of a few pixels. First the predicted group (the
    /// frame shown and the next of its animation, the next first once the frame's delay has elapsed); then the transitions:
    /// the neighbouring frames, stand and move, the other facing, hit, die, and the species' other animations. A dead track
    /// (its kill credited) shows only its die pose. The best of the first group with a pass wins unless a later one does
    /// clearly better; a convincing pass ends the search.
    /// </summary>
    private (SpriteMatch Match, FoundBy Found)? Precise(EntityTracker.LiveTrack t, MobSpecies species, ReadOnlySpan<byte> frame, int frameWidth, DetectionContext ctx,
        int camX, int camY, double dtm, double inPose, double step)
    {
        var last = t.Last.Frame;
        var walk = EntityTracker.SpeedFactor * species.WalkSpeed;
        // The feet: moved on by the track's velocity along x (no faster than it can walk), on its foothold at that x.
        var wx = t.LastWorld.X + Math.Clamp(t.Velocity.X, -walk, walk) * dtm;
        var wy = t.LastWorld.Y;
        if (species.Mobility != MobMobility.Fly && ctx.Footholds.Nearest(new PointD(wx, wy), PreciseSlackY + 2) is { } h) wy = h.SurfaceY;
        int fx = (int)Math.Round(wx - camX), fy = (int)Math.Round(wy - camY);
        var winX = PreciseSlackX + (int)Math.Ceiling(walk * dtm);
        var winY = species.Mobility == MobMobility.Jump ? JumpSlackY : PreciseSlackY;
        var (l, tp, r, b) = MapExtent();
        _matcher.PrepareRegion(frame, frameWidth, _mask, fx + l - winX - KnockSlackX - 2, fy + tp - winY - 2, fx + r + winX + KnockSlackX + 2, fy + b + winY + 2);

        SpriteMatch? best = null;
        var bestFound = FoundBy.Prediction;
        var tried = new HashSet<SpriteFrame>(ReferenceEqualityComparer.Instance);
        foreach (var (found, knock, frames) in Poses(species, last, t.Dead, inPose, step))
        {
            SpriteMatch? groupBest = null;
            foreach (var f in frames)
            {
                if (!tried.Add(f)) continue;
                // A mob whose kill no EXP gain credited dies only on a convincing die pose: half hidden behind the pet, the frozen Wild
                // Boar of fx-combat-exact's continuity frames took die1/0 at 0.52 on frame 748, a second before its kill's EXP.
                var needed = !t.Dead && f.Anim.StartsWith("die", StringComparison.Ordinal) && f.Anim != last.Anim ? DieTransitionScore : PredictThreshold;
                if (_matcher.Best(new CaptureRect(fx, fy, 1, 1), new PointD(fx, fy), species, f, needed, winX + (knock ? KnockSlackX : 0), winY,
                        minComparable: PredictMinComparable) is { } m && m.Score >= needed && (groupBest is null || m.Score > groupBest.Value.Score))
                    groupBest = m;
            }
            if (groupBest is not { } g) continue;
            if (best is null || g.Score > best.Value.Score + TransitionMargin) { best = g; bestFound = found; }
            if (best.Value.Score >= ConvincingScore || best.Value.Matches == best.Value.Compared) break;
        }
        return best is { } bm ? (bm, bestFound) : null;
    }

    /// <summary>
    /// The pose groups of the precise prediction, in order: (how a pass is counted, whether the knock-back slack applies, the
    /// frames). Frames of one animation and facing in index order; the stored frames that are links (a snail's stand is its
    /// move/0) are those frames.
    /// </summary>
    private static IEnumerable<(FoundBy Found, bool Knock, IEnumerable<SpriteFrame> Frames)> Poses(MobSpecies species, SpriteFrame last, bool dead, double inPose, double step)
    {
        List<SpriteFrame> Seq(string anim, int facing) => species.Frames.Where(f => f.Anim == anim && f.Facing == facing).OrderBy(f => f.Index).ToList();
        IEnumerable<SpriteFrame> Anims(Func<string, bool> which, int facing) => species.Frames.Where(f => which(f.Anim) && f.Facing == facing).OrderBy(f => f.Anim, StringComparer.Ordinal).ThenBy(f => f.Index);
        static bool IsDie(string a) => a.StartsWith("die", StringComparison.Ordinal);
        static bool IsHit(string a) => a.StartsWith("hit", StringComparison.Ordinal);
        var facing = last.Facing;
        var seq = Seq(last.Anim, facing);
        var at = seq.FindIndex(f => f.Index == last.Index);
        // A die animation ends on its last frame; the others loop (hit1 has one frame and then gives way to stand or move).
        SpriteFrame? Step(int k) => at < 0 || seq.Count == 0 ? null : IsDie(last.Anim) ? seq[Math.Clamp(at + k, 0, seq.Count - 1)] : seq[((at + k) % seq.Count + seq.Count) % seq.Count];
        if (dead)
        {
            // The kill was credited: the die pose, the frame shown and those after it first; until it starts, the pose it was in (the
            // EXP can come a few frames before the die animation).
            if (IsDie(last.Anim)) yield return (FoundBy.Prediction, true, inPose + step / 2 >= last.DelayMs / 1000.0 ? [Step(1)!, last] : [last, Step(1)!]);
            yield return (FoundBy.Transition, true, Anims(IsDie, facing).Concat(Anims(IsDie, -facing)));
            if (!IsDie(last.Anim)) yield return (FoundBy.Prediction, false, seq);
            yield break;
        }
        var next = Step(1);
        // The next frame first once the shown one's delay has run out (its start is known to a frame).
        if (next is null) yield return (FoundBy.Prediction, IsHit(last.Anim), [last]);
        else yield return (FoundBy.Prediction, IsHit(last.Anim), inPose + step / 2 >= last.DelayMs / 1000.0 ? [next, last] : [last, next]);
        if (Step(-1) is { } prev && Step(2) is { } after) yield return (FoundBy.Transition, false, [prev, after]);
        yield return (FoundBy.Transition, false, Anims(a => a is "stand" or "move", facing));
        yield return (FoundBy.Transition, false, Anims(a => a == last.Anim, -facing).Concat(Anims(a => a is "stand" or "move", -facing)));
        yield return (FoundBy.Transition, true, Anims(IsHit, facing).Concat(Anims(IsHit, -facing)));
        yield return (FoundBy.Transition, true, Anims(IsDie, facing).Concat(Anims(IsDie, -facing)));
        yield return (FoundBy.Transition, true, Anims(a => !(IsHit(a) || IsDie(a) || a is "stand" or "move"), facing).Concat(Anims(a => !(IsHit(a) || IsDie(a) || a is "stand" or "move"), -facing)));
    }

    /// <summary>
    /// The search along the foothold (keyframes, or a track missed for a few frames): every frame of the track's species at
    /// every column of its floor within its reach, <see cref="EntityTracker.GateBase"/> plus 1.5 times its walking speed over
    /// the time since its last match (plus the knock-back after a hit), feet on the line. Each placement is probed on a few
    /// spread opaque pixels as the foothold search probes (exactly equal as drawn or frozen, at most two missing), on the
    /// feet's row and, on a slope, the rows either side (the line's rounding); the survivors get the strict score, nearest
    /// the predicted feet first (<see cref="ScorePlaces"/>). The stretch around where the track walked to at its velocity is
    /// searched first, the rest of its reach only when nothing there passes. One species over a short stretch is probed
    /// placement by placement rather than through the foothold search's colour anchors, which look an anchor up on the row
    /// of its own column's feet and so miss mobs on a slope. A dead track's die pose only.
    /// </summary>
    /// <param name="taken">Predictions verified already: a placement on one of their mobs is another track's.</param>
    private SpriteMatch? Stretch(EntityTracker.LiveTrack t, MobSpecies species, ReadOnlySpan<byte> frame, int frameWidth, DetectionContext ctx, int camX, int camY,
        List<Prediction> taken)
    {
        if (_search is null || species.Mobility == MobMobility.Fly) return null;
        var dtm = Math.Min(ctx.Time - t.LastMatchTime, EntityTracker.MaxMissedSeconds);
        var reach = EntityTracker.GateBase + EntityTracker.SpeedFactor * species.WalkSpeed * dtm + (ctx.Time - t.HitTime <= EntityTracker.HitMemorySeconds ? EntityTracker.KnockBack : 0);
        var vp = _mask.Viewport;
        int sx0 = Math.Max(vp.X, (int)Math.Floor(t.LastWorld.X - reach - camX)), sx1 = Math.Min(vp.X + vp.Width - 1, (int)Math.Ceiling(t.LastWorld.X + reach - camX));
        if (sx1 < sx0) return null;
        var fy = (int)Math.Round(t.LastWorld.Y - camY);
        var frames = t.Dead ? species.Frames.Where(f => f.Anim.StartsWith("die", StringComparison.Ordinal)).ToList() : species.Frames;
        // The track's own floor only (a jumper that left it is the full search's to find), within the height of a jump.
        var footholds = _search.Under(ctx.Map, camX, camY, sx0, sx1, fy - (int)MaxAirUnits, fy + (int)MaxAirUnits)
            .Where(fh => t.Floor is not { } floor || ctx.Footholds.FloorOf(fh.Id) == floor).ToList();
        // First the stretch around where it walked to at its velocity (half its reach of speed either way), then the rest.
        var walk = EntityTracker.SpeedFactor * species.WalkSpeed;
        var px0 = t.LastWorld.X + Math.Clamp(t.Velocity.X, -walk, walk) * dtm - camX;
        var inner = PreciseSlackX + walk * dtm / 2;
        int i0 = Math.Max(sx0, (int)Math.Floor(px0 - inner)), i1 = Math.Min(sx1, (int)Math.Ceiling(px0 + inner));
        var hits = new List<(MobSpecies, SpriteFrame, int, int)>();
        if (i1 >= i0)
        {
            ProbeStretch(frame, frameWidth, footholds, camX, camY, i0, i1, species, frames, hits);
            if (ScorePlaces(frame, frameWidth, hits, px0, taken, t.Last.Frame) is { } m) return m;
        }
        hits.Clear();
        if (i0 > sx0) ProbeStretch(frame, frameWidth, footholds, camX, camY, sx0, Math.Min(sx1, i0 - 1), species, frames, hits);
        if (i1 < sx1) ProbeStretch(frame, frameWidth, footholds, camX, camY, Math.Max(sx0, i1 + 1), sx1, species, frames, hits);
        return ScorePlaces(frame, frameWidth, hits, px0, taken, t.Last.Frame);
    }

    /// <summary>
    /// Probes every placement with the feet on one of the footholds at screen columns <paramref name="lo"/> to <paramref name="hi"/>
    /// (on a slope the rows either side too) for every one of the frames, as the foothold search probes, and adds those that pass.
    /// </summary>
    private void ProbeStretch(ReadOnlySpan<byte> frame, int frameWidth, List<Foothold> footholds, int camX, int camY, int lo, int hi, MobSpecies species,
        IReadOnlyList<SpriteFrame> frames, List<(MobSpecies, SpriteFrame, int, int)> hits)
    {
        var pixels = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, int>(frame);
        var vp = _mask.Viewport;
        int frameHeight = frame.Length / 4 / frameWidth, mw = _mask.Width, mh = _mask.Height;
        var bits = _mask.Bits;
        var explained = _matcher.Explained;
        var probes = new ProbePixels[frames.Count];
        for (var k = 0; k < frames.Count; k++) probes[k] = ProbeOf(frames[k]);
        foreach (var fh in footholds)
        {
            int a = Math.Max(lo, Math.Min(fh.X1, fh.X2) - camX), b = Math.Min(hi, Math.Max(fh.X1, fh.X2) - camX);
            var slope = fh.Y1 != fh.Y2;
            for (var x = a; x <= b; x++)
            {
                if (fh.YAt(x + camX) is not { } yw) continue;
                var y0 = (int)Math.Round(yw - camY);
                for (var dy = slope ? -1 : 0; dy <= (slope ? 1 : 0); dy++)
                {
                    var y = y0 + dy;
                    for (var k = 0; k < frames.Count; k++)
                    {
                        var p = probes[k];
                        int compared = 0, missed = 0;
                        for (var i = 0; i < p.Dx.Length && compared < 8; i++)
                        {
                            int px = x + p.Dx[i], py = y + p.Dy[i];
                            if ((uint)px >= (uint)frameWidth || (uint)py >= (uint)frameHeight) continue;
                            int vx = px - vp.X, vy = py - vp.Y;
                            if ((uint)vx >= (uint)mw || (uint)vy >= (uint)mh || bits[vy * mw + vx] != 0 || explained is not null && explained[vy * mw + vx] != 0) continue;
                            compared++;
                            var c = pixels[py * frameWidth + px] & 0xFFFFFF;
                            if (c == p.Colour[i] || c == p.Frozen[i] || CloudAgrees(c, p.Colour[i], px, py)) continue;
                            if (++missed > FootholdSearch.ProbeMisses || (compared == 2 && missed == 2)) break;
                        }
                        if (compared >= FootholdSearch.ProbeCompared && missed <= FootholdSearch.ProbeMisses) hits.Add((species, frames[k], x, y));
                    }
                }
            }
        }
    }

    /// <summary>
    /// The strict score of probed placements, nearest <paramref name="px0"/> first, a place at a time: the placements within a
    /// body's width of the nearest one not yet scored are scored together (one prepared patch, each within a pixel either
    /// way); the best that passes and is no other track's mob wins, and a place where none does gives way to the next.
    /// </summary>
    /// <param name="last">The track's last frame: its animation and facing are scored first, and a convincing score ends the place.</param>
    /// <param name="threshold">The score a placement needs (default <see cref="PredictThreshold"/>; the pet's search passes its own).</param>
    private SpriteMatch? ScorePlaces(ReadOnlySpan<byte> frame, int frameWidth, List<(MobSpecies, SpriteFrame, int, int)> hits, double px0, List<Prediction> taken, SpriteFrame last,
        double threshold = PredictThreshold)
    {
        if (hits.Count == 0) return null;
        var left = hits.OrderBy(h => Math.Abs(h.Item3 - px0)).ToList();
        SpriteMatch? best = null;
        while (left.Count > 0 && best is null)
        {
            var near = left[0].Item3;
            var place = left.Where(h => Math.Abs(h.Item3 - near) <= SameMobX).ToList();
            left.RemoveAll(h => Math.Abs(h.Item3 - near) <= SameMobX);
            // A placement probed within a pixel of another of the same frame adds nothing: the score's window covers it.
            var kept = new List<(MobSpecies, SpriteFrame, int, int)>();
            foreach (var h in place)
                if (!kept.Any(k => ReferenceEquals(k.Item2, h.Item2) && Math.Abs(k.Item3 - h.Item3) <= FootholdSearch.RefineX && Math.Abs(k.Item4 - h.Item4) <= FootholdSearch.RefineY)) kept.Add(h);
            // The patch covers every kept placement's frame (and the contest's margin around the best).
            int x0 = kept.Min(h => h.Item3 + h.Item2.X0 - h.Item2.OriginX), x1 = kept.Max(h => h.Item3 + h.Item2.X1 - h.Item2.OriginX);
            int y0 = kept.Min(h => h.Item4 + h.Item2.Y0 - h.Item2.OriginY), y1 = kept.Max(h => h.Item4 + h.Item2.Y1 - h.Item2.OriginY);
            var pad = FootholdSearch.RefineX + ConfirmMargin + 2;
            _matcher.PrepareRegion(frame, frameWidth, _mask, x0 - pad, y0 - pad, x1 + pad, y1 + pad);
            foreach (var (sp, f, x, y) in kept.OrderBy(h => h.Item2.Facing == last.Facing ? h.Item2.Anim == last.Anim ? 0 : 1 : 2))
            {
                if (_matcher.Best(new CaptureRect(x, y, 1, 1), new PointD(x, y), sp, f, threshold, FootholdSearch.RefineX, FootholdSearch.RefineY, minComparable: PredictMinComparable) is { } m
                    && m.Score >= threshold && (best is null || m.Score > best.Value.Score || m.Score == best.Value.Score && m.Compared > best.Value.Compared))
                    best = m;
                if (best is { } b0 && (b0.Score >= ConvincingScore || b0.Matches == b0.Compared)) break;
            }
            if (best is { } found && taken.Any(p => SameMob(p.Match, found))) best = null;
        }
        return best;
    }

    private sealed record ProbePixels(int[] Dx, int[] Dy, int[] Colour, int[] Frozen);
    private readonly Dictionary<SpriteFrame, ProbePixels> _probePixels = new(ReferenceEqualityComparer.Instance);

    /// <summary>A frame's first <see cref="FootholdSearch.ProbePixels"/> spread opaque pixels as offsets from its origin, with their colours as drawn and frozen.</summary>
    private ProbePixels ProbeOf(SpriteFrame f)
    {
        if (_probePixels.TryGetValue(f, out var p)) return p;
        var n = Math.Min(FootholdSearch.ProbePixels, f.PixelX.Length);
        p = new ProbePixels(Enumerable.Range(0, n).Select(i => f.PixelX[i] - f.OriginX).ToArray(), Enumerable.Range(0, n).Select(i => f.PixelY[i] - f.OriginY).ToArray(),
            f.PixelPacked[..n], f.PixelFrozen[..n]);
        return _probePixels[f] = p;
    }

    /// <summary>
    /// The species contest for a verified prediction, as for any match (<see cref="Contest"/>): against its recolours, settled
    /// on the pixels that tell them apart, and against every other species whose frame of the same pose at the same feet
    /// scores within <see cref="ContestSlack"/> of it, on score times the share of the foreground the silhouette covers. The
    /// confirmed match, or null when another species wins.
    /// </summary>
    private SpriteMatch? Confirm(EntityTracker.LiveTrack t, SpriteMatch m)
    {
        var recolours = _mobs!.Recolours(m.Species);
        var rivals = new List<MapMobSet.Entry>();
        int fx = (int)Math.Round(m.Feet.X), fy = (int)Math.Round(m.Feet.Y);
        foreach (var e in _mobs.Entries)
        {
            if (e.Species.Id == m.Species.Id || recolours.Contains(e.Species) || !ShapeRival(m.Species, e.Species)) continue;
            if (FrameByKey(e.Species, m.Frame.Key) is not { } other) continue;
            if (_matcher.At(e.Species, other, fx - other.OriginX, fy - other.OriginY) is { } r && r.Score >= m.Score - ContestSlack) rivals.Add(e);
        }
        if (recolours.Count == 0 && rivals.Count == 0) return m;
        if (rivals.Count == 0)
        {
            // Recolours alone: settled on the pixels that tell them apart, at the same placement (as the contest does).
            var group = new List<SpriteMatch> { m };
            foreach (var other in recolours)
                if (FrameByKey(other, m.Frame.Key) is { } of && _matcher.At(other, of, m.Left, m.Top) is { } om) group.Add(om);
            var won = group.Count == 1 ? m : Settle(group, t.Last);
            if (won.Species.Id != m.Species.Id) return null;
            return group.Where(x => x.Species.Id != won.Species.Id).Select(x => (SpriteMatch?)x).MaxBy(x => x!.Value.Score) is { } rv
                ? won with { Rival = rv.Species, RivalScore = rv.Score } : won;
        }
        // The candidate the contest weighs coverage on: the frame's extent and a margin, where a rival's extra parts would show.
        var vp = _mask.Viewport;
        var box = new CaptureRect(m.Left + m.Frame.X0 - ConfirmMargin, m.Top + m.Frame.Y0 - ConfirmMargin, m.Frame.ExtentWidth + 2 * ConfirmMargin, m.Frame.ExtentHeight + 2 * ConfirmMargin);
        var probe = new Candidate
        {
            Box = box, Feet = m.Feet, World = m.Feet,
            Pixels = _fg.CountIn(box.X - vp.X, box.Y - vp.Y, box.X + box.Width - 1 - vp.X, box.Y + box.Height - 1 - vp.Y),
        };
        var settled = Contest(probe, m, rivals, t.Last, m.Feet, (FootholdSearch.RefineX, FootholdSearch.RefineY));
        return settled.Species.Id == m.Species.Id ? settled : null;
    }

    /// <summary>
    /// Whether another species (not a recolour) draws pixels a frame of this one could be taken for: in some pose both have,
    /// placed on the same origin, at least <see cref="ShapeShare"/> of this frame's opaque pixels show the same colour in the
    /// other's (the Dark Stump's body is the Dark Axe Stump's). Only such species are scored against a verified prediction.
    /// </summary>
    private bool ShapeRival(MobSpecies a, MobSpecies b)
    {
        if (_shapeRivals.TryGetValue((a.Id, b.Id), out var known)) return known;
        var rival = false;
        foreach (var f in a.Frames)
        {
            if (rival || FrameByKey(b, f.Key) is not { } g || f.PixelX.Length == 0) continue;
            var same = 0;
            for (var i = 0; i < f.PixelX.Length; i++)
            {
                int gx = f.PixelX[i] - f.OriginX + g.OriginX, gy = f.PixelY[i] - f.OriginY + g.OriginY;
                if ((uint)gx >= (uint)g.Width || (uint)gy >= (uint)g.Height) continue;
                var o = (gy * g.Width + gx) * 4;
                if (g.Bgra[o + 3] == SpriteFrame.SolidAlpha && (g.Bgra[o] | g.Bgra[o + 1] << 8 | g.Bgra[o + 2] << 16) == f.PixelPacked[i]) same++;
            }
            rival = same >= ShapeShare * f.PixelX.Length;
        }
        return _shapeRivals[(a.Id, b.Id)] = rival;
    }

    /// <summary>Share of a frame's opaque pixels another species must draw alike for it to be a shape rival (see <see cref="ShapeRival"/>).</summary>
    public const double ShapeShare = 0.5;

    private readonly Dictionary<(int, int), bool> _shapeRivals = [];

    /// <summary>Margin around a prediction's extent in which the species contest weighs coverage (a Dark Axe Stump's axe beyond a Dark Stump's frame).</summary>
    public const int ConfirmMargin = 10;

    /// <summary>The extent of every frame of every species of the map around its origin (left, top, right, bottom, inclusive).</summary>
    private (int L, int T, int R, int B) MapExtent()
    {
        if (_mapExtent is { } known) return known;
        int l = 0, t = 0, r = 0, b = 0;
        foreach (var e in _mobs!.Entries)
            foreach (var f in e.Species.Frames)
            {
                l = Math.Min(l, f.X0 - f.OriginX); t = Math.Min(t, f.Y0 - f.OriginY);
                r = Math.Max(r, f.X1 - f.OriginX); b = Math.Max(b, f.Y1 - f.OriginY);
            }
        _mapExtent = (l, t, r, b);
        return _mapExtent.Value;
    }

    /// <summary>
    /// After tracking: the pose each track was verified in and since when (pts), for the next frame's delay-table
    /// prediction; poses of tracks not verified for a while are forgotten.
    /// </summary>
    private void RememberPoses(IReadOnlyList<DetectedEntity> entities, DetectionContext ctx, bool full)
    {
        var pts = ctx.Pts ?? ctx.Time;
        foreach (var e in entities)
        {
            if (e.Kind != EntityKind.Mob || e.Identity is not { Verified: true } id) continue;
            // A track verified alive again (a die pose matched by mistake) is predicted again.
            if (!id.Anim.StartsWith("die", StringComparison.Ordinal) && e.State != MobState.Dying) _finished.Remove(e.TrackId);
            if (id.Sightings == 1) { if (full) Temporals.FirstMatchFull++; else Temporals.FirstMatchTemporal++; }
            var key = (id.MobId, id.Anim, id.FrameIndex, id.Facing);
            _poses[e.TrackId] = _poses.TryGetValue(e.TrackId, out var p) && p.Key == key ? (key, p.Since, pts) : (key, pts, pts);
        }
        foreach (var id in _poses.Where(p => pts - p.Value.Seen > PredictMemorySeconds).Select(p => p.Key).ToList()) _poses.Remove(id);
        _lastPts = pts;
    }

    /// <summary>
    /// A temporal frame's verified predictions as blobs of the residual: their explained pixels' extent, so name tags and HP
    /// bars attach to them as to any blob (the mob's pixels are gone from the foreground the components are found in).
    /// </summary>
    private static Blob BlobOf(Prediction p, CaptureRect vp) =>
        new(p.Box.X - vp.X, p.Box.Y - vp.Y, p.Box.X + p.Box.Width - 1 - vp.X, p.Box.Y + p.Box.Height - 1 - vp.Y, p.Pixels, 0, 0, p.Pixels);

    /// <summary>
    /// A full frame: the full search ran on every pixel, and the predictions were verified beside it. A prediction the full
    /// search found the same mob for (same place, same species) binds that candidate to its track; one where the full search
    /// matched another species is drift and is dropped; one the full search did not find at all is kept as a candidate of
    /// its own, since its pixels verified it.
    /// </summary>
    private void Reconcile(List<Candidate> candidates, List<Prediction> predictions, DetectionContext ctx, int camX, int camY)
    {
        foreach (var p in predictions)
        {
            var same = candidates.FirstOrDefault(c => c.Sprite is { } s && SameMob(s, p.Match));
            if (same is not null)
            {
                if (same.Sprite!.Value.Species.Id == p.Match.Species.Id)
                {
                    same.PredictedTrack ??= p.TrackId;
                    Temporals.FullAgree++;
                }
                else Temporals.FullDrift++;
                continue;
            }
            // An unverified blob the predicted frame holds is that mob's blob (a box cut so that no frame fitted it: a Red Snail on the
            // video's frame 207 was a separate effect beside its own prediction).
            var blob = candidates.FirstOrDefault(c => c.Sprite is null && c.Drop is null && c.PetSprite is null && c.Skill is null && c.Damage is null && c.Tag is null && c.Pixels > 0 && Covers(p.Match, c.Box) >= LeftoverShare);
            if (blob is not null) Predicted(blob, p, ctx, camX, camY);
            else candidates.Add(PredictedCandidate(p, ctx, camX, camY));
            Temporals.FullOnly++;
        }
    }

    /// <summary>A verified prediction as a candidate of its own (its box the pixels it explains), bound to its track.</summary>
    private Candidate PredictedCandidate(Prediction p, DetectionContext ctx, int camX, int camY)
    {
        var c = MakeCandidate(ctx, camX, camY, p.Box, p.Match.Feet, p.Pixels, null, false, false, null, _ownWorld);
        Predicted(c, p, ctx, camX, camY);
        return c;
    }

    /// <summary>Gives a candidate a verified prediction: its sprite, feet at the sprite's origin, how it was found and its track.</summary>
    private static void Predicted(Candidate c, Prediction p, DetectionContext ctx, int camX, int camY)
    {
        c.BoxSurfaceDistance = c.SurfaceDistance;
        c.Sprite = p.Match;
        c.Found = p.Found;
        c.PredictedTrack = p.TrackId;
        Place(c, p.Match.Feet, ctx, camX, camY);
        c.Floor = FloorFor(c, ctx);
    }
}
