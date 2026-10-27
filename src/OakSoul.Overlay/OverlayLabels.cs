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
    /// "#116 Dark Stump move/3 R 0.97" for a mob matched in this frame (" frozen", " steam" when tinted, " vs Axe Stump 0.90"
    /// when the contest's rival came close); "~" after the score when the identity is carried by the track; "#116 Mob? Lv23
    /// Dark Axe Stump" for a mob nothing verified; "#12 meso 09000000 1.00" for an identified drop, "#12 Drop?" otherwise;
    /// "#7 Pet Mara", "#9 Player KOOKAW", "#3 Effect" for the rest.
    /// </summary>
    public static string Entity(OverlayItem e)
    {
        var track = e.Long("track");
        var kind = Title(e.Kind);
        var evidence = e.Str("evidence");
        if (e.Kind == "mob" && e.Str("mob") is not null)
        {
            var score = e.Double("score") ?? 0;
            var text = $"#{track} {e.Str("name")} {e.Str("anim")}/{e.Long("frame")} {e.Str("facing")} {score.ToString("0.00", CultureInfo.InvariantCulture)}";
            if (evidence == "carried") text += "~";
            if (e.Str("tint") is { } tint && tint != "drawn") text += " " + tint;
            if (e.Str("rivalName") is { } rival && e.Double("rivalScore") is { } rs && rs >= score - CloseRival)
                text += $" vs {rival} {rs.ToString("0.00", CultureInfo.InvariantCulture)}";
            return text;
        }
        if (e.Kind == "drop")
            return e.Str("item") is { } item ? $"#{track} {(e.Str("category") == "meso" ? "meso" : "item")} {item} {(e.Double("score") ?? 0).ToString("0.00", CultureInfo.InvariantCulture)}" : $"#{track} Drop?";
        return $"#{track} {kind}{(e.Kind == "mob" ? "?" : "")}{NameText(e)}";
    }

    /// <summary>The contact sheets' label, as <c>EntityDetector.Label</c>: "#12 Red Snail move/2R" ("~" carried), "#12 Mob? Lv23 Name", "#12 meso 09000000".</summary>
    public static string Sheet(OverlayItem e)
    {
        var track = e.Long("track");
        if (e.Kind == "mob" && e.Str("mob") is not null)
            return $"#{track} {e.Str("name")} {e.Str("anim")}/{e.Long("frame")}{e.Str("facing")}{(e.Str("evidence") == "carried" ? "~" : "")}";
        if (e.Kind == "drop" && e.Str("item") is { } item) return $"#{track} {(item.StartsWith("0900000", StringComparison.Ordinal) ? "meso" : "item")} {item}";
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
