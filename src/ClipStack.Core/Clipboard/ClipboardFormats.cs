using ClipStack.Core.Interop;

namespace ClipStack.Core.Clipboard;

internal static class ClipboardFormats
{
    /// <summary>
    /// Custom format we put on the clipboard right before writing our own content,
    /// so the monitor can ignore the resulting clipboard update (no feedback loop).
    /// </summary>
    public const string IgnoreMarkerName = "ClipStack.IgnoreNext";

    private static readonly Lazy<uint> IgnoreMarkerId = new(() => NativeMethods.RegisterClipboardFormatW(IgnoreMarkerName));
    private static readonly Lazy<uint> HtmlId = new(() => NativeMethods.RegisterClipboardFormatW("HTML Format"));
    private static readonly Lazy<uint> RtfId = new(() => NativeMethods.RegisterClipboardFormatW("Rich Text Format"));
    private static readonly Lazy<uint> PngId = new(() => NativeMethods.RegisterClipboardFormatW("PNG"));

    public static uint IgnoreMarker => IgnoreMarkerId.Value;
    public static uint Html => HtmlId.Value;
    public static uint Rtf => RtfId.Value;
    public static uint Png => PngId.Value;
}
