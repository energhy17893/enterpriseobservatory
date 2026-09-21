using System.Globalization;

namespace EnterpriseObservatory.Application.Analysis;

/// <summary>
/// How the rules recognise a counter and print a number, written once.
/// </summary>
internal static class Readings
{
    /// <summary>Whether a counter is the one wanted. Vendors do not agree on case.</summary>
    public static bool IsCounter(string counterName, string wanted) =>
        string.Equals(counterName, wanted, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Milliseconds, and deliberately nothing else. See <see cref="PeerOutliers"/>.
    /// </summary>
    public static bool IsMilliseconds(string unit) =>
        string.Equals(unit, "millisecond", StringComparison.OrdinalIgnoreCase);

    /// <summary>A number as the alert texts print it: at most two decimals, invariant.</summary>
    public static string Number(double value) =>
        value.ToString("0.##", CultureInfo.InvariantCulture);
}
