# F — Inverting collector authority (design note)

Status: **draft for planner review** (22 September 2026). No code is changed until this note
is approved. Companion to [ADR-0025](../adr/0025-collectors-only-read.md) (accepted) and
`reference-approaches.md` §10.2 and §10.3.
Scope: package F as ADR-0025 redefines it. The runner (`SourceRunner` and whatever it grows
into) takes ownership of transport, session, server-side cleanup, learned state and
self-metrics. The vSphere collector keeps SOAP, parsing and vSphere-specific protocol.
Constraint: **the contract suite (E, `tests/EnterpriseObservatory.Collectors.Contract.Tests`)
stays green after every PR.** Every PR either changes no behaviour, or names the one behaviour
it changes and adds the contract case that pins it down.

Line numbers refer to `origin/main` at `5736e6d`.

## 0. Summary

- Two findings come first, as small independent PRs, because each is a live instance of the
  "guard bypassed by a side road" class ADR-0025 exists to close:
  **F1** (the event read bypasses `SourceRunner`) and **F2** (no per-vCenter request
  ceiling). Both are confirmed in code (§2).
- Then four PRs in dependency order: **F3** HttpClient + session together → **F4** server-side
  cleanup scope → **F5** learned state → **F6** self-metrics.
- `VsphereClient` goes from 2,276 lines to roughly **1,800–1,850**. What remains is SOAP
  envelopes, fault decoding and parsing (§3.7).
- Five things stay in the vSphere collector on purpose, because none of them has a Redfish
  counterpart (§4).

## 1. Current authority map

Who owns each concern today. "Collector" means `EnterpriseObservatory.Collectors.Vsphere`,
"runner" means `Application/Collection`, and "host" means `Host.AllInOne`.

### 1.1 Transport (HttpClient)

| What | Where | Owner |
|---|---|---|
| `HttpClient` built, one per connection | `VsphereSourceRegistry.cs:509` (`new HttpClient(VsphereClient.CreateHandler(options))`) | host |
| Handler policy: cookies, relaxed TLS | `VsphereClient.cs:2130` `CreateHandler` | collector (the registry comment at :505–507 says so deliberately) |
| Per-call timeout set on the shared client | `VsphereClient.cs:101` (`_http.Timeout = options.RequestTimeout`) | collector mutates a host-built object |
| Bounded body read (64 MB), DTD refusal, per-call deadline over headers and body | `VsphereClient.cs:1970` `PostAsync`, `ReadBoundedAsync` | collector |
| One client shared by three readers | `VsphereSourceRegistry.cs:514–526`: one `VsphereClient` handed to `VsphereInventorySource`, `VsphereObservationSource` and `VsphereEventSource` | host wires, collector holds |
| Client closed | `VsphereSourceRegistry.cs:302` `CloseAsync` (logout, then `Dispose` client and `HttpClient`) | host |
| Other constructors | `VsphereConnectionProbe.cs:62,68,201,206`; `tools/EnterpriseObservatory.VsphereProbe/Program.cs:104`; five test helpers (contract fixture, event, hardening, cleanup and race tests) | host, tool, tests |

### 1.2 Session

| What | Where |
|---|---|
| Session state: `_serviceContent`, `_loggedIn`, `_sessionGeneration`, `_sessionGate` | `VsphereClient.cs:85–93` |
| Open or renew under one lock, keyed by generation | `VsphereClient.cs:1881` `EnsureSessionAsync(int? expired, …)`; the generation check is at :1896–1899 |
| Re-login once on `NotAuthenticated` | `VsphereClient.cs:1952` `SendAsync` (reads the generation at :1957, retries at :1963–1966) |
| Every public read starts with `EnsureSessionAsync` | e.g. :115, :133, :329, :727, :1788, :2159 |
| Logout | `VsphereClient.cs:2235` `LogoutAsync`. **It clears `_loggedIn` at :2244 outside `_sessionGate`.** This is safe today only because the registry promises that no reader is left (`VsphereSourceRegistry.cs:293–297`). It is the same shape as the #53 row in ADR-0025's table |
| Logout + confirm (probe only) | `VsphereClient.cs:2188` `LogoutAndConfirmAsync`; `ReadSessionsAsync` at :2157 |

### 1.3 Server-side cleanup

| Server object | Created | Released | Mechanism |
|---|---|---|---|
| `ContainerView` (inventory) | `CreateViewAsync` :782 (called at :730) | `finally` at :771–776 → `TryDestroyViewAsync` :801 | `TryCleanUpAsync` :822, with `CleanupGrace` = 10 s (:844) when the read token is cancelled (T0.5) |
| `ContainerView` (shape probe) | :1206 | `finally` at :1317–1320 | same |
| Property-collector paging token | `RetrieveAllPagesAsync` :861, token kept in `open` (:883, :898) | `finally` at :903–913 → `CancelRetrievePropertiesEx` | same |
| `EventHistoryCollector` | `ReadEventsAsync` :1798 | `finally` at :1863–1866 → `TryDestroyCollectorAsync` :1869 | same |
| Session | `EnsureSessionAsync` :1881 | `LogoutAsync` :2235, called only by the registry on retire or shutdown and by the probe | host decides when, collector decides how |

