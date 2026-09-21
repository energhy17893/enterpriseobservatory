using System.Globalization;
using System.Security;

namespace EnterpriseObservatory.Collectors.Vsphere;

/// <summary>
/// Builds vim25 SOAP request bodies.
/// </summary>
/// <remarks>
/// Kept apart from the transport so the request shape can be inspected and
/// tested without a server. Targets vSphere 8; the <c>urn:vim25</c> namespace
/// has been stable across 6.x–8.x, so these bodies are not version-specific.
/// </remarks>
public static class VsphereSoapRequests
{
    /// <summary>Wraps a body in a SOAP envelope.</summary>
    public static string Envelope(string body) => $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <soapenv:Envelope xmlns:soapenv="http://schemas.xmlsoap.org/soap/envelope/" xmlns:vim25="urn:vim25">
          <soapenv:Body>
        {body}
          </soapenv:Body>
        </soapenv:Envelope>
        """;

    /// <summary>Reads the service content, which names every manager object.</summary>
    /// <remarks>
    /// Manager references are discovered rather than hard-coded. They are
    /// conventional (<c>propertyCollector</c>, <c>PerfMgr</c>) but conventions
    /// are not contracts, and a wrong guess fails in a way that looks like an
    /// empty inventory.
    /// </remarks>
    public static string RetrieveServiceContent() => Envelope("""
            <vim25:RetrieveServiceContent>
              <vim25:_this type="ServiceInstance">ServiceInstance</vim25:_this>
            </vim25:RetrieveServiceContent>
        """);

    public static string Login(string sessionManagerMoRef, string username, string password) => Envelope($"""
            <vim25:Login>
              <vim25:_this type="SessionManager">{Escape(sessionManagerMoRef)}</vim25:_this>
              <vim25:userName>{Escape(username)}</vim25:userName>
              <vim25:password>{Escape(password)}</vim25:password>
            </vim25:Login>
        """);

    public static string Logout(string sessionManagerMoRef) => Envelope($"""
            <vim25:Logout>
              <vim25:_this type="SessionManager">{Escape(sessionManagerMoRef)}</vim25:_this>
            </vim25:Logout>
        """);

    /// <summary>
    /// Reads <c>config.vpxd.stats.maxQueryMetrics</c>.
    /// </summary>
    /// <remarks>
    /// Needed to size performance queries. Reading it may itself be denied, in
    /// which case the caller falls back to the documented default rather than
    /// failing — see <see cref="AdaptiveBatchSizer"/>.
    /// </remarks>
    public static string QueryMaxQueryMetrics(string settingMoRef) => Envelope($"""
            <vim25:QueryOptions>
              <vim25:_this type="OptionManager">{Escape(settingMoRef)}</vim25:_this>
              <vim25:name>config.vpxd.stats.maxQueryMetrics</vim25:name>
            </vim25:QueryOptions>
        """);

    /// <summary>
    /// Reads every counter definition up to a statistics level.
    /// </summary>
    /// <param name="level">
    /// 4 returns the full catalogue. This asks what the server <em>defines</em>,
    /// not what it is currently collecting — availability is a separate
    /// question, answered by <see cref="QueryAvailablePerfMetric"/>.
    /// </param>
    /// <remarks>
    /// The documented way to enumerate counters, and the one the previous
    /// product used successfully against real servers. An earlier attempt here
    /// used a different call and came back with 28 counters where a vCenter
    /// defines several hundred.
    /// </remarks>
    public static string QueryPerfCounterByLevel(string perfManagerMoRef, int level = 4) => Envelope($"""
            <vim25:QueryPerfCounterByLevel>
              <vim25:_this type="PerformanceManager">{Escape(perfManagerMoRef)}</vim25:_this>
              <vim25:level>{level.ToString(CultureInfo.InvariantCulture)}</vim25:level>
            </vim25:QueryPerfCounterByLevel>
        """);

    /// <summary>
    /// Asks which counters an entity can currently supply.
    /// </summary>
    /// <remarks>
    /// The empirical way to detect an insufficient statistics level: rather than
    /// reasoning about how level numbers map to counters across versions, ask
    /// the server what it has.
    /// </remarks>
    /// <param name="window">
    /// The time range, for a historical interval. Null for real-time.
    /// </param>
    /// <remarks>
    /// The same range a historical <see cref="QueryPerf"/> needs, and for the
    /// same reason — but this one bites harder, because it gates the other.
    /// Asked without a range, a historical availability probe answers "nothing
    /// is available", the collector concludes the statistics level is too low,
    /// and the data query is never issued at all. Fixing the range on the data
    /// query alone changed nothing for exactly this reason.
    /// </remarks>
    public static string QueryAvailablePerfMetric(
        string perfManagerMoRef,
        string entityMoRef,
        string entityType,
        int intervalSeconds,
        (DateTimeOffset From, DateTimeOffset To)? window = null)
    {
        // Schema order: entity, beginTime?, endTime?, intervalId?.
        var range = window is { } w
            ? $"""

