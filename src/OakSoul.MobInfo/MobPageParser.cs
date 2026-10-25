using System.Globalization;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;

namespace OakSoul.MobInfo;

/// <summary>
/// Parses a meowdb.com monster page. The site is a server-rendered Next.js app whose CSS classes are
/// utility soup, so nothing here keys on classes. It finds a label element by its exact text and reads the
/// next sibling element (stat cards, definition lists, combat details), and uses the few stable hooks the
/// markup has: <c>data-monster-*</c> attributes and the <c>#locations</c>, <c>#monster-animation-timing</c> ids.
/// Every section is optional; a missing one leaves the field null and is listed in <see cref="ParsedPage.Missing"/>.
/// </summary>
public static partial class MobPageParser
{
    public sealed record ParsedPage(MobRecord Mob, List<string> Missing, string? Problem);

    public static ParsedPage Parse(int id, string html)
    {
        var mob = new MobRecord { Id = id };
        var missing = new List<string>();
        var doc = new HtmlParser().ParseDocument(html);

        mob.SiteName = Clean(doc.QuerySelector("h1")?.TextContent);
        if (mob.SiteName is null)
            missing.Add("siteName");

        mob.Level = Int(ValueAfter(doc, "Level", IsNumber)) ?? Missing(missing, "level");
        mob.Hp = Int(ValueAfter(doc, "HP", IsNumber)) ?? Missing(missing, "hp");
        mob.Exp = Int(ValueAfter(doc, "EXP", IsNumber)) ?? Missing(missing, "exp");
        mob.MagicDamage = Int(ValueAfter(doc, "M.DMG", IsNumber)); // "\u2014" for most mobs, so never reported as missing
        mob.PhysicalDefence = Int(ValueAfter(doc, "P.DEF", IsNumber)) ?? Missing(missing, "physicalDefence");
        mob.MagicDefence = Int(ValueAfter(doc, "M.DEF", IsNumber)) ?? Missing(missing, "magicDefence");
        mob.Accuracy = Int(ValueAfter(doc, "ACC", IsNumber)) ?? Missing(missing, "accuracy");
        mob.Avoid = Int(ValueAfter(doc, "AVOID", IsNumber)) ?? Missing(missing, "avoid");

        var speedCell = doc.QuerySelectorAll("dt").FirstOrDefault(d => Clean(d.TextContent) == "Speed")?.NextElementSibling;
        mob.Speed = FirstSpanText(speedCell);
        if (mob.Speed is null)
            missing.Add("speed");
        var modifier = Regex.Match(speedCell?.TextContent ?? "", @"speed\s*(-?\d+)");
        if (modifier.Success)
            mob.SpeedModifier = Int(modifier.Groups[1].Value);

        string? movement = FirstSpanText(doc.QuerySelector("[data-monster-movement] dd")
            ?? doc.QuerySelectorAll("dt").FirstOrDefault(d => Clean(d.TextContent) == "Movement")?.NextElementSibling);
        if (movement is null)
            missing.Add("mobility");
        mob.Mobility = ClassifyMovement(movement);

        mob.Knockback = Int(Number(ValueAfter(doc, "Knockback", t => t.Any(char.IsDigit)))) ?? Missing(missing, "knockback");
        string? stagger = ValueAfter(doc, "Stagger", t => t.Any(char.IsDigit));
        mob.StaggerSeconds = stagger is not null && Dec().Match(stagger) is { Success: true } sm
            ? double.Parse(sm.Value, CultureInfo.InvariantCulture)
            : MissingD(missing, "staggerSeconds");

        mob.TouchDamage = ParseTouchDamage(doc);
        if (mob.TouchDamage is null)
            missing.Add("touchDamage");

        ParseAttributes(doc, mob);

        mob.Sprite = ParseSize(ValueAfter(doc, "Sprite size", t => Dims().IsMatch(t)));
        if (mob.Sprite is null)
            missing.Add("sprite");
        mob.TouchZone = ParseSize(ValueAfter(doc, "Touch zone", t => Dims().IsMatch(t)));
        if (mob.TouchZone is null)
            missing.Add("touchZone");

        mob.AnimationMs = ParseAnimations(doc);
        if (mob.AnimationMs is null)
            missing.Add("animationMs");

        mob.Maps = ParseMaps(doc, out int? declared, out string? mapProblem);
        if (mob.Maps.Count == 0)
            missing.Add("maps");
        if (mapProblem is not null)
            missing.Add("maps(" + mapProblem + ")");

        // A page with neither a heading nor the core numbers is not a monster page we understand.
        string? problem = mob.SiteName is null && mob.Level is null && mob.Hp is null
            ? "no monster heading, level or HP found"
            : null;
        _ = declared;
        return new ParsedPage(mob, missing, problem);
    }

