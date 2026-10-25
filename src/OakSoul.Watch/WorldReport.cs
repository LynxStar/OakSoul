using OakSoul.World;

namespace OakSoul.Watch;

/// <summary>
/// Collects the world tracker's inputs and outputs per frame and prints the checks that validate the world model
/// against a recording: the minimap model per map, foothold agreement, the camera anchor and clamping, the camera
/// model's self-consistency, and the portals used at each map change. Run with --world-report on keyframes.
/// </summary>
public sealed class WorldReport
{
    private sealed record Row(long Sequence, WorldObservation Obs, WorldState State);
    private readonly List<Row> _rows = [];

    public void Add(long sequence, WorldObservation observation, WorldState state) => _rows.Add(new Row(sequence, observation, state));

    public void Print(TextWriter o, MapCatalog catalog)
    {
        o.WriteLine();
        o.WriteLine("== world report ==");
        PrintMinimapModels(o);
        PrintFootholdAgreement(o);
        PrintCamera(o);
        PrintSelfConsistency(o);
        PrintPortals(o, catalog);
    }

    // ---- minimap model -------------------------------------------------------------------------------------------

    private void PrintMinimapModels(TextWriter o)
    {
        o.WriteLine();
        o.WriteLine("minimap model per map (visible image = MapArea +(-1,+2), size +(2,-5)); portal-marker residual = px from each whole marker to the nearest predicted one");
        o.WriteLine("(a windowed axis has its window fitted from the same markers, one parameter per frame)");
        foreach (var group in _rows.Where(r => r.State.Map is not null && r.Obs.MapArea is not null).GroupBy(r => r.State.MapId!.Value))
        {
            var map = group.First().State.Map!;
            var area = group.GroupBy(r => r.Obs.MapArea!.Value).OrderByDescending(g => g.Count()).First().Key;
            var rows = group.Where(r => r.Obs.MapArea == area).ToList();
            var (pw, ph) = MinimapTransform.PredictedImageSize(map);
            var (vw, vh) = MinimapTransform.VisibleSize(area);
            o.WriteLine($"  {map.MapId,-9} {map.MapName,-30} vr {map.Vr.Width}x{map.Vr.Height}  visible {vw}x{vh} ({rows.Count} frames)  fixed-scale image {pw}x{ph}");
            foreach (var model in new[] { MinimapModel.FixedScale, MinimapModel.Stretch, MinimapModel.Letterbox })
            {
                var t = MinimapTransform.Create(map, area, model);
                var residuals = new List<double>();
                var windows = new List<double>();
                foreach (var r in rows)
                {
                    var markers = r.Obs.PortalMarkers ?? [];
                    if (markers.Count == 0) continue;
                    double wy = 0;
                    if (t.Vertical.Mode == MinimapAxisMode.Window)
                    {
                        if (t.EstimateWindow(markers) is not { } w) continue;
                        wy = w.Window;
                        windows.Add(w.Window);
                    }
                    var predicted = map.Portals.Where(p => MinimapTransform.DrawnPortalTypes.Contains(p.Type)).Select(p => t.PortalToMap(p, 0, wy)).ToList();
                    if (predicted.Count == 0) continue;
                    residuals.AddRange(MinimapTransform.WholeMarkers(markers).Select(m => predicted.Min(p => p.DistanceTo(m))));
                }
                var res = residuals.Count == 0 ? "no markers" : $"residual median {Percentile(residuals, 50):0.0} p90 {Percentile(residuals, 90):0.0} px over {residuals.Count} markers";
                var win = windows.Count == 0 ? "" : $", window y {windows.Min():0.#}..{windows.Max():0.#}";
                o.WriteLine($"      {model,-10} x {t.Horizontal.Mode,-8} {t.Horizontal.WorldPerPixel,6:0.00}/px  y {t.Vertical.Mode,-8} {t.Vertical.WorldPerPixel,6:0.00}/px  {res}{win}");
            }
        }
    }

    // ---- foothold agreement ---------------------------------------------------------------------------------------

    /// <summary>A frame with both dot and tag: the raw (unsnapped) world feet and its vertical error to the surface below.</summary>
    private sealed record Measured(Row Row, PointD Raw, PointD Soles, double? Error, bool OnLadder, bool Standing);

