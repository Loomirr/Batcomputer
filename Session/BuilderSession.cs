namespace Batcomputer;

/// <summary>
/// The single source of truth for the currently open build session.
/// It owns the current <see cref="NativeSuitProject"/> and raises ONE change notification when
/// the project's state is mutated, so every view (Your Character, Toybox, Inspector) can refresh
/// from the same authoritative snapshot instead of each mutation site calling an ad-hoc set of
/// refresh methods.
///
/// This first slice establishes ownership + the notification spine only; mutation sites are
/// migrated onto <see cref="RaiseChanged"/> incrementally in later slices so each step stays
/// behavior-preserving and testable.
/// </summary>
public sealed class BuilderSession
{
    private NativeSuitProject? _project;

    /// <summary>The open project, or null when no suit is loaded.</summary>
    public NativeSuitProject? Project
    {
        get => _project;
        set => _project = value;
    }

    /// <summary>Raised after the project's state changes and views should refresh from the snapshot.</summary>
    public event EventHandler? Changed;

    /// <summary>Signals that the session state changed; subscribers refresh from the current snapshot.</summary>
    public void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);
}