    public static string ClassifyMovement(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "unknown";
        string t = text.ToLowerInvariant();
        if (t.Contains("fly") || t.Contains("flies") || t.Contains("flying") || t.Contains("float") || t.Contains("hover"))
            return "fly";
        if (t.Contains("jump") || t.Contains("between floors") || t.Contains("hop"))
            return "jump";
        if (t.Contains("stays on") || t.Contains("its floor") || t.Contains("floor"))
            return "floor";
        return "unknown";
    }

    private static TouchDamage? ParseTouchDamage(IDocument doc)
    {
        var label = FindLeaves(doc, "Touch DMG").FirstOrDefault(l => l.NextElementSibling is { } v && v.TextContent.Any(char.IsDigit));
        if (label?.NextElementSibling is not { } value)
            return null;
        var range = Regex.Match(value.TextContent, @"(\d[\d,]*)\s*[-–]\s*(\d[\d,]*)");
        int? min = null, max = null, bas = null;
        if (range.Success)
        {
            min = Int(range.Groups[1].Value);
            max = Int(range.Groups[2].Value);
        }
        else
        {
            var single = Regex.Match(value.TextContent, @"\d[\d,]*");
            if (single.Success)
                min = max = Int(single.Value);
        }
        string haystack = (value.NextElementSibling?.TextContent ?? "") + " " + (label.ParentElement?.GetAttribute("title") ?? "");
        var b = Regex.Match(haystack, @"Base(?:\s+P\.DMG:?)?\s*(\d[\d,]*)");
        if (b.Success)
            bas = Int(b.Groups[1].Value);
        return min is null && max is null && bas is null ? null : new TouchDamage(min, max, bas);
    }

    /// <summary>"Fire weak" or "Holy weak: 25% extra dmg".</summary>
    private static readonly Regex ElementItem = new(@"^(?<e>.+?)\s+(?<r>strong|weak|immune)(?::\s*(?<n>.+))?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>"Other Attributes" list: element items become <see cref="MobRecord.Elements"/>; anything else (e.g. "MP 30", "Undead") is kept verbatim.</summary>
    private static void ParseAttributes(IDocument doc, MobRecord mob)
    {
        foreach (var li in doc.QuerySelectorAll("[data-monster-attributes] li"))
        {
            string? text = Clean(li.TextContent);
            if (text is null)
                continue;
            var m = ElementItem.Match(text);
            if (m.Success)
            {
                var e = new ElementEffect(m.Groups["e"].Value, m.Groups["r"].Value.ToLowerInvariant(), m.Groups["n"].Success ? m.Groups["n"].Value : null);
                if (!mob.Elements.Contains(e))
                    mob.Elements.Add(e);
            }
            else if (!mob.OtherAttributes.Contains(text))
                mob.OtherAttributes.Add(text);
        }
    }

    private static SortedDictionary<string, int>? ParseAnimations(IDocument doc)
    {
        var section = doc.QuerySelector("#monster-animation-timing");
        if (section is null)
            return null;
        var result = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (var e in section.QuerySelectorAll("*"))
        {
            if (e.ChildElementCount != 0)
                continue;
            var m = Ms().Match(Clean(e.TextContent) ?? "");
            if (!m.Success || e.PreviousElementSibling is not { } prev)
                continue;
            string? name = Clean(prev.TextContent);
            if (name is null || name.Contains(':') || name.Any(char.IsDigit))
                continue;
            result.TryAdd(name, int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture));
        }
        return result.Count == 0 ? null : result;
    }

    private static List<MobMap> ParseMaps(IDocument doc, out int? declared, out string? problem)
    {
        problem = null;
        declared = null;
        var result = new List<MobMap>();
        var section = doc.QuerySelector("#locations");
        if (section is null)
            return result;
        var heading = Regex.Match(section.QuerySelector("h2")?.TextContent ?? "", @"\((\d+)\)");
        if (heading.Success)
            declared = int.Parse(heading.Groups[1].Value, CultureInfo.InvariantCulture);

        var table = section.QuerySelector("table");
        if (table is null)
            return result;
        var columns = table.QuerySelectorAll("thead th").Select(th => Clean(th.TextContent) ?? "").ToList();
        int Col(string name) => columns.FindIndex(c => c.StartsWith(name, StringComparison.OrdinalIgnoreCase));
        int cCount = Col("Count"), cRate = Col("Mob Rate"), cResp = Col("Respawn");
        if (cCount < 0 || cRate < 0 || cResp < 0)
            problem = "unrecognised table header: " + string.Join("|", columns);

        foreach (var tr in table.QuerySelectorAll("tbody tr"))
        {
            var cells = tr.Children.Where(c => c.LocalName == "td").ToList();
            var link = cells.FirstOrDefault()?.QuerySelector("a[href*='/maps/']");
            var href = Regex.Match(link?.GetAttribute("href") ?? "", @"/maps/(\d+)");
            if (!href.Success)
                continue;
            string? Cell(int i) => i >= 0 && i < cells.Count ? Clean(cells[i].TextContent) : null;
            var street = cells[0].QuerySelector("span");
            string? respText = Cell(cResp);
            var respawn = ParseRespawn(respText);
            result.Add(new MobMap(
                int.Parse(href.Groups[1].Value, CultureInfo.InvariantCulture), // strips leading zeros
                Clean(link!.TextContent),
                Clean(street?.TextContent),
                Int(Cell(cCount)),
                Dbl(Cell(cRate)),
                respawn.Min ?? respawn.Wave, respawn.Min, respawn.Max, respawn.Wave, respText));
        }

        if (declared is int d && d != result.Count)
            problem = $"heading says {d}, table has {result.Count}";
        return result;
    }

