using System.Globalization;

namespace OakSoul.Overlay;

/// <summary>
/// The text that names an entity item, built from its fields only, so a label read off a baked frame can be matched to
/// the item it names. <see cref="Entity"/> is the stream's label; <see cref="Sheet"/> the contact sheets' shorter one
/// (as <c>EntityDetector.Label</c> draws it).
/// </summary>
public static class OverlayLabels
{
    /// <summary>A rival species is named in the label when its score is within this much of the winner's.</summary>
    public const double CloseRival = 0.15;

    /// <summary>
    /// "#116 Dark Stump move/3 R 0.97 [p]" for a mob matched in this frame (" frozen", " steam" when tinted, " vs Axe Stump
    /// 0.90" when the contest's rival came close, " dying", then how it was found: [p] prediction, [t] transition, [s] search); "#28 Dark Stump ~carried 1.5 s (last move/0 R 1.00 at 23.0)" when the
    /// identity is carried by the track (its last match's pose, score and time, not a score of this frame); "#116 Mob? Lv23
    /// Dark Axe Stump" for a mob nothing verified; "#12 meso 09000000 1.00" for an identified drop, "#12 Drop?" otherwise;
    /// "#173 dmg 287 dealt", "#174 dmg 119 taken fading 0.79" for damage numbers; "#7 Pet Mara", "#9 Player KOOKAW", "#3
    /// Effect" for the rest.
    /// </summary>
    public static string Entity(OverlayItem e)
    {
        var track = e.Long("track");
        var kind = Title(e.Kind);
        var evidence = e.Str("evidence");
        var dying = e.Str("state") == "dying" ? " dying" : "";
        if (e.Kind == "mob" && e.Str("mob") is not null)
        {
            if (evidence == "carried")
            {
                var last = e.Double("lastScore") ?? 0;
                return $"#{track} {e.Str("name")} ~carried {(e.Double("carriedFor") ?? 0).ToString("0.0", CultureInfo.InvariantCulture)} s (last {e.Str("anim")}/{e.Long("frame")} {e.Str("facing")} {last.ToString("0.00", CultureInfo.InvariantCulture)}" +
                       $" at {(e.Double("lastMatchedAt") ?? 0).ToString("0.0", CultureInfo.InvariantCulture)}){dying}";
            }
            var score = e.Double("score") ?? 0;
            var text = $"#{track} {e.Str("name")} {e.Str("anim")}/{e.Long("frame")} {e.Str("facing")} {score.ToString("0.00", CultureInfo.InvariantCulture)}";
            if (e.Str("tint") is { } tint && tint != "drawn") text += " " + tint;
            if (e.Str("rivalName") is { } rival && e.Double("rivalScore") is { } rs && rs >= score - CloseRival)
                text += $" vs {rival} {rs.ToString("0.00", CultureInfo.InvariantCulture)}";
            return text + dying + FoundMark(e);
        }
        if (e.Kind == "drop")
            return e.Str("item") is { } item ? $"#{track} {(e.Str("category") == "meso" ? "meso" : "item")} {item} {(e.Double("score") ?? 0).ToString("0.00", CultureInfo.InvariantCulture)}" : $"#{track} Drop?";
        if (e.Kind == "damage")
            return $"#{track} dmg {(e.Bool("complete") ? "" : "?")}{e.Str("digits")} {e.Str("direction")}{(e.Bool("critical") ? " crit" : "")}" +
                   $"{(e.Double("alpha") is { } a && a < 0.95 ? $" fading {a.ToString("0.00", CultureInfo.InvariantCulture)}" : "")}";
        return $"#{track} {kind}{(e.Kind == "mob" ? "?" : "")}{NameText(e)}";
    }

    /// <summary>
    /// How a mob matched in its frame was found, briefly: " [p]" its track's predicted pose verified, " [t]" a transition of
    /// it verified, " [s]" the full search (the stream's <c>found</c>).
    /// </summary>
    public static string FoundMark(OverlayItem e) => e.Str("found") switch
    {
        "prediction" => " [p]",
        "transition" => " [t]",
        "search" => " [s]",
        _ => "",
    };

    /// <summary>The contact sheets' label, as <c>EntityDetector.Label</c>: "#12 Red Snail move/2R" ("~" carried), "#12 Mob? Lv23 Name", "#12 meso 09000000".</summary>
    public static string Sheet(OverlayItem e)
    {
        var track = e.Long("track");
        if (e.Kind == "mob" && e.Str("mob") is not null)
            return $"#{track} {e.Str("name")} {e.Str("anim")}/{e.Long("frame")}{e.Str("facing")}{(e.Str("evidence") == "carried" ? "~" : "")}";
        if (e.Kind == "drop" && e.Str("item") is { } item) return $"#{track} {(item.StartsWith("0900000", StringComparison.Ordinal) ? "meso" : "item")} {item}";
        if (e.Kind == "damage") return $"#{track} dmg {(e.Bool("complete") ? "" : "?")}{e.Str("digits")} {e.Str("direction")}";
        return $"#{track} {Title(e.Kind)}{(e.Kind == "mob" ? "?" : "")}{SheetName(e)}";
    }

    /// <summary>" Lv23 Name" from the entity's own name (the tag-learned one when an identity named the species).</summary>
    private static string NameText(OverlayItem e)
    {
        var name = e.Str("tagName") ?? e.Str("name");
        if (name is null) return "";
        var level = e.Long("level");
        return " " + (level is { } lv ? lv > 0 ? $"Lv{lv} " : "Lv? " : "") + name;
    }

    /// <summary>The sheet label's name part: the entity's name as the tracker gives it (its tags' name, else the species).</summary>
    private static string SheetName(OverlayItem e) => NameText(e);

    public static string Title(string kind) => kind.Length == 0 ? kind : char.ToUpperInvariant(kind[0]) + kind[1..];
}
