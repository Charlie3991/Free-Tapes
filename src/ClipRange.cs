using System.Globalization;

namespace FreeTapes;
public sealed record ClipRange(double StartSeconds = 0, double? EndSeconds = null)
{
    public double? LengthSeconds => EndSeconds - StartSeconds;
    public static ClipRange Parse(string start, string end)
    {
        var range = new ClipRange(ParseTime(start, true) ?? 0, ParseTime(end, true));
        range.Validate(); return range;
    }
    private static double? ParseTime(string text, bool blankAllowed)
    {
        if (string.IsNullOrWhiteSpace(text) && blankAllowed) return null;
        var parts = text.Trim().Split(':');
        if (parts.Length is < 1 or > 3) throw new ArgumentException("Use seconds, mm:ss, or hh:mm:ss for trim times.");
        double seconds = 0;
        for (var i = 0; i < parts.Length; i++)
        {
            if (!double.TryParse(parts[i], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value)
                || !double.IsFinite(value) || value < 0 || (i < parts.Length - 1 && value != Math.Truncate(value))
                || (parts.Length > 1 && i > 0 && value >= 60))
                throw new ArgumentException("Enter a valid nonnegative trim time (for example 1:25.5).");
            seconds = seconds * 60 + value;
        }
        if (seconds > TimeSpan.MaxValue.TotalSeconds / 2) throw new ArgumentException("Trim time is too large.");
        return seconds;
    }
    public void Validate(double? duration = null)
    {
        if (!double.IsFinite(StartSeconds) || StartSeconds < 0 || (EndSeconds is double end && (!double.IsFinite(end) || end <= StartSeconds)))
            throw new ArgumentException("End time must be later than the start time.");
        if (duration is double total && (StartSeconds >= total || EndSeconds > total + 0.05))
            throw new ArgumentException("Trim times must be within the song's duration.");
    }
    public static string Format(double seconds) => seconds.ToString("0.###", CultureInfo.InvariantCulture);
}
