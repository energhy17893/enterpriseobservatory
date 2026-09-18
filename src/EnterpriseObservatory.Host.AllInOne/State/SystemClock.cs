using EnterpriseObservatory.Application.Collection;

namespace EnterpriseObservatory.Host.AllInOne.State;

/// <summary>The system clock.</summary>
/// <remarks>
/// The only implementation of <see cref="IClock"/> outside tests. Time is a
/// dependency so that alert hysteresis, retention and flap windows can be
/// tested at any instant rather than by waiting.
/// </remarks>
public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