Every release is a `finally` the collector author had to remember. T0.5 was one they got
wrong: cleanup was sent on the cancelled token.

### 1.4 Learned state (per source, in memory)

All of it lives in `VsphereObservationSource`, a singleton per connection.

| State | Where | Notes |
|---|---|---|
| `LearnedBatchSizes` | field :105; class `AdaptiveBatchSizer.cs:196`; read at :894 and :1543–1544; written at :1580 | keyed `(entity type, counter count)` |
| `AdaptiveBatchSizer` | `AdaptiveBatchSizer.cs:30`; the formula `Initial` is at :81–95 (`maxQueryMetrics`, `-1` → 8 × 256, `× 0.8 / counters`) | re-created on every read, seeded from the learned size |
| `ProbeMemory` (hourly "which counters exist" re-check) | class :139, field :102; used at :1451, :1467, :1591 | own `Lock`, because abandoned reads overlap (:108–137) |
| High-water marks + unfilled slots | class `HighWaterMarks` :322 (`_byMoRef`, `_unfilled` :329, `_written` :335), field :310 | seeded **from the store** in `SeedMarks` :708 (`_gaps.LatestSampleTimes` :746); advanced only after a store ack via `ObservationBatch.Stored` → `Stored` :941 → `_marks.Advance` :943 |
| `ReadCursor` (rotating start, T1.1) | class :1224, field :1253; used at :1329–1337 | |
| Counter catalogue cache | `VsphereClient.cs:88`, filled at :127 | lives in the client, not the source |
| `maxQueryMetrics` | fetched every read, `VsphereObservationSource.cs:200` → `VsphereClient.cs:131` | not cached |

The collector reads the product's store (`ICollectionGapStore`, `SeedMarks` :746) and writes
to it (`Stored` :941–955). ADR-0005 §3 says a collector reads its source, never the product's
store. `InventoryCollectionPipeline.cs:80–82` says the same thing in a comment. The gap
record is the exception that proves the rule is enforced by review only.

### 1.5 Self-metrics

| Metric | Where | Owner |
|---|---|---|
| Clock skew `collector.clockSkew.latest` | `CollectionGaps.cs:115` (name); `GetServerTimeAsync` called at `VsphereObservationSource.cs:204`; emitted at :262–263 via `ClockSkew` :957 | collector |
| Health: last attempt, last success, consecutive failures, backing off, partial failures | `SourceRunner.cs:366–436` (`Succeeded`, `Failed`, `FailedFatally`) | runner ✔ |
| Unreachable alert | `SourceRunner.cs:455` | runner ✔ |
| Held-session count, duration, samples read/unread, skipped cycles, freshness | — | nobody (package D) |

### 1.6 Concurrency and resilience

| Guard | Where | Scope |
|---|---|---|
| Breaker, one-strike rule, retry with jitter, shared budget, hard timeout and abandonment, cooperative grace | `SourceRunner.cs:54–364` | inventory and observation only |
| Concurrency gate | `ObservationCollectionPipeline.cs:57`, `InventoryCollectionPipeline.cs:63` | **per cycle, per pipeline** (§2.2) |
| Event read | `EventCollection.cs:399–536` | **none of the above** (§2.1) |

## 2. The two findings

### 2.1 (a) The event read bypasses `SourceRunner` — confirmed

- `EventCollectionPipeline.RunAsync` (`EventCollection.cs:399`) walks the sources in a plain
  `foreach` (:425) and calls **`source.ReadAsync(since, budget.Token)` directly at :459**.
  It has no breaker, no one-strike rule, no hard timeout and no abandonment. The only
  bound is `budget.CancelAfter(deadline)` (:412–416), which is cooperative only. A read that
  ignores the token holds the inventory loop, and that is the failure
  `ReadWithHardTimeoutAsync` (`SourceRunner.cs:233`) exists to prevent.
- T0.3 borrowed a verdict instead of adding a guard: `answered` (:401, :407, :435–452), fed
  from `MonitoringWorker.cs:88` (`result.ReportingSources`). The remarks at `EventCollection.cs:379–394`
  and `MonitoringWorker.cs:80–86` both say this read "has no breaker of its own".
- `VsphereEventSource.ReadAsync` (`VsphereEventSource.cs:34–48`) turns `VsphereApiException`
  and `HttpRequestException` into `EventRead.CouldNotAsk`. So even once this read goes through
  the runner, an `InvalidLogin` would reach it as a *value* and not as an `ICollectionFault`,
  and the one-strike rule (`SourceRunner.cs:156`, :354) would never fire. **F1 has to change
  this, not only the pipeline.**
- ADR-0025 §1: *"Envanter, gözlem ve **olay** okuması aynı tutamacı alır."* Events take the same
  guarded handle.

→ **F1, first and independent.** It shares no file with F2–F6 except `SourceRunner.cs:461`.