              <vim25:beginTime>{w.From.UtcDateTime.ToString("o", CultureInfo.InvariantCulture)}</vim25:beginTime>
              <vim25:endTime>{w.To.UtcDateTime.ToString("o", CultureInfo.InvariantCulture)}</vim25:endTime>
        """
            : string.Empty;

        return Envelope($"""
            <vim25:QueryAvailablePerfMetric>
              <vim25:_this type="PerformanceManager">{Escape(perfManagerMoRef)}</vim25:_this>
              <vim25:entity type="{Escape(entityType)}">{Escape(entityMoRef)}</vim25:entity>{range}
              <vim25:intervalId>{intervalSeconds.ToString(CultureInfo.InvariantCulture)}</vim25:intervalId>
            </vim25:QueryAvailablePerfMetric>
        """);
    }

    /// <summary>Requests samples for a batch of entities of one type.</summary>
    /// <param name="maxSample">
    /// How many samples to return per series. More than one is requested so a
    /// short spike between polls is not missed entirely; the parser keeps the
    /// latest for current state. The previous product asked for exactly one.
    /// </param>
    /// <param name="window">
    /// The time range, for a historical interval. Null for real-time.
    /// </param>
    /// <remarks>
    /// A historical query without a range returns nothing. vim25 ignores
    /// <c>maxSample</c> outside real-time, so the range is the only thing
    /// selecting any samples at all — and its absence produces an empty answer
    /// rather than a fault, which is why every datastore in a live estate read
    /// as unmeasured with nothing anywhere saying why.
    /// </remarks>
    public static string QueryPerf(
        string perfManagerMoRef,
        IReadOnlyList<string> entityMoRefs,
        string entityType,
        IReadOnlyList<VsphereCounter> counters,
        int intervalSeconds,
        int maxSample,
        (DateTimeOffset From, DateTimeOffset To)? window = null)
    {
        ArgumentNullException.ThrowIfNull(entityMoRefs);
        ArgumentNullException.ThrowIfNull(counters);

        // Schema order: entity, startTime?, endTime?, maxSample?, metricId*,
        // intervalId?, format?. PerfQuerySpec is an xsd:sequence, so a range
        // emitted anywhere else is rejected outright with "Unexpected element
        // tag" rather than ignored.
        var range = window is { } w
            ? $"""

                    <vim25:startTime>{w.From.UtcDateTime.ToString("o", CultureInfo.InvariantCulture)}</vim25:startTime>
                    <vim25:endTime>{w.To.UtcDateTime.ToString("o", CultureInfo.InvariantCulture)}</vim25:endTime>
            """
            : string.Empty;

        // instance "*" asks for every device series plus the aggregate; the
        // parser decides how to combine them.
        var metrics = string.Concat(counters.Select(c => $"""

