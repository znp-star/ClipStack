namespace ClipStack.App;

/// <summary>One entry of the panel's shortcut row: the key cap and what the key does.</summary>
public sealed class HotkeyHint
{
    public HotkeyHint(string keys, string label)
    {
        Keys = keys;
        Label = label;
    }

    /// <summary>Rendered inside the key cap, e.g. "Ctrl+P".</summary>
    public string Keys { get; }

    public string Label { get; }
}