### 2.2 (b) The concurrency semaphore is per cycle, not per vCenter — confirmed

- `ObservationCollectionPipeline.cs:57` and `InventoryCollectionPipeline.cs:63` each create
  `new SemaphoreSlim(policy.MaxConcurrency, …)` (`CollectionPolicy.cs:55`, default 8) on
  **every `RunAsync`**. It is handed to `SourceRunner.RunAsync` as `gate` (:61, :76) and held
  around the **whole source read**.
- So it limits *how many sources are read at once within one pipeline's cycle*. It does not
  limit how many requests one vCenter receives:
  - The inventory, observation and event loops each get their own gate, or none in the
    events case, but they share one `VsphereClient` (`VsphereSourceRegistry.cs:514–526`).
  - An abandoned read keeps issuing requests after the runner walks away (`SourceRunner.cs:141–154`),
    and the next cycle's semaphore is a new object that does not know about it.
  - A′ will add parallel `QueryPerf` workers *inside* one observation read. The per-cycle
    gate cannot see them at all.
- T2.3 (roadmap) asks for *"tek vCenter'a istek tavanı"*, a request ceiling per vCenter.
  §10.3 gives the evidence for one: Telegraf `collect_concurrency` defaults to 1, with *"VM / 1500,
  never more than 8"*.

→ **F2, small and independent.** It is one per-source request ceiling in one place, and it is
where A′'s workers must acquire.

## 3. Migration order

```
F1 events → runner ─┐
F2 request ceiling ─┼─→ F3 (a) HttpClient + session ─┬─→ F4 (b) cleanup scope ─┐
                    │                                 └─→ F5 (c) learned state ──┼─→ F6 (d) self-metrics
(F1 and F2 are independent of each other)                                      │
F1w event read watermark (ADR-0026 §5.7) — after F1, outside F ─────────────────┘ (no dependency)
Cov per-object coverage (ADR-0026 (10)) — independent, outside F
```

F4 and F5 both depend only on F3, and they touch different files (`VsphereClient` finally
blocks versus `VsphereObservationSource` fields). They can run in either order, but not in
parallel on one worktree, because code work is serial. The dependency of F6 on F4 is weak: it
needs only the held-session count, which F3 already has.

Each PR below lists scope, files, risk, how E proves "behaviour unchanged", and the contract
case it adds.

### 3.1 F1 — Events through `SourceRunner`

- **Scope.**
  - `EventCollectionPipeline` gets a `SourceRunner` and calls it per source with a new
    `CollectorRole.Events`.
  - The `EventRead` null/empty semantics stay in the pipeline. The runner returns
    `Result = null` → the pipeline records `CouldNotAsk` from `Health.LastFailureDetail`, as
    today.
  - `VsphereEventSource` stops converting `VsphereApiException` into `CouldNotAsk`. It lets
    `ICollectionFault` reach the runner and still converts non-faults, so the runner's
    classification decides.
  - The `answered` verdict borrowing stays in this PR (see "behaviour unchanged" below). It is
    removed in a follow-up once the events health row has been seen in production.
- **Files.**
  - `EventCollection.cs:273, 399–515`.
  - `SourceRunner.cs:461`: `UnreachableAlert` maps every non-inventory role to "metrics", so
    a third role would be mislabelled. It becomes a switch and gets the fingerprint
    `collector-unreachable:events`.
  - `CollectionPorts.cs:284` (enum).
  - `VsphereEventSource.cs:34–48`.
  - `MonitoringWorker.cs:88` (passes prior health).
  - `PostgresCollectorHealthStore` needs **no migration**: `collector_health.role` is `text NOT NULL`
    with no CHECK (`PostgresSchema.cs`, ~:214), keyed `(instance_id, role)`.
  - `ReadModel.cs:964` shows the role as a string, so a third row appears on the collectors
    screen.
- **Behaviour unchanged, deliberately.**
  - The events policy is `policy with { MaxRetries = 0 }`: the event read is not retried today.
  - `SourceTimeout` = the remaining `EventReadDeadline`.
  - The runner's gate for events is `new SemaphoreSlim(1)`, which keeps the sequential order.
  - The cursor still moves only on a stored, successful read (:486–505).
- **Behaviour that changes (named).** The event read of a source whose read ignores the token
  is now abandoned at the deadline and not waited on. The event read gets its own breaker, so
  a vCenter whose event manager keeps failing while inventory answers is backed off from.
- **Risk.** Low to medium.
  - The events loop runs after inventory (`MonitoringWorker.cs:70–100`). A hard abandonment
    leaves an event read running inside the shared `VsphereClient` next to the next inventory
    read. That is the same exposure inventory and observation already have. F2 bounds it.
- **E proves.**
  - The existing `EventCollectionPipelineTests` and `VsphereEventCollectorTests` pass
    unchanged, except for the ones that assert a `VsphereApiException` becomes `CouldNotAsk`
    *inside the source*. That assertion moves to the pipeline level.