                    <vim25:metricId><vim25:counterId>{c.Id.ToString(CultureInfo.InvariantCulture)}</vim25:counterId><vim25:instance>*</vim25:instance></vim25:metricId>
            """));

        // Element order is part of the contract: PerfQuerySpec is an XSD
        // sequence, so the server rejects a spec whose children are in any
        // other order with "Unexpected element tag". The schema order is
        // entity, startTime?, endTime?, maxSample?, metricId*, intervalId?,
        // format? — metricId before intervalId, both after maxSample.
        var specs = string.Concat(entityMoRefs.Select(moRef => $"""

                  <vim25:querySpec>
                    <vim25:entity type="{Escape(entityType)}">{Escape(moRef)}</vim25:entity>{range}
                    <vim25:maxSample>{maxSample.ToString(CultureInfo.InvariantCulture)}</vim25:maxSample>{metrics}
                    <vim25:intervalId>{intervalSeconds.ToString(CultureInfo.InvariantCulture)}</vim25:intervalId>
                    <vim25:format>normal</vim25:format>
                  </vim25:querySpec>
            """));

        return Envelope($"""
                <vim25:QueryPerf>
                  <vim25:_this type="PerformanceManager">{Escape(perfManagerMoRef)}</vim25:_this>{specs}
                </vim25:QueryPerf>
            """);
    }

    /// <summary>
    /// Requests properties for every object of the given types below the root.
    /// </summary>
    /// <remarks>
    /// <c>RetrievePropertiesEx</c> rather than <c>RetrieveProperties</c>: the
    /// former pages, and the latter can be refused outright on a large
    /// inventory. Paging must then actually be followed — see
    /// <see cref="ContinueRetrievePropertiesEx"/>.
    /// </remarks>
    public static string RetrievePropertiesEx(
        string propertyCollectorMoRef,
        string rootFolderMoRef,
        string viewManagerMoRef,
        IReadOnlyDictionary<string, IReadOnlyList<string>> propertiesByType,
        int maxObjects)
    {
        ArgumentNullException.ThrowIfNull(propertiesByType);

        var propSet = string.Concat(propertiesByType.Select(pair => $"""

                    <vim25:propSet>
                      <vim25:type>{Escape(pair.Key)}</vim25:type>
                      <vim25:all>false</vim25:all>{string.Concat(pair.Value.Select(p => $"""

                      <vim25:pathSet>{Escape(p)}</vim25:pathSet>
            """))}
                    </vim25:propSet>
            """));

        return Envelope($"""
                <vim25:RetrievePropertiesEx>
                  <vim25:_this type="PropertyCollector">{Escape(propertyCollectorMoRef)}</vim25:_this>
                  <vim25:specSet>{propSet}
                    <vim25:objectSet>
                      <vim25:obj type="ContainerView">{Escape(viewManagerMoRef)}</vim25:obj>
                      <vim25:skip>false</vim25:skip>
                      <vim25:selectSet xsi:type="vim25:TraversalSpec" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
                        <vim25:name>view</vim25:name>
                        <vim25:type>ContainerView</vim25:type>
                        <vim25:path>view</vim25:path>
                        <vim25:skip>false</vim25:skip>
                      </vim25:selectSet>
                    </vim25:objectSet>
                  </vim25:specSet>
                  <vim25:options>
                    <vim25:maxObjects>{maxObjects.ToString(CultureInfo.InvariantCulture)}</vim25:maxObjects>
                  </vim25:options>
                </vim25:RetrievePropertiesEx>
            """);
    }

    /// <summary>
    /// Reads the names behind a set of alarm references.
    /// </summary>
    /// <remarks>
    /// <para>
    /// No container view and no traversal: the references are already known, so
    /// each is named directly in its own object spec. A view over every Alarm
    /// in the inventory would read hundreds of definitions to use the two that
    /// are currently triggered.
    /// </para>
    /// <para>
    /// <c>info.name</c> is what an operator recognises — "Host memory status" —
    /// while <c>info.systemName</c> is the localisation key behind it. The name
    /// is what goes on screen, because the key is only readable to somebody who
    /// already knows the answer.
    /// </para>
    /// </remarks>
    public static string RetrieveAlarmDefinitions(
        string propertyCollectorMoRef,
        IReadOnlyList<string> alarmMoRefs)
    {
        ArgumentNullException.ThrowIfNull(alarmMoRefs);

        var objectSet = string.Concat(alarmMoRefs.Select(moRef => $"""

