namespace OakSoul.Vision.Readers;

/// <summary>HP, MP and EXP as read from the status bar in one frame.</summary>
public sealed record StatusReading(BarReading Hp, BarReading Mp, BarReading Exp, NumberReading HpText, NumberReading MpText, NumberReading ExpText)
{
    public (long Current, long Maximum)? HpValue => HpText.Fraction;
    public (long Current, long Maximum)? MpValue => MpText.Fraction;
    public (long Total, double Percent)? ExpValue => ExpText.Exp;

    public override string ToString() => $"HP {HpText.Text} bar {Hp}  MP {MpText.Text} bar {Mp}  EXP {ExpText.Text} bar {Exp}";
}

/// <summary>Reads the three status bars from a frame, using the layout's regions and the frame's geometry.</summary>
public static class StatusReader
{
    public static StatusReading Read(FrameGeometry geometry, ReadOnlySpan<byte> frame, GlyphFont? font = null)
    {
        var layout = geometry.Layout;
        var hp = geometry.ExtractNative(frame, layout["status.hp"]);
        var mp = geometry.ExtractNative(frame, layout["status.mp"]);
        var exp = geometry.ExtractNative(frame, layout["status.exp"]);
        int hw = layout["status.hp"].Width, mw = layout["status.mp"].Width, ew = layout["status.exp"].Width;
        int hh = layout["status.hp"].Height, mh = layout["status.mp"].Height, eh = layout["status.exp"].Height;
        return new StatusReading(
            BarReader.Read(hp, hw, hh), BarReader.Read(mp, mw, mh), BarReader.Read(exp, ew, eh),
            StatusNumberReader.Read(hp, hw, font), StatusNumberReader.Read(mp, mw, font), StatusNumberReader.Read(exp, ew, font));
    }
}