- **New contract cases.** A new `IEventContractFixture` + `EventContractTests`, source-level
  like the inventory and observation halves:
  1. a slow event source is abandoned and counted, and the cursor is unchanged;
  2. a rejected credential is **one strike**: the source is not asked next cycle;
  3. a failed read never becomes an empty window (`Events` null, never `[]`);
  4. self-metrics (health row, `LastAttemptUtc`) exist after every result.

### 3.2 F2 — Per-source request ceiling

- **Scope.**
  - A `SourceRequestGate` is created **once per source instance**, not per cycle. It is owned
    by the registry for now and moves into the runner's channel in F3.
  - It is acquired **per HTTP exchange**, never across a read and never across nested calls.
    A login inside `SendAsync` is its own acquisition, so nothing can deadlock on itself.
  - Cleanup acquires it with its own grace token.
  - `CollectionPolicy.MaxConcurrency` keeps its meaning, "sources read in parallel per
    pipeline". It gets a doc comment saying so, because its name invites the confusion.
- **Files.**
  - New `Application/Collection/SourceRequestGate.cs`.
  - `VsphereClient.cs:95` (optional ctor argument) and :1970 (`PostAsync` acquires around the
    exchange).
  - `VsphereSourceRegistry.cs:507–526` (one gate per `Built`).
  - `CollectionPolicy`/`CollectionOptions` (`MaxRequestsPerSource`).
- **Default = 4.** Today one vCenter sees at most three in-flight requests in normal operation,
  one per loop, because each read is sequential inside. A ceiling of 4 never blocks that, so
  normal behaviour is unchanged. It does bound the pathological case, where abandoned reads
  stack up. A′ tunes the value from its measurement, starting from Telegraf's rule.
- **Risk.** Low. The one trap is a permit held across an `await` that itself needs a permit.
  The rule is "the permit wraps `HttpClient.SendAsync` + body read only".
- **E proves.** The transport and source contract suites pass unchanged, and the session-race
  test (two calls, one expiry, one re-login) still counts one.
- **New contract case (transport).** *In-flight requests to one source never exceed its
  ceiling.* This holds across inventory, observation and events together, and with an
  abandoned read still running. The fixture is a counting handler that holds replies open.

### 3.3 F3 — (a) HttpClient and session ownership, together

They move together because `SendAsync`'s re-login on `NotAuthenticated` (`VsphereClient.cs:1952–1967`)
is session logic *inside* the transport call. Moving the transport alone would leave the
collector re-authenticating underneath a runner that believes it owns retry. Moving the
session alone would leave the runner unable to see the calls.

- **Scope.**
  - The runner gets a per-source **`SourceChannel`**. It owns:
    - the `HttpClient` and handler: cookies, the TLS decision, and later the TLS thumbprint
      from package G;
    - the per-call deadline over headers and body, and the bounded read;
    - the F2 gate;
    - the session under one lock with a generation, re-login once on expiry, and logout on
      retire or shutdown **under the same lock**. That closes the `LogoutAsync` :2244
      out-of-lock write.
  - The vendor supplies only the *how*, through `ISessionProtocol`:

    ```csharp
    // Application/Collection — names tentative
    public interface ISessionProtocol<TSession>
    {
        Task<TSession> OpenAsync(IRawExchange wire, CancellationToken ct);        // vSphere: RetrieveServiceContent + Login
        Task CloseAsync(IRawExchange wire, TSession session, CancellationToken ct); // vSphere: Logout
        ReplyVerdict Classify(RawReply reply);   // Ok | SessionExpired | Fault(exception) — vendor decodes, runner acts
    }
    ```
  - `VsphereClient` is constructed from `IGuardedChannel<VsphereServiceContent>` instead of
    `HttpClient`. `PostAsync` shrinks to "build envelope → `channel.SendAsync` → decode". The
    SOAP fault decoding (`VsphereSoapFaultReader`, `Describe`) stays and is what `Classify`
    calls.
- **Signatures.**
  - `IVsphereApi` / `IVsphereInventoryApi` / `IVsphereEventApi` keep every **read** method
    shape. That is deliberate: the ~20 test fakes (`FakeApi`, `LateApi`, `FixedPayloadApi`, …)
    keep compiling.
  - What leaves `VsphereClient`: `LogoutAsync`, `LogoutAndConfirmAsync`, `IDisposable`,
    `CreateHandler`. These become channel members.
  - `ReadSessionsAsync` stays as a SOAP read.
- **Files.**
  - `VsphereClient.cs:83–103, 1872–2100, 2130–2276`.
  - `VsphereSourceRegistry.cs:302–312, 507–526`: the registry asks the runner for a channel
    and no longer builds `HttpClient`.
  - `VsphereConnectionProbe.cs:62–83, 201–216`.
  - `tools/EnterpriseObservatory.VsphereProbe/Program.cs:104–140`: `LogoutAndConfirmAsync`
    becomes `channel.CloseAndConfirmAsync` with a vendor "still signed in?" callback.
  - Five test construction helpers.
