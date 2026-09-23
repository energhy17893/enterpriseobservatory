namespace EnterpriseObservatory.Application.Collection;

/// <summary>
/// What the runner remembers about one source between its reads (F5,
/// ADR-0025 §4).
/// </summary>
/// <remarks>
/// <para>
/// The <em>storage</em> and <em>lifetime</em> of a collector's learned state,
/// not its logic. What a cell holds and how it is used stays in the collector
/// that put it there (F note §4): a vSphere probe's reputation, its read
/// cursor, its high-water marks. The runner only keeps the cells, hands them
/// to each read, and throws them away with the source they belong to — one
/// state per source object, so a connection that is rebuilt starts from
/// nothing, as it did when the state lived in the collector.
/// </para>
/// <para>
/// Unlocked, deliberately. It is touched only inside the source's read slot:
/// by the read itself, and by the advances the runner applies at the start of
/// the next read (<see cref="IStateAdvance"/>). F2's skip rule means at most
/// one read of a source is running at any time — an abandoned read keeps its
/// slot until it truly finishes — so there is never a second thread in here.
/// The locks the vSphere collector used to carry existed only because that
/// was not so.
/// </para>
/// </remarks>
public sealed class SourceState
{
    private readonly Dictionary<Type, object> _cells = [];

    /// <summary>The one <typeparamref name="T"/> this source keeps, created on first use.</summary>
    public T Cell<T>()
        where T : class, new()
    {
        if (!_cells.TryGetValue(typeof(T), out var cell))
        {
            _cells[typeof(T)] = cell = new T();
        }

        return (T)cell;
    }

    /// <summary>
    /// The learned-limit slots (T1.2): an integer per key that the platform
    /// is known to accept.
    /// </summary>
    /// <remarks>
    /// Generic on purpose: what the value means, how it is first planned and
    /// what counts as a refusal stay with the collector (the vSphere
    /// <c>maxQueryMetrics</c> formula is not the runner's business, F note
    /// §4.1). The runner only remembers the number.
    /// </remarks>
    public LearnedLimits Limits => Cell<LearnedLimits>();
}

/// <summary>Integers a source has learnt its platform accepts, by key.</summary>
public sealed class LearnedLimits
{
    private readonly Dictionary<string, int> _values = new(StringComparer.Ordinal);

    public int? For(string key) => _values.TryGetValue(key, out var value) ? value : null;

    public void Remember(string key, int value)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value);
        _values[key] = value;
    }
}

/// <summary>
/// A change to a source's learned state that holds only once the read that
/// produced it is safely accepted.
/// </summary>
/// <remarks>
/// Returned by a read as data (<see cref="ObservationBatch.Advance"/>) and
/// applied by the runner — after the store queue accepted the batch, inside
/// the source's next read slot — never by the collector. A mark that moved on
/// the read would sit ahead of the history if the write then failed, and the
/// hole behind it would never be asked for again (T0.4).
/// </remarks>
public interface IStateAdvance
{
    void Apply(SourceState state);
}
