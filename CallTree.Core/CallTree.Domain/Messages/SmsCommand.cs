using System.Diagnostics.CodeAnalysis;
using System.Text;
using CallTree.Domain.ValueObjects;

namespace CallTree.Domain.Messages;

/// <summary>
/// A "send this text to that number" instruction, texted to the DID from the operator's own mobile in
/// the form <c>{RECIPIENT-NUMBER} Body of text</c>.
/// </summary>
/// <remarks>
/// <para>
/// The recipient is read a whitespace-delimited token at a time, because that is the only boundary the
/// input actually offers: a number may be written with spaces inside it ("+1 305 555 1234") *and* be
/// followed by a body that starts with digits ("305-555-1234 42 is the answer"). Testing only at token
/// boundaries is what keeps those two apart — a scan that stopped at the first substring which happened
/// to parse would cut "+13055551234" short at "+1305555123", which
/// <see cref="PhoneNumber.TryParse(string?, out PhoneNumber?)"/> accepts quite happily.
/// </para>
/// <para>
/// Two rules end the number, in this order:
/// </para>
/// <list type="number">
/// <item>
/// A <b>canonical</b> length — 10 digits, or 11 beginning with 1 — ends it immediately, whatever
/// follows. This is what makes "305-555-1234 42 is the answer" send "42 is the answer" rather than
/// dialling twelve digits.
/// </item>
/// <item>
/// Otherwise the <b>end of the run</b> of number-shaped tokens ends it, which is what lets an
/// international number be typed with spaces in it ("+44 79 1112 3456 hi").
/// </item>
/// </list>
/// <para>
/// One ambiguity is left deliberately unresolved: a non-NANP <c>+</c> number followed by a body that
/// starts with a bare number ("+44 7911 123456 42 apples") reads the "42" as part of the recipient and
/// then fails to parse. Resolving it would need a country-by-country length table, and the failure is
/// loud — the operator gets the usage text back — rather than a message sent to the wrong number.
/// </para>
/// </remarks>
public sealed record SmsCommand(PhoneNumber Recipient, string Body)
{
    /// <summary>
    /// How many tokens may be swallowed looking for the number. A body of nothing but digits should
    /// fail as unparseable rather than be silently eaten looking for a fifteenth digit.
    /// </summary>
    private const int MaxTokens = 8;

    /// <summary>What to text back when the command could not be read. Kept here beside the grammar.</summary>
    public const string Usage = "To send a text, start the message with the number: 3055551234 Your message here.";

    /// <summary>
    /// Reads a send command. On failure <paramref name="error"/> is a sentence fit to text straight
    /// back to the operator — this is the only channel the phone has for saying what went wrong.
    /// </summary>
    public static bool TryParse(string? input, [NotNullWhen(true)] out SmsCommand? command, out string error)
    {
        command = null;

        var text = (input ?? "").Trim();
        if (text.Length == 0)
        {
            error = "The message was empty.";
            return false;
        }

        var hasPlus = text[0] == '+';
        var digits = new StringBuilder();
        var index = 0;

        for (var consumed = 0; consumed < MaxTokens; consumed++)
        {
            if (!TryReadToken(text, ref index, out var token))
            {
                break;
            }

            if (!IsNumberToken(token))
            {
                break;
            }

            foreach (var character in token)
            {
                if (char.IsAsciiDigit(character))
                {
                    digits.Append(character);
                }
            }

            // E.164's own ceiling. Past it nothing can parse, so stop rather than eat the whole message.
            if (digits.Length > 15)
            {
                break;
            }

            var candidate = (hasPlus ? "+" : "") + digits;

            if ((IsCanonicalLength(digits, hasPlus) || IsEndOfNumberRun(text, index))
                && PhoneNumber.TryParse(candidate, out var recipient))
            {
                var body = text[index..].Trim();
                if (body.Length == 0)
                {
                    error = $"There was nothing to send after {recipient.ToDisplayString()}.";
                    return false;
                }

                command = new SmsCommand(recipient, SmsText.Truncate(body));
                error = "";
                return true;
            }
        }

        error = $"No recipient number was found at the start of the message. {Usage}";
        return false;
    }

    /// <summary>Reads the next whitespace-delimited token, leaving <paramref name="index"/> after it.</summary>
    private static bool TryReadToken(string text, ref int index, out ReadOnlySpan<char> token)
    {
        while (index < text.Length && char.IsWhiteSpace(text[index]))
        {
            index++;
        }

        var start = index;
        while (index < text.Length && !char.IsWhiteSpace(text[index]))
        {
            index++;
        }

        token = text.AsSpan(start, index - start);
        return token.Length > 0;
    }

    /// <summary>
    /// Whether a token could be part of a written phone number: only the punctuation people actually
    /// use, and at least one digit so a stray bracket or dash cannot extend the run on its own.
    /// </summary>
    private static bool IsNumberToken(ReadOnlySpan<char> token)
    {
        var sawDigit = false;

        foreach (var character in token)
        {
            if (char.IsAsciiDigit(character))
            {
                sawDigit = true;
            }
            else if (character is not ('+' or '(' or ')' or '-' or '.' or '/'))
            {
                return false;
            }
        }

        return sawDigit;
    }

    /// <summary>
    /// A complete NANP number, which ends the recipient whatever follows it. There is no equivalent for
    /// the rest of the world without a per-country length table, so those fall back to the end of the run.
    /// </summary>
    private static bool IsCanonicalLength(StringBuilder digits, bool hasPlus) => digits.Length switch
    {
        10 => !hasPlus,
        11 => digits[0] == '1',
        _ => false,
    };

    /// <summary>Whether what follows <paramref name="index"/> is no longer part of the number.</summary>
    private static bool IsEndOfNumberRun(string text, int index)
    {
        var lookahead = index;
        return !TryReadToken(text, ref lookahead, out var next) || !IsNumberToken(next);
    }
}
