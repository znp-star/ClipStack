using System.Text.RegularExpressions;
using ClipStack.Core.Models;

namespace ClipStack.Core.Security;

/// <summary>Why a payload is being kept in memory instead of the database.</summary>
public enum SensitivityReason
{
    None,

    /// <summary>The producer flagged the content with ExcludeClipboardContentFromMonitorProcessing.</summary>
    ProducerOptOut,

    /// <summary>It came from a password manager or a private browser window.</summary>
    Source,

    /// <summary>Its text matches a secret pattern (keys, tokens, card numbers).</summary>
    Content,
}

public readonly record struct SensitivityVerdict(bool IsSensitive, SensitivityReason Reason, string? Detail)
{
    public static readonly SensitivityVerdict Clean = new(false, SensitivityReason.None, null);

    public static SensitivityVerdict Flagged(SensitivityReason reason, string detail) =>
        new(true, reason, detail);
}

/// <summary>
/// Three-layer judgement, evaluated on every clipboard update: the producer's opt-out flag,
/// the source application, then a few high-precision content patterns. Precision matters more
/// than recall here — a wrong hit keeps a harmless entry out of the database for a few minutes,
/// a missed hit writes a password to disk.
/// </summary>
public static class SensitivityFilter
{
    private static readonly string[] BlockedApps =
    [
        "keepass", "1password", "bitwarden", "lastpass", "dashlane", "enpass", "proton pass",
    ];

    private static readonly string[] PrivateBrowsingHints =
    [
        "inprivate", "incognito", "private browsing", "无痕", "隐私浏览", "隐身模式",
    ];

    private static readonly Regex PrivateKeyBlock = new(
        @"-----BEGIN [A-Z0-9 ]*PRIVATE KEY-----",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex AwsAccessKey = new(
        @"\bAKIA[0-9A-Z]{16}\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex GitHubToken = new(
        @"\bgh[pousr]_[A-Za-z0-9]{36,}\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex OpenAiKey = new(
        @"\bsk-[A-Za-z0-9]{32,}\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>13–19 digits, optionally grouped with spaces or dashes, not embedded in a longer number.</summary>
    private static readonly Regex CardCandidate = new(
        @"(?<!\d)(?:\d[ -]?){12,18}\d(?!\d)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static SensitivityVerdict Evaluate(ClipPayload payload)
    {
        if (payload.Excluded)
        {
            return SensitivityVerdict.Flagged(SensitivityReason.ProducerOptOut, "producer opted out");
        }

        if (IsBlockedSource(payload.SourceApp, payload.SourceTitle))
        {
            return SensitivityVerdict.Flagged(SensitivityReason.Source, payload.SourceApp ?? "private window");
        }

        return payload.Type == ClipType.Text && LooksLikeSecret(payload.Text)
            ? SensitivityVerdict.Flagged(SensitivityReason.Content, "secret pattern")
            : SensitivityVerdict.Clean;
    }

    public static bool IsBlockedSource(string? app, string? title)
    {
        if (!string.IsNullOrEmpty(app))
        {
            var name = Path.GetFileNameWithoutExtension(app);
            foreach (var blocked in BlockedApps)
            {
                if (name.Contains(blocked, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        if (string.IsNullOrEmpty(title))
        {
            return false;
        }

        foreach (var hint in PrivateBrowsingHints)
        {
            if (title.Contains(hint, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    public static bool LooksLikeSecret(string? text)
    {
        if (string.IsNullOrEmpty(text) || text.Length < 13)
        {
            return false;
        }

        if (PrivateKeyBlock.IsMatch(text)
            || AwsAccessKey.IsMatch(text)
            || GitHubToken.IsMatch(text)
            || OpenAiKey.IsMatch(text))
        {
            return true;
        }

        foreach (Match candidate in CardCandidate.Matches(text))
        {
            var digits = candidate.Value.Where(char.IsAsciiDigit).ToArray();
            if (digits.Length is >= 13 and <= 19 && PassesLuhn(digits))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Luhn checksum, which is what keeps arbitrary long numbers out of the sensitive queue.</summary>
    private static bool PassesLuhn(char[] digits)
    {
        var sum = 0;
        var double_ = false;

        for (var i = digits.Length - 1; i >= 0; i--)
        {
            var digit = digits[i] - '0';
            if (double_)
            {
                digit *= 2;
                if (digit > 9)
                {
                    digit -= 9;
                }
            }

            sum += digit;
            double_ = !double_;
        }

        return sum % 10 == 0;
    }
}
