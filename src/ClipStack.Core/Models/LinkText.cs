using System.Text.RegularExpressions;

namespace ClipStack.Core.Models;

/// <summary>
/// Recognises clipboard text that is nothing but a single link. Deliberately strict: a note
/// that merely mentions a URL stays plain text, so the link filter really means "pasteable
/// address" rather than "contains a link somewhere".
/// </summary>
public static partial class LinkText
{
    [GeneratedRegex(@"^(?:https?://|www\.)\S+$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LinkPattern();

    public static bool Is(string? text) => !string.IsNullOrWhiteSpace(text) && LinkPattern().IsMatch(text.Trim());
}