    private List<Measured> MeasuredFrames()
    {
        var list = new List<Measured>();
        Row? prev = null;
        foreach (var r in _rows)
        {
            var s = r.State;
            if (s.Source == WorldSource.DotAndTag && s.RawPosition is { } raw && s.Map is not null && r.Obs.TagFeet is { } feet && s.Dot is { Complete: true })
            {
                var index = new FootholdIndex(s.Map);
                var soles = CameraModel.SolesFromTag(feet);
                var below = index.Below(raw, 400, 16);
                var ladder = index.LadderAt(raw, 12) is not null;
                // Standing: neither the screen feet nor the dot moved vertically since the previous keyframe.
                var standing = prev is { State.Source: WorldSource.DotAndTag } p && p.State.MapId == s.MapId && p.Obs.TagFeet is { } pf
                    && Math.Abs(pf.Y - feet.Y) <= 2 && p.Obs.SelfInMap is { } pd && r.Obs.SelfInMap is { } d && Math.Round(pd.Y) == Math.Round(d.Y);
                list.Add(new Measured(r, raw, soles, below?.Distance, ladder, standing));
            }
            prev = r;
        }
        return list;
    }

    private void PrintFootholdAgreement(TextWriter o)
    {
        o.WriteLine();
        o.WriteLine("foothold agreement (raw world feet from dot + tag + clamped camera, before snapping; error = surface y - feet y, + means the feet are above it)");
        foreach (var group in MeasuredFrames().GroupBy(m => m.Row.State.MapId!.Value))
        {
            var all = group.ToList();
            bool Within(Measured m, double limit) => m.OnLadder || m.Error is { } e && Math.Abs(e) <= limit;
            var ladders = all.Count(m => m.OnLadder);
            var airborne = all.Count(m => !m.OnLadder && (m.Error is null || m.Error > 16));
            o.WriteLine($"  {group.Key} {all[0].Row.State.Map!.MapName}: {all.Count} frames with dot and tag; within 6 of a surface or on a ladder/rope {Fraction(all.Count(m => Within(m, 6)), all.Count)}; within 8 (one dot cell) {Fraction(all.Count(m => Within(m, 8)), all.Count)}");
            o.WriteLine($"      on a ladder/rope {ladders}; more than 16 above any surface (airborne by the error distribution) {airborne}; grounded {all.Count - ladders - airborne}: within 6 {Fraction(all.Count(m => !m.OnLadder && m.Error is { } e && Math.Abs(e) <= 6), all.Count - ladders - airborne)}");
            var standing = all.Where(m => m.Standing).ToList();
            var moving = all.Where(m => !m.Standing).ToList();
            var standingErrors = standing.Where(m => !m.OnLadder && m.Error is not null).Select(m => Math.Abs(m.Error!.Value)).ToList();
            o.WriteLine($"      standing by motion (tag feet y within 2 px and dot row unchanged since the previous keyframe) {standing.Count}: within 6 {Fraction(standing.Count(m => Within(m, 6)), standing.Count)}; off ladders |error| median {Percentile(standingErrors, 50):0.0} p90 {Percentile(standingErrors, 90):0.0} max {(standingErrors.Count > 0 ? standingErrors.Max() : 0):0.0}");
            var movingErrors = moving.Where(m => !m.OnLadder && m.Error is not null).Select(m => m.Error!.Value).ToList();
            o.WriteLine($"      moving vertically {moving.Count}: within 6 {Fraction(moving.Count(m => Within(m, 6)), moving.Count)}; {movingErrors.Count(e => e > 16)} more than 16 above a surface");
            var hist = all.Where(m => !m.OnLadder).GroupBy(m => m.Error is not { } e ? 99 : e < -16 ? -99 : e > 16 ? 98 : (int)Math.Floor(e / 4) * 4)
                .OrderBy(g => g.Key).Select(g => $"{(g.Key == 99 ? "none" : g.Key == -99 ? "<-16" : g.Key == 98 ? ">16" : $"[{g.Key},{g.Key + 4})")}:{g.Count()}");
            o.WriteLine($"      error histogram off ladders: {string.Join(" ", hist)}");
            var withSupport = group.Count(m => m.Row.State.Foothold is not null || m.Row.State.Ladder is not null);
            o.WriteLine($"      after snapping: {Fraction(withSupport, all.Count)} on a foothold or ladder");
        }
    }

