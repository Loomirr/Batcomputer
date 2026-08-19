namespace Batcomputer;

/// <summary>
/// Shared owner of the current character-component selection.
/// Both "Your Character" and the Inspector read from here and can subscribe to
/// <see cref="SelectionChanged"/>, so the selected row and the Inspector always agree without
/// MainForm hand-coordinating them. This first slice establishes ownership + the event; wiring
/// views onto it happens as each is extracted.
/// </summary>
public sealed class SelectionController
{
    /// <summary>The selected component's mesh/component name (e.g. "CharacterMesh0", "Head_2").</summary>
    public string Component { get; private set; } = "CharacterMesh0";

    /// <summary>The selected 0-based slot within that component.</summary>
    public int Slot { get; private set; }

    /// <summary>Friendly label for the selection (e.g. "Body", "Head / cowl").</summary>
    public string Label { get; private set; } = "Body";

    /// <summary>Raised after the selection changes.</summary>
    public event EventHandler? SelectionChanged;

    /// <summary>Sets the current selection and notifies subscribers.</summary>
    public void Select(string label, string component, int slot)
    {
        Label = label;
        Component = component;
        Slot = slot;
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }
}
