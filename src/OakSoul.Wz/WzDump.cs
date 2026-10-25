namespace OakSoul.Wz;

/// <summary>Debug helpers that print archive and image trees as indented text.</summary>
public static class WzDump
{
    /// <summary>Writes a node and its descendants, one per line, indented two spaces per level.</summary>
    public static void Dump(WzNode node, TextWriter writer, int maxDepth = int.MaxValue, int maxChildren = int.MaxValue)
    {
        DumpNode(node, writer, 0, maxDepth, maxChildren);
    }

    /// <summary>Returns <see cref="Dump(WzNode, TextWriter, int, int)"/> output as a string.</summary>
    public static string Dump(WzNode node, int maxDepth = int.MaxValue, int maxChildren = int.MaxValue)
    {
        var sw = new StringWriter();
        Dump(node, sw, maxDepth, maxChildren);
        return sw.ToString();
    }

    /// <summary>Writes a directory tree (directories and image names with sizes), without parsing images.</summary>
    public static void Dump(WzDirectory dir, TextWriter writer, int maxDepth = int.MaxValue, int maxEntries = int.MaxValue)
    {
        DumpDirectory(dir, writer, 0, maxDepth, maxEntries);
    }

    private static void DumpNode(WzNode node, TextWriter w, int depth, int maxDepth, int maxChildren)
    {
        w.Write(new string(' ', depth * 2));
        w.WriteLine(node.ToString());
        if (depth >= maxDepth)
            return;
        int shown = 0;
        foreach (var child in node.Children)
        {
            if (shown++ == maxChildren)
            {
                w.WriteLine($"{new string(' ', (depth + 1) * 2)}... {node.Children.Count - maxChildren} more");
                break;
            }
            DumpNode(child, w, depth + 1, maxDepth, maxChildren);
        }
    }

    private static void DumpDirectory(WzDirectory dir, TextWriter w, int depth, int maxDepth, int maxEntries)
    {
        string pad = new(' ', depth * 2);
        w.WriteLine($"{pad}{(dir.Parent is null ? "/" : dir.Name + "/")}{(dir.IsExternal ? " (external)" : "")}");
        if (depth >= maxDepth)
            return;
        foreach (var sub in dir.Directories)
            DumpDirectory(sub, w, depth + 1, maxDepth, maxEntries);
        int shown = 0;
        foreach (var img in dir.Images)
        {
            if (shown++ == maxEntries)
            {
                w.WriteLine($"{pad}  ... {dir.Images.Count - maxEntries} more images");
                break;
            }
            w.WriteLine($"{pad}  {img.Name} ({img.Size} bytes)");
        }
    }
}
