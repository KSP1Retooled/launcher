namespace KSP.Retooled.Launcher;

/// <summary>
/// What the launcher is doing: <see cref="Done"/> of <see cref="Total"/> (0 when unknown) in <see cref="Stage"/>.
/// </summary>
public readonly record struct LauncherProgress(string Stage, long Done = 0, long Total = 0)
{
    public double? Fraction => this.Total > 0 ? Math.Clamp((double)this.Done / this.Total, 0, 1) : null;
}