    /// <summary>
    /// "1m + ~7.5s" -> individual 60..60, wave 7.5; "20s-40s" -> individual 20..40. A "~" prefix marks the shared wave timer.
    /// </summary>
    private static (double? Min, double? Max, double? Wave) ParseRespawn(string? text)
    {
        double? min = null, max = null, wave = null;
        foreach (Match m in RespawnToken().Matches(text ?? ""))
        {
            double lo = Seconds(m.Groups[2].Value, m.Groups[3].Value);
            double hi = m.Groups[4].Success ? Seconds(m.Groups[4].Value, m.Groups[5].Value) : lo;
            if (m.Groups[1].Success)
                wave ??= lo;
            else if (min is null)
            {
                min = lo;
                max = hi;
            }
        }
        return (min, max, wave);
    }

    private static double Seconds(string number, string unit) =>
        double.Parse(number, CultureInfo.InvariantCulture) * unit switch { "h" => 3600, "m" => 60, _ => 1 };

    [GeneratedRegex(@"(~)?\s*(\d+(?:\.\d+)?)(h|m|s)\b(?:\s*-\s*(\d+(?:\.\d+)?)(h|m|s)\b)?")]
    private static partial Regex RespawnToken();

    // ---- helpers -------------------------------------------------------------------------------------

    private static IEnumerable<IElement> FindLeaves(IParentNode scope, string label) =>
        scope.QuerySelectorAll("*").Where(e => e.ChildElementCount == 0 && Clean(e.TextContent) == label);

    /// <summary>Text of the element following the first leaf labelled <paramref name="label"/> whose value passes <paramref name="accept"/>.</summary>
    private static string? ValueAfter(IParentNode scope, string label, Func<string, bool> accept)
    {
        foreach (var leaf in FindLeaves(scope, label))
            if (Clean(leaf.NextElementSibling?.TextContent) is { } text && accept(text))
                return text;
        return null;
    }

    /// <summary>
    /// Main text of a value cell. The site writes either <c>Slow&lt;span&gt;(speed -50)&lt;/span&gt;</c> (own text, then a hint)
    /// or <c>&lt;span&gt;Stays on its floor&lt;/span&gt;&lt;span&gt;hint&lt;/span&gt;</c>; take the cell's own text if it has any, else its first child.
    /// </summary>
    private static string? FirstSpanText(IElement? cell)
    {
        if (cell is null)
            return null;
        string own = string.Concat(cell.ChildNodes.OfType<IText>().Select(t => t.Data));
        return Clean(own) ?? Clean(cell.Children.FirstOrDefault()?.TextContent) ?? Clean(cell.TextContent);
    }

    private static string? Clean(string? s)
    {
        if (s is null)
            return null;
        s = Whitespace().Replace(s.Replace(' ', ' '), " ").Trim();
        return s.Length == 0 || s == "—" ? null : s;
    }

    private static bool IsNumber(string t) => Regex.IsMatch(t, @"^[\d,]+$");

    private static string? Number(string? t) => t is null ? null : Regex.Match(t, @"\d[\d,]*").Value is { Length: > 0 } v ? v : null;

    private static int? Int(string? s) =>
        s is not null && int.TryParse(s.Replace(",", ""), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : null;

    private static double? Dbl(string? s)
    {
        if (s is null)
            return null;
        var m = Dec().Match(s);
        return m.Success ? double.Parse(m.Value, CultureInfo.InvariantCulture) : null;
    }

    private static Size? ParseSize(string? s)
    {
        if (s is null || Dims().Match(s) is not { Success: true } m)
            return null;
        return new Size(int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture));
    }

    private static int? Missing(List<string> missing, string field)
    {
        missing.Add(field);
        return null;
    }

    private static double? MissingD(List<string> missing, string field)
    {
        missing.Add(field);
        return null;
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"\d+(?:\.\d+)?")]
    private static partial Regex Dec();

    [GeneratedRegex(@"(\d+)\s*[x×]\s*(\d+)")]
    private static partial Regex Dims();

    [GeneratedRegex(@"^(\d+)\s*ms$")]
    private static partial Regex Ms();
}