    // ---- camera anchor and clamping ---------------------------------------------------------------------------------

    private void PrintCamera(TextWriter o)
    {
        o.WriteLine();
        o.WriteLine($"camera (view {CameraModel.ViewWidth}x{CameraModel.ViewHeight}; soles = tag feet - {CameraModel.TagFeetToSoles}; raw camera = dot world - soles, before clamping)");
        foreach (var group in MeasuredFrames().GroupBy(m => m.Row.State.MapId!.Value))
        {
            var map = group.First().Row.State.Map!;
            var cam = new CameraModel(map.Vr);
            var all = group.ToList();
            var rawCams = all.Select(m => (m, Cam: CameraModel.Raw(m.Row.State.Dot!.Value.Point!.Value, m.Soles))).ToList();
            const double Cell = 16, Margin = 32;
            var freeX = rawCams.Where(c => c.Cam.X > cam.MinX + Margin && c.Cam.X < cam.MaxX - Margin).Select(c => c.m.Soles.X).ToList();
            var freeY = rawCams.Where(c => c.Cam.Y > cam.MinY + Margin && c.Cam.Y < cam.MaxY - Margin).Select(c => c.m.Soles.Y).ToList();
            o.WriteLine($"  {map.MapId} {map.MapName}: unclamped soles x over {freeX.Count} frames: mode {Mode(freeX)} p10 {Percentile(freeX, 10):0} median {Percentile(freeX, 50):0} p90 {Percentile(freeX, 90):0}");
            o.WriteLine($"      unclamped soles y over {freeY.Count} frames: mode {Mode(freeY)} p10 {Percentile(freeY, 10):0} median {Percentile(freeY, 50):0} p90 {Percentile(freeY, 90):0}");
            // Rests: dot and tag unchanged from the previous keyframe, camera unclamped: where the camera settles.
            var rests = all.Where(m => m.Standing && rawCams.First(c => c.m == m).Cam is var c && c.X > cam.MinX + Margin && c.X < cam.MaxX - Margin && c.Y > cam.MinY + Margin && c.Y < cam.MaxY - Margin).ToList();
            var restX = rests.Select(m => m.Soles.X).ToList();
            var restY = rests.Select(m => m.Soles.Y).ToList();
            o.WriteLine($"      resting (standing, unclamped) {rests.Count} frames: x {Spread(restX)}; y {Spread(restY)}; y values {string.Join(" ", restY.GroupBy(v => v).OrderByDescending(g => g.Count()).Take(6).Select(g => $"{g.Key:0}x{g.Count()}"))}");
            // Clamped frames: with the right bound and view size the raw camera (dot +-8, rounding) never passes the bound
            // by more than one dot cell, and the frames at the edge cluster within a cell of it.
            void Bound(string name, double bound, IEnumerable<double> values, int outward)
            {
                var d = values.Select(v => (v - bound) * outward).ToList();
                var cluster = d.Where(v => Math.Abs(v) <= Cell).ToList();
                o.WriteLine($"      {name,-7} bound {bound,6:0}: {cluster.Count,3} frames within a cell (raw - bound {(cluster.Count == 0 ? "-" : $"min {cluster.Min():0} median {Percentile(cluster, 50):0} max {cluster.Max():0}")}), {d.Count(v => v > Cell),3} beyond it by more than a cell");
            }
            Bound("left", cam.MinX, rawCams.Select(c => c.Cam.X), -1);
            Bound("right", cam.MaxX, rawCams.Select(c => c.Cam.X), 1);
            Bound("top", cam.MinY, rawCams.Select(c => c.Cam.Y), -1);
            Bound("bottom", cam.MaxY, rawCams.Select(c => c.Cam.Y), 1);
            Bound("bot-702", map.Vr.Bottom - 702, rawCams.Select(c => c.Cam.Y), 1);
        }
    }

    // ---- self-consistency --------------------------------------------------------------------------------------------

