using EnterpriseObservatory.Api.Contracts;
using EnterpriseObservatory.Application.Collection;
using EnterpriseObservatory.Domain;

namespace EnterpriseObservatory.Api.Projections;

/// <summary>
/// The events vCenter reported, and whether they are still being read.
/// </summary>
/// <remarks>
/// <para>
/// A projection of its own rather than a method on <see cref="ReadModel"/>,
/// because it reads a store the rest of the read model does not, and nothing
/// here joins the two.
/// </para>
/// <para>
/// The streams travel with the events, always. A list of events with nothing
/// beside it saying when the list was last refreshed reads as current long
/// after the collector stopped — and "no new events" and "we have not been
/// able to ask" look the same on a list and nowhere else.
/// </para>
/// </remarks>
public static class EventFeed
{
    /// <summary>The page size when none is asked for.</summary>
    public const int DefaultLimit = 50;

    /// <remarks>
    /// Paged and searched on the server (E2): the client used to ask for the
    /// newest 200 and filter those, so "nothing matches" could be false for
    /// an older event still inside the retention window.
    /// </remarks>
    public static EventFeedView Build(
        IEventStore store, string? source, string? search, int? offset, int? limit)
    {
        ArgumentNullException.ThrowIfNull(store);

        var safeOffset = Math.Max(offset ?? 0, 0);
        var safeLimit = Math.Clamp(limit ?? DefaultLimit, 1, EventCollectionPipeline.MaxRecent);
        var page = store.Recent(
            safeOffset,
            safeLimit,
            string.IsNullOrWhiteSpace(source) ? null : source,
            string.IsNullOrWhiteSpace(search) ? null : search);

        return new EventFeedView
        {
            Events = [.. page.Events.Select(Present)],
            Total = page.Total,
            Offset = safeOffset,
            Limit = safeLimit,
            Streams =
            [
                .. store.Cursors
                    .OrderBy(c => c.SourceInstanceId, StringComparer.Ordinal)
                    .Select(c => new EventStreamView
                    {
                        SourceInstanceId = c.SourceInstanceId,
                        LastAttemptUtc = c.LastAttemptUtc,
                        LastSuccessUtc = c.LastSuccessUtc,
                        LastFailure = c.LastFailure,
                        LastGapUtc = c.LastGapUtc,
                    }),
            ],
            RetentionDays = (int)EventCollectionPipeline.Retention.TotalDays,
        };
    }

    private static SourceEventView Present(SourceEvent e) => new()
    {
        SourceInstanceId = e.SourceInstanceId,
        Key = e.Key,
        CreatedAtUtc = e.CreatedAtUtc,
        EventClass = e.EventClass,
        TypeId = e.TypeId,
        Severity = e.Severity,
        Message = e.Message,
        UserName = e.UserName,
        DatacenterName = e.DatacenterName,
        ComputeResource = Object(e.SourceInstanceId, e.ComputeResource),
        Host = Object(e.SourceInstanceId, e.Host),
        VirtualMachine = Object(e.SourceInstanceId, e.VirtualMachine),
        Datastore = Object(e.SourceInstanceId, e.Datastore),
    };

    /// <summary>
    /// The object, with the entity id the inventory would have given it.
    /// </summary>
    /// <remarks>
    /// Composed the same way the inventory composes it, so a link goes to the
    /// same page. The entity may no longer exist — a deleted virtual machine's
    /// events outlive it — and the page then says so, which is better than an
    /// event with no way to follow it. A reference that cannot form an id is
    /// shown by name only rather than failing the whole list.
    /// </remarks>
    private static EventObjectView? Object(string source, EventObjectRef? reference)
    {
        if (reference is null)
        {
            return null;
        }

        var linkable = reference.MoRef.Length > 0 &&
                       source.Length > 0 &&
                       reference.MoRef.IndexOfAny(['/', '\\']) < 0 &&
                       source.IndexOfAny(['/', '\\']) < 0;

        return new EventObjectView
        {
            Name = reference.Name,
            EntityId = linkable ? EntityId.For(source, reference.MoRef).Value : null,
        };
    }
}
