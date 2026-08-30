using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace CallTree.Domain.ValueObjects;

/// <summary>
/// A phone number normalized to E.164 (e.g. "+13055551234"). Bare 10-digit and
/// 1-prefixed 11-digit inputs are assumed to be NANP numbers.
/// </summary>
public sealed partial record PhoneNumber
{
    public string Value { get; }

    private PhoneNumber(string value) => Value = value;

    public static PhoneNumber Parse(string input) =>
        TryParse(input, out var number)
            ? number
            : throw new FormatException($"'{input}' is not a valid phone number.");

    public static bool TryParse(string? input, [NotNullWhen(true)] out PhoneNumber? number)
    {
        number = null;
        if (string.IsNullOrWhiteSpace(input))
        {
            return false;
        }

        var digits = new string(input.Where(char.IsAsciiDigit).ToArray());
        var hasCountryCode = input.TrimStart().StartsWith('+');

        var e164Digits = digits switch
        {
            _ when hasCountryCode => digits,
            { Length: 10 } => "1" + digits,
            { Length: 11 } when digits[0] == '1' => digits,
            _ => digits,
        };

        // E.164: max 15 digits, no leading zero; 8 as a pragmatic minimum.
        if (e164Digits.Length is < 8 or > 15 || e164Digits[0] == '0')
        {
            return false;
        }

        number = new PhoneNumber("+" + e164Digits);
        return true;
    }

    /// <summary>
    /// NANP numbers grouped for readability ("+13055551234" -> "(305) 555-1234"); anything else is the
    /// E.164 value unchanged. This feeds the stored default <c>RecordingName</c>, so it deliberately does
    /// not depend on a viewer's locale. The frontend applies the same rule for display
    /// (CallTree.UI/src/lib/format.ts).
    /// </summary>
    public string ToDisplayString()
    {
        var nanp = NanpPattern().Match(Value);
        return nanp.Success
            ? $"({nanp.Groups[1].Value}) {nanp.Groups[2].Value}-{nanp.Groups[3].Value}"
            : Value;
    }

    public override string ToString() => Value;

    [GeneratedRegex(@"^\+1(\d{3})(\d{3})(\d{4})$")]
    private static partial Regex NanpPattern();
}
