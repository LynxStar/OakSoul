namespace OakSoul.World;

/// <summary>A point in world units or screen pixels; which one is always clear from the member name.</summary>
public readonly record struct PointD(double X, double Y)
{
    public static PointD operator +(PointD a, PointD b) => new(a.X + b.X, a.Y + b.Y);
    public static PointD operator -(PointD a, PointD b) => new(a.X - b.X, a.Y - b.Y);
    public double DistanceTo(PointD other) => Math.Sqrt((X - other.X) * (X - other.X) + (Y - other.Y) * (Y - other.Y));
    public override string ToString() => $"{X:0},{Y:0}";
}

/// <summary>An integer pixel rectangle (native screen pixels), independent of the capture layer's types.</summary>
public readonly record struct PixelRect(int X, int Y, int Width, int Height)
{
    public override string ToString() => $"{Width}x{Height}+{X}+{Y}";
}