                    <vim25:objectSet>
                      <vim25:obj type="Alarm">{Escape(moRef)}</vim25:obj>
                      <vim25:skip>false</vim25:skip>
                    </vim25:objectSet>
            """));

        return Envelope($"""
                <vim25:RetrievePropertiesEx>
                  <vim25:_this type="PropertyCollector">{Escape(propertyCollectorMoRef)}</vim25:_this>
                  <vim25:specSet>
                    <vim25:propSet>
                      <vim25:type>Alarm</vim25:type>
                      <vim25:all>false</vim25:all>
                      <vim25:pathSet>info.name</vim25:pathSet>
                      <vim25:pathSet>info.description</vim25:pathSet>
                    </vim25:propSet>{objectSet}
                  </vim25:specSet>
                  <vim25:options />
                </vim25:RetrievePropertiesEx>
            """);
    }

    /// <summary>Fetches the next page.</summary>
    /// <remarks>
    /// Not optional. Stopping at the first page truncates the inventory at
    /// whatever the server chose to return, and the result looks like a small
    /// healthy estate rather than a bug.
    /// </remarks>
    public static string ContinueRetrievePropertiesEx(string propertyCollectorMoRef, string token) => Envelope($"""
            <vim25:ContinueRetrievePropertiesEx>
              <vim25:_this type="PropertyCollector">{Escape(propertyCollectorMoRef)}</vim25:_this>
              <vim25:token>{Escape(token)}</vim25:token>
            </vim25:ContinueRetrievePropertiesEx>
        """);

    /// <summary>Releases a retrieval that will not be continued.</summary>
    /// <remarks>
    /// The server holds the rest of the result against the token until it is
    /// either continued to the end or cancelled. A read cut off between pages
    /// does neither unless it says so.
    /// </remarks>
    public static string CancelRetrievePropertiesEx(string propertyCollectorMoRef, string token) => Envelope($"""
            <vim25:CancelRetrievePropertiesEx>
              <vim25:_this type="PropertyCollector">{Escape(propertyCollectorMoRef)}</vim25:_this>
              <vim25:token>{Escape(token)}</vim25:token>
            </vim25:CancelRetrievePropertiesEx>
        """);

    /// <summary>Creates a view over every object of the given types.</summary>
    public static string CreateContainerView(
        string viewManagerMoRef,
        string rootFolderMoRef,
        IReadOnlyList<string> types) => Envelope($"""
            <vim25:CreateContainerView>
              <vim25:_this type="ViewManager">{Escape(viewManagerMoRef)}</vim25:_this>
              <vim25:container type="Folder">{Escape(rootFolderMoRef)}</vim25:container>{string.Concat(types.Select(t => $"""

              <vim25:type>{Escape(t)}</vim25:type>
        """))}
              <vim25:recursive>true</vim25:recursive>
            </vim25:CreateContainerView>
        """);

    public static string DestroyView(string viewMoRef) => Envelope($"""
            <vim25:DestroyView>
              <vim25:_this type="ContainerView">{Escape(viewMoRef)}</vim25:_this>
            </vim25:DestroyView>
        """);

    // --- events -------------------------------------------------------------
    //
    // A collector rather than QueryEvents, and the reason is a silent
    // truncation: QueryEvents returns the *oldest* thousand events of the
    // window it is given, so a day's query on a busy vCenter can miss the most
    // recent hours with nothing in the reply saying so. A collector exposes the
    // newest page first and is walked backwards to where the last read
    // stopped, so what is lost under load, if anything, is the old end.

    /// <summary>Opens a server-side collector over events since a time.</summary>
    /// <remarks>
    /// EventFilterSpec is an xsd:sequence; <c>time</c> comes before everything
    /// this sends, and only <c>beginTime</c> is set so that events written
    /// while the read is in progress are not excluded by an end bound.
    /// </remarks>
    public static string CreateCollectorForEvents(string eventManagerMoRef, DateTimeOffset beginTime) => Envelope($"""
            <vim25:CreateCollectorForEvents>
              <vim25:_this type="EventManager">{Escape(eventManagerMoRef)}</vim25:_this>
              <vim25:filter>
                <vim25:time>
                  <vim25:beginTime>{beginTime.UtcDateTime.ToString("o", CultureInfo.InvariantCulture)}</vim25:beginTime>
                </vim25:time>
              </vim25:filter>
            </vim25:CreateCollectorForEvents>
        """);

    /// <summary>Sets how many events the latest page and each backwards read hold.</summary>
    public static string SetCollectorPageSize(string collectorMoRef, int maxCount) => Envelope($"""
            <vim25:SetCollectorPageSize>
              <vim25:_this type="EventHistoryCollector">{Escape(collectorMoRef)}</vim25:_this>
              <vim25:maxCount>{maxCount.ToString(CultureInfo.InvariantCulture)}</vim25:maxCount>
            </vim25:SetCollectorPageSize>
        """);

    /// <summary>Reads the collector's <c>latestPage</c> — the newest events.</summary>
    public static string RetrieveLatestEventPage(string propertyCollectorMoRef, string collectorMoRef) => Envelope($"""
            <vim25:RetrievePropertiesEx>
              <vim25:_this type="PropertyCollector">{Escape(propertyCollectorMoRef)}</vim25:_this>
              <vim25:specSet>
                <vim25:propSet>
                  <vim25:type>EventHistoryCollector</vim25:type>
                  <vim25:all>false</vim25:all>
                  <vim25:pathSet>latestPage</vim25:pathSet>
                </vim25:propSet>
                <vim25:objectSet>
                  <vim25:obj type="EventHistoryCollector">{Escape(collectorMoRef)}</vim25:obj>
                  <vim25:skip>false</vim25:skip>
                </vim25:objectSet>
              </vim25:specSet>
              <vim25:options />
            </vim25:RetrievePropertiesEx>
        """);

    /// <summary>Reads the next-older page of events from the collector's position.</summary>
    public static string ReadPreviousEvents(string collectorMoRef, int maxCount) => Envelope($"""
            <vim25:ReadPreviousEvents>
              <vim25:_this type="EventHistoryCollector">{Escape(collectorMoRef)}</vim25:_this>
              <vim25:maxCount>{maxCount.ToString(CultureInfo.InvariantCulture)}</vim25:maxCount>
            </vim25:ReadPreviousEvents>
        """);

    /// <summary>Closes a collector. vCenter bounds how many one session may hold.</summary>
    public static string DestroyCollector(string collectorMoRef) => Envelope($"""
            <vim25:DestroyCollector>
              <vim25:_this type="EventHistoryCollector">{Escape(collectorMoRef)}</vim25:_this>
            </vim25:DestroyCollector>
        """);

    /// <summary>
    /// Escapes a value for inclusion in XML content.
    /// </summary>
    /// <remarks>
    /// Applied to every interpolated value without exception. Passwords and
    /// object names both routinely contain <c>&amp;</c> and <c>&lt;</c>, and an
    /// unescaped one produces a malformed request that fails in a way nobody
    /// traces back to a punctuation mark.
    /// </remarks>
    public static string Escape(string value) => SecurityElement.Escape(value) ?? string.Empty;
}