    private void PrintSelfConsistency(TextWriter o)
    {
        o.WriteLine();
        o.WriteLine("self-consistency: soles predicted from the dot's world position through anchor + clamping (and the walking lag) vs the tag (px)");
        var all = MeasuredFrames();
        // Walking direction from the dot's world x against the previous keyframe with a dot, as the tracker would see it.
        var walking = new Dictionary<Measured, int>();
        Row? prev = null;
        var byRow = all.ToDictionary(m => m.Row);
        foreach (var r in _rows)
        {
            if (byRow.TryGetValue(r, out var m))
                walking[m] = prev is { State.Dot.X: { } px } && prev.State.MapId == r.State.MapId && r.State.Dot is { X: { } x } ? (x - px >= 16 ? 1 : x - px <= -16 ? -1 : 0) : 0;
            if (r.State.Dot is { X: not null }) prev = r;
        }
        void Line(string name, List<Measured> frames, bool lag)
        {
            var errs = frames.Select(m =>
            {
                var cam = new CameraModel(m.Row.State.Map!.Vr);
                var world = m.Row.State.Dot!.Value.Point!.Value;
                var dir = lag ? walking[m] : 0;
                var predicted = cam.PredictScreenFeet(world, dir);
                return (Dist: predicted.DistanceTo(m.Soles), Dx: Math.Abs(predicted.X - m.Soles.X), Dy: Math.Abs(predicted.Y - m.Soles.Y), Clamped: cam.IsClamped(cam.Predict(world, dir)));
            }).ToList();
            if (errs.Count == 0) { o.WriteLine($"  {name}: no frames"); return; }
            List<double> Of(Func<(double Dist, double Dx, double Dy, (bool X, bool Y) Clamped), double> f, Func<(double Dist, double Dx, double Dy, (bool X, bool Y) Clamped), bool>? where = null) =>
                errs.Where(e => where?.Invoke(e) ?? true).Select(f).ToList();
            o.WriteLine($"  {name}{(lag ? " +lag" : "")}: {errs.Count} frames, error median {Percentile(Of(e => e.Dist), 50):0} p90 {Percentile(Of(e => e.Dist), 90):0}; |dx| median {Percentile(Of(e => e.Dx), 50):0} p90 {Percentile(Of(e => e.Dx), 90):0}; |dy| median {Percentile(Of(e => e.Dy), 50):0} p90 {Percentile(Of(e => e.Dy), 90):0}; "
                + $"x clamped {Of(e => e.Dx, e => e.Clamped.X).Count}: |dx| median {Percentile(Of(e => e.Dx, e => e.Clamped.X), 50):0} p90 {Percentile(Of(e => e.Dx, e => e.Clamped.X), 90):0}; "
                + $"y clamped {Of(e => e.Dy, e => e.Clamped.Y).Count}: |dy| median {Percentile(Of(e => e.Dy, e => e.Clamped.Y), 50):0} p90 {Percentile(Of(e => e.Dy, e => e.Clamped.Y), 90):0}");
        }
        foreach (var lag in new[] { false, true })
        {
            Line("all maps", all, lag);
            foreach (var group in all.GroupBy(m => m.Row.State.MapId!.Value)) Line($"  {group.Key} {group.First().Row.State.Map!.MapName}", group.ToList(), lag);
        }
    }

    // ---- portals at map changes ----------------------------------------------------------------------------------------

