namespace OakSoul.Detect;

/// <summary>The map's drifting clouds (<see cref="CloudLayer"/>), followed every frame before any sprite is compared.</summary>
public sealed partial class EntityDetector
{
    /// <summary>
    /// The clouds of a map whose info/cloud is 1, set by the caller when the cloud art was exported; null on other maps. The
    /// matcher then expects a sprite pixel under clouds as the clouds' blend over its art.
    /// </summary>
    public CloudLayer? Clouds
    {
        get => _clouds;
        set { _clouds = value; _matcher.Clouds = value; }
    }

    private CloudLayer? _clouds;

    private long _traceSequence;
    private static readonly bool _traceClouds = Environment.GetEnvironmentVariable("OAKSOUL_CLOUD_TRACE") is "1";

    /// <summary>Follows the clouds into this frame (registered camera, foreground classes of this frame), timed apart from the stages.</summary>
    private void UpdateClouds(ReadOnlySpan<byte> frame, int frameWidth, DetectionContext ctx, int camX, int camY)
    {
        if (_clouds is null) return;
        // The clouds drift in the game's time, which the capture's pts follow.
        if (_traceClouds && _clouds.Trace is null) _clouds.Trace = line => Console.WriteLine($"clouds {_traceSequence}: {line}");
        _traceSequence = ctx.Sequence;
        _clouds.Update(frame, frameWidth, _mask, _mosaic, camX, camY, ctx.Pts ?? ctx.Time, _fg.Classes);
        if (_search is not null) _search.Clouds = _clouds;
        if (_traceClouds) Console.WriteLine($"clouds {ctx.Sequence} cam {camX},{camY}: {string.Join("; ", _clouds.Placed.Select(c => c.ToString()))}");
    }

    /// <summary>
    /// Whether screen pixel (x, y), colour <paramref name="screen"/>, shows an art colour through the clouds over it: for the
    /// probes that look at the screen directly (a probe through clouds tries the colour as drawn; the strict score after it
    /// knows the freeze too).
    /// </summary>
    private bool CloudAgrees(int screen, int colour, int x, int y) =>
        _clouds is { } cl && cl.MayCover(x, y) && screen == cl.CompositeAt(colour, x, y);

    /// <summary>
    /// What the fog may be hiding or altering in this frame, in world coordinates: the mobs no sprite verified and the mobs a
    /// sprite verified only with pixels through steam (an unmodelled light layer). The next frames look for clouds only
    /// around them: a cloud anywhere else hides nothing the detector misses, and a discovery costs milliseconds.
    /// </summary>
    private void CloudInterest(IReadOnlyList<DetectedEntity> entities, DetectionContext ctx, int camX, int camY)
    {
        if (_clouds is null) return;
        if (_search is not null) _search.Clouds = _clouds;
        _clouds.Interest(entities.Where(e => e.Kind == EntityKind.Mob && (e.Identity is not { Verified: true } || e.Sprite is { Fogged: > 0 }))
            .Select(e => (e.Box.X + camX, e.Box.Y + camY, e.Box.X + e.Box.Width - 1 + camX, e.Box.Y + e.Box.Height - 1 + camY)), ctx.Pts ?? ctx.Time);
    }
}
