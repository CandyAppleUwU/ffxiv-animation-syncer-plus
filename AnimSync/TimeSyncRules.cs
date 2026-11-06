using System;
using System.Linq;
using System.Text;

namespace AnimSync;

internal static class TimeSyncRules {
    public const int SenderDelayMs = 300;
    public const int RepeatDelayMs = 2000;
    public static bool ValidPhrase(string? phrase) => !string.IsNullOrWhiteSpace(phrase) &&
        Encoding.UTF8.GetByteCount(phrase) <= 180 && !phrase.Any(char.IsControl) &&
        !phrase.Contains('<') && !phrase.Contains('>'); // Prevent game chat placeholder expansion.
    public static bool Matches(string phrase, string text) => ValidPhrase(phrase) &&
        string.Equals(phrase.Trim(), text.Trim(), StringComparison.Ordinal);
    public static long Due(long received, bool outgoing) => received + (outgoing ? SenderDelayMs : 0);
}