    private void PrintPortals(TextWriter o, MapCatalog catalog)
    {
        o.WriteLine();
        o.WriteLine("map changes (OCR timeline): last position in the old map vs the portal leading to the new map; first position in the new map vs that portal's target");
        var timeline = new List<(int Id, int First, int Last)>();
        for (var i = 0; i < _rows.Count; i++)
        {
            if (_rows[i].State.MapId is not { } id) continue;
            if (timeline.Count == 0 || timeline[^1].Id != id) timeline.Add((id, i, i));
            else timeline[^1] = (id, timeline[^1].First, i);
        }
        o.WriteLine($"  timeline: {string.Join(" -> ", timeline.Select(t => $"{t.Id} {catalog.Names(t.Id)?.Map}"))}");
        for (var k = 1; k < timeline.Count; k++)
        {
            var (oldId, oldFirst, oldLast) = timeline[k - 1];
            var (newId, newFirst, newLast) = timeline[k];
            var oldMap = catalog.Load(oldId)!;
            var newMap = catalog.Load(newId)!;
            var exits = oldMap.Portals.Where(p => p.TargetMap == newId).ToList();
            var targetNames = exits.Select(p => p.TargetName).ToHashSet();
            var entries = newMap.Portals.Where(p => targetNames.Contains(p.Name)).ToList();
            o.WriteLine($"  {oldId} {oldMap.MapName} -> {newId} {newMap.MapName}   (keyframes {_rows[oldLast].Sequence} -> {_rows[newFirst].Sequence})");

            // Exit: the last frame of the old map with a measured position (not one held through the transition).
            var exit = Enumerable.Range(oldFirst, oldLast - oldFirst + 1).Reverse().Select(i => _rows[i])
                .FirstOrDefault(r => r.State.Position is not null && r.State.Source is not WorldSource.Held);
            o.WriteLine($"      exit:  {Near(exit?.State.Position, exits, exit is null ? "" : $"keyframe {exit.Sequence}, {exit.State.Source}")}");

            // Entry: the first new-map frame with a position; and, since the names lag the panel, the earliest frame
            // that already shows the new panel, its dot re-read with the new map's metadata.
            var entry = Enumerable.Range(newFirst, newLast - newFirst + 1).Select(i => _rows[i]).FirstOrDefault(r => r.State.Position is not null);
            o.WriteLine($"      entry: {Near(entry?.State.Position, entries, entry is null ? "" : $"keyframe {entry.Sequence}, {entry.State.Source}")}");
            var area = _rows[newFirst].Obs.MapArea;
            var early = Enumerable.Range(oldLast + 1, newFirst - oldLast).Select(i => _rows[i]).FirstOrDefault(r => r.Obs.MapArea == area && r.Obs.SelfInMap is not null);
            if (early is not null && area is { } a)
            {
                var t = MinimapTransform.Create(newMap, a);
                var markers = early.Obs.PortalMarkers ?? [];
                var fix = t.ToWorld(early.Obs.SelfInMap!.Value, null, t.EstimateWindow(markers)?.Window);
                o.WriteLine($"      first dot in the new panel: {(fix.Point is { } p ? Near(p, entries, $"keyframe {early.Sequence}, dot only") : $"keyframe {early.Sequence}: x {fix.X:0}, height not observable")}");
            }
        }
    }

    private static string Near(PointD? position, IReadOnlyList<Portal> portals, string label)
    {
        if (position is not { } pos) return "no position";
        if (portals.Count == 0) return $"pos {pos} ({label}): no such portal";
        var best = portals.OrderBy(p => pos.DistanceTo(new PointD(p.X, p.Y))).First();
        return $"pos {pos} ({label}) portal {best.Name} at {best.X},{best.Y}: distance {pos.DistanceTo(new PointD(best.X, best.Y)):0} (dx {pos.X - best.X:0}, dy {pos.Y - best.Y:0})";
    }

    // ---- helpers -------------------------------------------------------------------------------------------------------

    private static string Fraction(int n, int d) => d == 0 ? "-" : $"{n}/{d} = {100.0 * n / d:0.0}%";

    private static double Percentile(IReadOnlyList<double> values, double p)
    {
        if (values.Count == 0) return double.NaN;
        var sorted = values.Order().ToList();
        var rank = p / 100 * (sorted.Count - 1);
        var lo = (int)Math.Floor(rank);
        var hi = (int)Math.Ceiling(rank);
        return sorted[lo] + (sorted[hi] - sorted[lo]) * (rank - lo);
    }

    private static string Mode(IReadOnlyList<double> values) =>
        values.Count == 0 ? "-" : values.GroupBy(v => Math.Round(v)).OrderByDescending(g => g.Count()).First() is var g ? $"{g.Key:0} ({g.Count()}x)" : "-";

    private static string Spread(IReadOnlyList<double> values) =>
        values.Count == 0 ? "-" : $"min {values.Min():0} median {Percentile(values, 50):0} max {values.Max():0}";
}
