using EnterpriseObservatory.Application.Collection;

namespace EnterpriseObservatory.Collectors.Contract.Tests;

/// <summary>A clock that never moves, so a contract case is not timing-sensitive.</summary>
internal sealed class TestClock : IClock
{
    public DateTimeOffset UtcNow { get; set; } = new(2026, 9, 22, 9, 0, 0, TimeSpan.Zero);
}
