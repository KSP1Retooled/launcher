namespace KSP.Retooled.Patch.Patcher;

/// <summary>
/// Where an apply or revert is: <see cref="Done"/> of <see cref="Total"/> (bytes or files) in <see cref="Stage"/>.
/// </summary>
public readonly record struct PatchProgress(string Stage, long Done, long Total);
