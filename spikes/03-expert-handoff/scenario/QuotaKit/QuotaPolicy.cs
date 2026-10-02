using System.Globalization;

namespace QuotaKit;

/// <summary>A tenant's daily allowance and the time zone that defines its day.</summary>
/// <param name="Tenant">Tenant identifier.</param>
/// <param name="Limit">Maximum units per local day.</param>
/// <param name="Zone">Time zone whose midnight resets the quota.</param>
public sealed record QuotaPolicy(string Tenant, long Limit, TimeZoneInfo Zone)
{
    /// <summary>Parses lines of the form <c>tenant=limit@ZoneId</c>; blank lines and <c>#</c> comments are skipped.</summary>
    /// <param name="text">Multi-line policy text.</param>
    /// <returns>Policies in file order.</returns>
    /// <exception cref="FormatException">A line is malformed or the zone is unknown.</exception>
    public static IReadOnlyList<QuotaPolicy> Parse(string text)
    {
        var result = new List<QuotaPolicy>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var eq = line.IndexOf('=');
            var at = line.IndexOf('@');
            if (eq <= 0 || at <= eq + 1)
            {
                throw new FormatException($"Bad policy line: '{line}'");
            }

            if (!long.TryParse(line[(eq + 1)..at], NumberStyles.None, CultureInfo.InvariantCulture, out var limit))
            {
                throw new FormatException($"Bad limit in: '{line}'");
            }

            try
            {
                var zone = TimeZoneInfo.FindSystemTimeZoneById(line[(at + 1)..].Trim());
                result.Add(new QuotaPolicy(line[..eq].Trim(), limit, zone));
            }
            catch (TimeZoneNotFoundException ex)
            {
                throw new FormatException($"Unknown zone in: '{line}'", ex);
            }
        }

        return result;
    }
}
