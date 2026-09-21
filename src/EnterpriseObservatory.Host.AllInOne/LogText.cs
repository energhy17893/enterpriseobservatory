using System.Globalization;
using System.Text;

namespace EnterpriseObservatory.Host.AllInOne;

/// <summary>
/// Makes text that came from outside safe to put on one log line.
/// </summary>
/// <remarks>
/// <para>
/// Alert descriptions and vCenter fault messages carry text vCenter controls:
/// VM names, event messages, user names. A line break inside one of those, in
/// a plain-text or console log, starts what looks like a new, genuine entry —
/// written by whoever can name a VM. So control characters are shown rather
/// than obeyed: CR and LF as <c>\r</c> and <c>\n</c>, the rest as
/// <c>\u00XX</c>. Tab is left alone; it cannot start a line.
/// </para>
/// <para>
/// This is for the log path only. The stored alert keeps the text as vCenter
/// gave it; escaping it there would change what an operator searches for.
/// </para>
/// </remarks>
public static class LogText
{
    /// <summary>The text with every line-breaking or control character made visible.</summary>
    public static string Escape(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text ?? string.Empty;
        }

        var first = IndexOfUnsafe(text);
        if (first < 0)
        {
            return text;
        }

        var escaped = new StringBuilder(text.Length + 8);
        escaped.Append(text, 0, first);

        for (var i = first; i < text.Length; i++)
        {
            var c = text[i];
            switch (c)
            {
                case '\r':
                    escaped.Append("\\r");
                    break;
                case '\n':
                    escaped.Append("\\n");
                    break;
                default:
                    if (IsUnsafe(c))
                    {
                        escaped.Append("\\u").Append(((int)c).ToString("X4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        escaped.Append(c);
                    }

                    break;
            }
        }

        return escaped.ToString();
    }

    private static int IndexOfUnsafe(string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            if (IsUnsafe(text[i]))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// C0 controls except tab, DEL, and the Unicode line breaks some log
    /// viewers honour (NEL, LINE SEPARATOR, PARAGRAPH SEPARATOR).
    /// </summary>
    private static bool IsUnsafe(char c) =>
        (c < ' ' && c != (char)9) || c is (char)0x7F or (char)0x85 or (char)0x2028 or (char)0x2029;
}
