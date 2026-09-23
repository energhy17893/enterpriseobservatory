namespace EnterpriseObservatory.RedfishProbe;

/// <summary>Keeps enough of a value to tell entries apart without disclosing it.</summary>
/// <remarks>
/// Never a password: no code path in this tool ever passes a credential
/// through this. It exists for serials, UUIDs and hostnames -- the values
/// M6.0b's constraints ask <c>--mask</c> to redact.
/// </remarks>
internal static class Mask
{
    public static string Show(string? value, bool mask)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "(none)";
        }

        return !mask ? value : value.Length <= 4 ? "****" : $"{value[..2]}***{value[^2..]}";
    }
}