- **Risk.** **High. This is the PR ADR-0025 calls a rewrite.**
  - Every vSphere call changes path.
  - The #53 race and the T0.5 leak were both found here.
  - Mitigation: the two session contract cases already exist and are vendor-neutral in
    intent. This PR makes them runner tests *plus* vSphere-fixture tests, so a regression
    shows in both.
- **E proves.**
  - The whole transport suite passes against the new construction: dispose-without-read
    silent, healthy read, cut-off read leaks nothing, invalid property fails the whole read,
    malformed reply never empty `Ok`, two calls → one re-login.
  - `VsphereSessionCleanupTests` and `VsphereSessionRaceTests` pass after moving to the
    channel.
- **New contract cases.**
  1. *Logout and a concurrent re-login cannot both win.* After close, no call re-opens a
     session. This is the :2244 shape.
  2. *A client that never logged in sends nothing on close* (today's :2237 guard) is lifted
     into the suite.
  3. *The collector cannot construct a transport.* This is an architecture test:
     `Collectors.*` has no reference to `System.Net.Http.HttpClient` outside tests. ADR-0025
     wants this enforced structurally, not by review.

### 3.4 F4 — (b) Server-side cleanup scope (after F3)

- **Scope.** Every read gets an `IReadScope` from the runner. A server-side object is
  **registered** the moment it exists:

  ```csharp
  var view = scope.Register("ContainerView " + moRef, (wire, ct) => wire.SendAsync(DestroyView(moRef), ct));
  // …last page consumed…
  token.Released();          // paging token: the server already let go; nothing to send
  ```

  The runner releases whatever is still registered in each of these cases:
  - when the read returns, faults or is cancelled, with a **fresh bounded token**. This is
    today's `CleanupGrace`, now in one place;
  - when an **abandoned** read finally completes, as a continuation on the read task;
  - before logout, as a sweep.

  The four `finally` blocks (:771, :903, :1317, :1863) and `TryCleanUpAsync` (:822–844) are
  deleted.
- **Signature.** The scope has to be per read, so `IInventorySource.ReadAsync`,
  `IObservationSource.ReadAsync` and `IEventSource.ReadAsync` gain a read-context parameter.
  This is the one Application-port change in F. The fakes in the contract fixtures get the
  parameter and ignore it.
- **Files.** `VsphereClient.cs` (four sites + helper, about 60 lines out), `CollectionPorts.cs:30–60`,
  `EventCollection.cs:140–150`, `SourceRunner.cs`, the three pipelines and the fixtures.
- **Risk.** Medium.
  - The paging token has to be released *only if still open*. Today that is decided by
    `open` at :883/:898. With a registration, the collector has to call `Released()` on the
    last page. If it forgets, a spurious `CancelRetrievePropertiesEx` is sent. That is
    harmless (the fault is swallowed), but it is noise. A contract case pins it.
- **E proves.** *"A read cut off mid-cycle leaves no server-side object behind"* passes
  unchanged.
- **New contract cases.**
  1. *An abandoned read's objects are released when it eventually returns, and before
     logout.*
  2. *Cleanup is sent with a live token even when the read's token was cancelled.* This is
     T0.5 pinned as a contract, not a vSphere test.
  3. *A fully consumed paging token sends no cancel.*

### 3.5 F5 — (c) Learned state into the runner's per-source state (after F3)

- **Scope.** The runner keeps an `ISourceState` per `(instance, role)`. It lives exactly as
  long as the channel's session generation *or* the process, whichever the item declares.
  Five items move into it:
  - `LearnedBatchSizes` → a generic **learned-limit slot**, keyed by `(role, key)`.
    - It has three operations: remembered value, `Reduce()` on a refusal, and `Remember()`
      after a success.
    - The collector still supplies the planned value (`AdaptiveBatchSizer.Initial`, the
      formula, §4) and the refusal classifier.
    - `AdaptiveBatchSizer`'s halving loop is generic and moves too. It goes into a
      `Collectors.Shared`-free place in Application, because T2.2's "shared part" is simply
      the runner now.
  - High-water marks → the runner's **mark book**.
    - Seed once from the store: the runner reads `LatestSampleTimes`, so the collector no
      longer touches `ICollectionGapStore`. See §1.4.
    - Hand the marks to the read.
    - Advance only after the store acknowledges. This is today's `ObservationBatch.Stored`
      callback, which becomes the runner's commit step.
  - Unfilled-slot bookkeeping → the mark book's **pending-slot list** (§4.2 says what stays).
    - Covered: pending slots, re-ask until the lookback, "given up" counted as dropped.
    - Detection stays in the collector: the parser deciding that a slot is unfilled because
      vCenter sent `-1`.
  - `ProbeMemory` → a runner-held **state cell**. What moves is the *storage and its
    lifetime*. The probe, its interval and its trust rules stay (§4.3).
  - `ReadCursor` and the counter catalogue cache → state cells.
  - All locks inside `VsphereObservationSource` (:141, :324, :1226) are then deleted **if**
    the next item is accepted.
- **Proposed with it: no overlapping reads.** The locks exist only because an abandoned read
  and the next cycle's read run in one object (`VsphereObservationSource.cs:108–137`).
  - Telegraf never runs two reads of one input at once. It skips the cycle and counts it
    (§10.2).
  - The runner already knows when it abandoned a read. It can skip that source's next cycle
    while the read is still in flight, count it, and report it as `IsBackingOff`-like health.
  - This is a **behaviour change** (a skipped cycle instead of an overlapping read). It is
    named as such and is open question Q3.
- **Files.**
  - `VsphereObservationSource.cs:94–190, 310–530, 700–800, 941–955, 1224–1260, 1540–1600`.
  - `AdaptiveBatchSizer.cs`.
  - `VsphereClient.cs:88, 111–128`.
  - `SourceRunner.cs` and `ObservationCollectionPipeline.cs`.
- **Risk.** Medium to high. The late-slot logic (#76, H3) is subtle and freshly fixed.
  - Mitigation: the contract case *"a sample returned with placeholders is read again and
    each value written exactly once"* already exists (`ObservationContractTests.cs:119`). It
    must pass unchanged, and it is the main proof for this PR.
- **E proves.** All observation contract cases, including the late-value and
  long-run-stability ones. Nothing is added to the persisted schema.
- **New contract cases.**
  1. *The collector does not read or write the product's store.* This is an architecture
     test: no `ICollectionGapStore` reference in `Collectors.*`.
  2. *A learned batch size survives across reads and resets when the session changes.*
  3. *Marks advance only after a store acknowledgement* (lifted from the store-error-balance
     test).
  4. If Q3 is accepted: *a source still inside an abandoned read is skipped next cycle and
     counted*. This is §10.2's "yavaş kaynak atlanır ve sayılır", the half the suite does not
     yet have.

### 3.6 F6 — (d) Self-metrics, last (needs F3; the held-session count needs F3/F4)

- **Scope.** The runner produces, per source and cycle, ADR-0025 §5's list:
  - answered y/n, duration, samples read/unread, skipped cycles, last success, freshness;
  - **held sessions**: the channel knows the opens and closes. It needs F3.
  - **server–local clock skew**: the channel asks the protocol for server time when it
    probes the session. For vSphere that is `CurrentTime`, the same call §10.3 recommends as
    the session probe. It needs F3.
  - the **orphaned server objects** the scope released on sweep or after abandonment. It
    needs F4.
- The series name `collector.clockSkew.latest` and its entity (the vCenter) stay the same,
  so charts and the D screen do not change.
- The collector's `ClockSkew` (:957) and its call site (:262–263) are deleted.
- The server's "now" reaches the read as `context.ServerNowUtc`. Today the observation source
  fetches it itself (:204) to end its windows.
- `IVsphereApi.GetServerTimeAsync` (the default method at `IVsphereApi.cs:77`) is removed.
- **Files.** `VsphereObservationSource.cs:200–265, 957–970`, `IVsphereApi.cs:70–78`, `SourceRunner.cs`,
  and package D's consumers (`MonitoringCycle`, `/health`).
- **Risk.** Low to medium. Clock skew goes from one per observation read to one per session
  probe. The cadence is the same because both happen once a cycle, but the **timestamp
  source** changes. The test pins the value, not the call site.
- **E proves.** *Self-metrics are present after every result, healthy or not* is extended to
  assert the skew series and the held-session gauge for a healthy source, **with the fake
  collector emitting neither**.
- **New contract case.** *Self-metrics are produced by the runner even when the collector
  emits none.* The vSphere fixture and a null fixture both pass it.

### 3.7 Size of `VsphereClient` afterwards

| Leaves | Lines (approx.) |
|---|---|
| Session: fields, `EnsureSessionAsync`, `SendAsync` (:85–93, :1872–1968) | ~110 |
| HTTP half of `PostAsync`, `ReadBoundedAsync`, `EncodingFor` (:1970–2110, SOAP fault part stays) | ~110 |
| `CreateHandler`, `LogoutAndConfirmAsync`, `LogoutAsync`, `Dispose` (:2120–2276, minus `ReadSessionsAsync`) | ~130 |
| `TryCleanUpAsync`, `CleanupGrace`, `TryDestroy*`, four `finally` blocks (net of the `Register` calls) | ~60 |
| Counter-catalogue cache (moves to a state cell) | ~10 |
| **Total** | **~420** |

That leaves **≈ 1,850 lines**. With the hand-written `Classify` adapter at about +30, the
result is ~1,800–1,880. What remains is pure vim25: envelopes, fault decoding, the property
lists, the payload mappers, `MeasureCoverage` and the event walk. A further split, for example
moving the mappers into `VsphereClient.Mapping.cs` the way `.Candidates.cs` already is, is
file hygiene and not F.

## 4. What must **not** move into the runner

Only mechanisms that a second collector, Redfish, would use *identically* move. Where a
mechanism has a vSphere-specific parameter, the mechanism moves and the parameter stays as a
vendor-supplied callback.

1. **SOAP fault text and type matching, and the `maxQueryMetrics` formula.**
   - What stays:
     - `VsphereSoapFaultReader`, `VsphereFaultKind`, `AdaptiveBatchSizer.IsQuerySizeRefusal`
       (`AdaptiveBatchSizer.cs:119–165`: "restricted by administrator" with **no "the"**,
       per KB 301449);
     - `RefusalFaultType`;
     - `Initial` (:81–95): `-1` → 8 × 256, unreadable → 256, × 0.8 / counter count.
   - Why:
     - Redfish has no query-size limit. Its failures are HTTP status codes plus
       `@Message.ExtendedInfo`.
     - The formula encodes one vCenter setting's semantics, which even Telegraf's own code
       comment admits it does not fully understand (§10.3).
   - The **learned-limit slot** (remember, reduce on refusal, reset with the session) is
     generic and moves. Its *seed* and its *refusal test* are callbacks the collector
     supplies.
2. **`EventMark` and the collector / latest page / walk-back protocol.**
   - What stays: `ReadEventsAsync` (`VsphereClient.cs:1783–1867`). That covers
     `CreateCollectorForEvents`, `SetCollectorPageSize`, `RetrieveLatestEventPage`, the
     `ReadPreviousEvents` walk back to the mark, the key-reset rule (:1816), and
     `EventWindowOverlap`.
   - Why: the `(Key, CreatedAtUtc)` mark exists because vCenter numbers its events in order
     (`EventCollection.cs:3–12`). Redfish's `LogServices/…/Entries` pages forwards by `$skip`
     with an `Id` that is not ordered across resets.
   - The runner owns the *cursor's persistence and "move only after store"*, which the
     pipeline already does. It does not own the mark's shape: `EventMark` stays opaque to it.
3. **`ProbeMemory`'s hourly "which counters exist" probe.**
   - What stays: `QueryAvailablePerfMetric`, trusting an empty answer until data contradicts
     it (:1451), `RecheckInterval` (:92), and "the probe was proven wrong" (:1591).
   - Why: this exists because vCenter's availability probe is known to lie per entity type.
     Redfish advertises what a BMC has in its schema.
   - Only the *cell it is kept in* and that cell's lifetime move (§3.5).
4. **`ContainerView` and paging-token types.**
   - What stays: `CreateContainerView`, `DestroyView`, `ContinueRetrievePropertiesEx`,
     `CancelRetrievePropertiesEx`, the `EventHistoryCollector`, and the rule that a token
     is released only while open.
   - Why: these are vim25 managed objects.
   - What generalises is only the contract *"register a server-side handle with a release
     action; the runner guarantees the release"*. On Redfish the one such handle is the
     session itself, plus a task monitor if a collector ever starts a task.
5. **Session *content*** (`VsphereServiceContent`, `Login`, `SessionManager`, the `vmware_soap_session`
   cookie). The runner owns *when and under which lock*. The protocol owns *how*. Redfish
   uses `X-Auth-Token` from `POST /SessionService/Sessions` and ends it with `DELETE`.

The test for anything not listed here: **would a Redfish author write the same code?** If they
would, it moves. If only the shape is shared, the shape becomes a callback.

## 5. What a collector author writes (Redfish example, hypothetical)

```csharp
// The whole Redfish observation collector, under the runner F leaves behind.
public sealed class RedfishSessionProtocol : ISessionProtocol<RedfishSession>
{
    public async Task<RedfishSession> OpenAsync(IRawExchange wire, CancellationToken ct)
    {
        var reply = await wire.SendAsync(Post("/redfish/v1/SessionService/Sessions", Credential()), ct);
        return new RedfishSession(reply.Header("X-Auth-Token"), reply.Header("Location"));
    }

    public Task CloseAsync(IRawExchange wire, RedfishSession s, CancellationToken ct) =>
        wire.SendAsync(Delete(s.Location), ct);

    public ReplyVerdict Classify(RawReply reply) => reply.Status switch
    {
        401 => ReplyVerdict.SessionExpired,
        403 => ReplyVerdict.Fault(new RedfishFault(CollectionFailureKind.PermissionDenied, reply)),
        >= 400 => ReplyVerdict.Fault(RedfishFault.From(reply)),   // @Message.ExtendedInfo
        _ => ReplyVerdict.Ok,
    };
}

public sealed class RedfishObservationSource : IObservationSource
{
    public string InstanceId { get; init; }

    public async Task<ObservationBatch> ReadAsync(IReadContext ctx, CancellationToken ct)
    {
        var failures = new List<CollectionFailure>();
        var samples  = new List<Observation>();

        foreach (var chassis in await ctx.Channel.GetJsonAsync<Collection>("/redfish/v1/Chassis", ct))
        {
            try
            {
                var power = await ctx.Channel.GetJsonAsync<Power>(chassis + "/Power", ct);
                samples.AddRange(Map(power, ctx.ServerNowUtc));   // sample time = BMC's clock
            }
            catch (RedfishFault f) when (f.Kind != CollectionFailureKind.AuthenticationRejected)
            {
                failures.Add(CouldNotRead(chassis, f));          // one chassis, not the BMC
            }
        }

        return new ObservationBatch { SourceInstanceId = InstanceId, Observations = samples, Failures = failures };
    }
}
```

**What they get without writing it:**
- a schedule, a hard timeout with cooperative grace, retry with jitter inside one budget, the
  breaker, and the one-strike rule on a rejected credential;
- the per-source request ceiling;
- `HttpClient`, TLS policy, cookie/token plumbing and the bounded body read;
- a session opened, probed, renewed once under one lock, and closed on retire or shutdown;
- server-side handles released on every exit path, including abandonment;
- learned-limit slots and high-water marks seeded from the store and advanced only after it
  acknowledges;
- self-metrics: up, duration, read/unread, skipped cycles, last success, freshness, held
  sessions, clock skew from the BMC's `DateTime`;
- the contract suite. Writing an `IObservationContractFixture` and an
  `ITransportContractFixture` over a fake BMC is the entry ticket.

**What they must not do. Once F lands, the structure makes most of these impossible:**
- `new HttpClient` (the architecture test fails);
- log in, log out or retry on their own;
- `Task.Delay` or a loop that waits;
- hold a field that changes between reads: state goes in `ctx.State`;
- read or write the product's store;
- start a background task;
- turn a fault into an empty result, which is a parse or wire error and never `Ok([])`;
- substitute a value for one that was not read;
- decide entity identity (ADR-0005 §3, unchanged).

## 6. Two related items and where they land

- **Per-source event read watermark (ADR-0026 note §5.7). Separate, directly after F1.**
  - What it needs:
    - `vcenter-events` has to be able to say `SourceSilent` when a source's events were not
      read this cycle.
    - Today such a source gets `NotReported`.
  - Once F1 exists, the runner writes an **Events-role health row** per source per cycle.
    `EventCursor` already has `LastSuccessUtc` (`EventCollection.cs:155–170`). The watermark
    is "this cycle's successful event read for source S covered up to T", exposed to
    `MonitoringCycle` next to `ReportingSources`.
  - It is not in F1, because F1 is behaviour-unchanged and the watermark changes rule
    outcomes (ADR-0026's domain).
  - It does not wait for F3–F6, because it depends on nothing they move.
- **Count Coverage per object, not per type (ADR-0026 note (10) follow-up). Separate,
  independent of F.**
  - The current state: `MeasureCoverage` (`VsphereClient.cs:528–560`) aggregates `Asked`/`Answered`
    per (object type, property) across all objects. It cannot tell a datastore that answered
    `summary.uncommitted` as empty from one that did not answer.
  - The fix, in the parser and coverage record, is to record per object that an *optional*
    property was requested and came back unset. It also adds a per-object dimension to
    `PropertyCoverage`.
  - That is parsing, which stays in the collector (§4). It shares no lines with F3's session
    block or F4's finally blocks.
  - It can go before or after F. If it overlaps F3 in time, it should wait for F3 only to
    avoid a rebase on `VsphereClient.cs`, not for any design reason.

## 7. Open questions

1. **F1: remove the borrowed inventory verdict?** Once events have their own breaker, is
   `answered` (`EventCollection.cs:401`) removed? Or is it kept, so that a vCenter whose
   inventory just failed is not asked for events either?
   - Recommendation: keep it in F1 and remove it in a follow-up after the Events health row
     has been seen in production.
2. **F1: Events role on the collectors screen.** Show a third row, or fold events into the
   inventory row? The row is shown today as `Role.ToString()`, `ReadModel.cs:964`.
3. **F5: skip the next cycle while an abandoned read is in flight?** This is Telegraf's rule
   and removes every lock in `VsphereObservationSource`. The cost is one skipped 30-second
   cycle per abandonment, counted and visible, instead of a quiet overlapping read.
   - Recommendation: yes.
4. **F4: explicit read-context parameter versus an ambient (`AsyncLocal`) scope.** Ambient
   avoids changing three Application ports and every fake. Explicit is visible, and an
   abandoned read keeps its own flow either way.
   - Recommendation: explicit. Hidden flow is the thing ADR-0025 argues against.
5. **The result sum type (ADR-0025 §2, `Ok`/`Partial`/`Failed`).** It is not in F3–F6 above,
   because today's `Failures` list plus a thrown fault already covers the three cases.
   - Proposal: a small **F7** after F6 that flips the three result records to the sum type,
     before M6 starts, so Redfish is written once against it.
6. **The bounded store queue (ADR-0025 §6).** It belongs to the runner but not to *collector*
   authority. Does it go into F (as F8), or into D, which already owns "dropped samples
   counted and alarmed"?
   - Recommendation: D.
7. **F2 default and naming.** Is `MaxRequestsPerSource = 4` acceptable as the behaviour-neutral
   default until A′ measures? And is `CollectionPolicy.MaxConcurrency` renamed now, to
   `MaxSourcesInParallel`, or only re-documented?
8. **F3: where does the TLS decision live?** `CreateHandler`'s relaxed-certificate switch
   moves to the runner's channel. Package G's thumbprint pinning then lands there once, for
   both collectors. Is G sequenced after F3 to avoid doing it twice?
