-- storage-latency-blind-spot: window-ratio hysteresis replay.
--
-- Read-only. Replays the rule over the RAW samples it reads, not over
-- alert_history: since #85 a quiet cycle is Unknown(NotJudgeable) and writes no
-- history row, so alert_history no longer shows the flaps this replay is about.
-- Raw samples are kept 2 days (ADR-0017); the replay covers whatever is there.
--
-- What it reconstructs, per volume and per 30 s metric cycle, is the verdict of
-- StorageLatencyBlindSpot.Judge in its own order (ADR-0026 design note §7):
--
--   latency readings = 0      -> NotReported             (no verdict, no window)
--   latency readings < 3      -> NotJudgeable:readings   (not in the window)
--   worst latency >= 1 ms     -> Measured:latency        (window: measurable)
--   SIOC active >= 1 %        -> Measured:sioc           (window: measurable)
--   no load counter           -> InputNotCollected:load  (not in the window)
--   load < 1 op/s             -> NotJudgeable:quiet      (not in the window)
--   no SIOC counter           -> InputNotCollected:sioc  (not in the window)
--   otherwise                 -> Blind                   (window: not measurable)
--
-- "Judged" below means Measured or Blind: the cycles that enter the window.
-- The window-ratio state after a judged cycle is: fewer than K judged cycles
-- so far -> NULL (window filling, the rule says NotJudgeable); otherwise
-- Absent if at least P % of the last K judged cycles were measurable, else
-- Present. "current" is the rule as it is on main, which is the same thing
-- with K = 1.
--
-- Which sample a cycle read: the rule is handed only the newest sample of each
-- series (older ones are backfill, CollectionPorts.cs). A cycle at t is modelled
-- as reading, per series, the newest sample in (t - 30 s, t], on a 30 s grid
-- aligned to the epoch. With the 20 s real-time interval every such bucket
-- holds one or two samples, so this is the rule's view up to the grid's phase.
-- The same counters and the same volume (series.entity_id, the observation's
-- entity) and vantage-point (instance <> '') mapping as the §7 query.
--
-- Flips:
--   verdict_flips   Present <-> Absent changes of the per-cycle state over the
--                   judged cycles (window-filling NULLs skipped). What the rule
--                   says, before reconciliation.
--   alert_flips     the same sequence with the reconciler's two-in-a-row on
--                   both sides (Warning opens after 2 consecutive Present hits,
--                   the rule resolves after N = 2 consecutive absences; Unknown
--                   in between neither counts nor resets): a flip is a run of
--                   >= 2 of the other state. An approximation of what an
--                   operator sees as open/resolve; it ignores the Warning
--                   minimum duration and operator actions.
--   The replay assumes no product restart inside the window (a restart would
--   empty the in-memory window: NotJudgeable for K judged cycles).
--
-- True blind: a run of consecutive judged cycles with no measurable one,
-- spanning at least 30 minutes of wall-clock time between its first and its
-- last judged cycle. Quiet cycles in between do not break it and do not
-- count. Measured on this estate, judged cycles land roughly 1.1 a minute per
-- volume (most 30 s buckets are quiet, not judged), so a wall-clock threshold
-- is used rather than a fixed judged-cycle count: a count built on the 30 s
-- grid (60 cycles) assumes two judged cycles a minute and demands about twice
-- the real elapsed time, which silently drops every true-blind stretch under
-- roughly an hour and was why this section came back empty.
--
-- Output: one result set, sections in column "section" (see the end).

SET default_transaction_read_only = on;
SET work_mem = '256MB';

WITH
counters AS MATERIALIZED (
    SELECT s.id,
           s.entity_id AS volume,
           CASE
               WHEN s.counter LIKE 'datastore.total%Latency.average'   THEN 'lat'    -- latency, ms
               WHEN s.counter LIKE 'datastore.number%Averaged.average' THEN 'load'   -- load, op/s
               ELSE 'sioc'                                                           -- SIOC active, %
           END AS kind
    FROM series s
    WHERE s.instance <> ''                                                           -- from a vantage point (a host)
      AND (s.counter LIKE 'datastore.total%Latency.average'
           OR s.counter LIKE 'datastore.number%Averaged.average'
           OR s.counter = 'datastore.siocActiveTimePercentage.average')
),
bucketed AS (
    SELECT x.series_id,
           x.value,
           ((x.at_utc + 29) / 30) * 30 AS cycle_s,                                   -- the cycle at t reads (t - 30, t]
           lead(((x.at_utc + 29) / 30) * 30) OVER (PARTITION BY x.series_id ORDER BY x.at_utc) AS next_cycle_s
    FROM sample x
    WHERE x.series_id IN (SELECT id FROM counters)
),
reading AS (
    -- The newest sample of each series in each cycle: the one the rule saw.
    SELECT c.volume, c.kind, b.cycle_s, b.value
    FROM bucketed b
    JOIN counters c ON c.id = b.series_id
    WHERE b.next_cycle_s IS DISTINCT FROM b.cycle_s
),
cycle AS (
    SELECT volume, cycle_s,
           count(*)   FILTER (WHERE kind = 'lat')  AS lat_n,
           max(value) FILTER (WHERE kind = 'lat')  AS worst_ms,
           sum(value) FILTER (WHERE kind = 'load') AS load_ops,
           max(value) FILTER (WHERE kind = 'sioc') AS sioc_pct
    FROM reading
    GROUP BY volume, cycle_s
),
verdict AS MATERIALIZED (
    SELECT volume, cycle_s,
           CASE
               WHEN lat_n = 0            THEN 'NotReported'
               WHEN lat_n < 3            THEN 'NotJudgeable:readings'
               WHEN worst_ms >= 1        THEN 'Measured:latency'
               WHEN sioc_pct >= 1        THEN 'Measured:sioc'
               WHEN load_ops IS NULL     THEN 'InputNotCollected:load'
               WHEN load_ops < 1         THEN 'NotJudgeable:quiet'
               WHEN sioc_pct IS NULL     THEN 'InputNotCollected:sioc'
               ELSE                           'Blind'
           END AS verdict
    FROM cycle
),
span AS (
    SELECT volume, greatest((max(cycle_s) - min(cycle_s) + 30) / 86400.0, 1 / 1440.0) AS days
    FROM verdict
    GROUP BY volume
),
estate AS (
    SELECT greatest((max(cycle_s) - min(cycle_s) + 30) / 86400.0, 1 / 1440.0) AS days FROM verdict
),
judged AS MATERIALIZED (
    SELECT volume, cycle_s, m,
           row_number() OVER w                                              AS rn,
           sum(m) OVER (w ROWS BETWEEN  9 PRECEDING AND CURRENT ROW)        AS m10,
           sum(m) OVER (w ROWS BETWEEN 19 PRECEDING AND CURRENT ROW)        AS m20,
           sum(m) OVER (w ROWS BETWEEN 29 PRECEDING AND CURRENT ROW)        AS m30
    FROM (
        SELECT volume, cycle_s, (verdict LIKE 'Measured%')::int AS m
        FROM verdict
        WHERE verdict = 'Blind' OR verdict LIKE 'Measured%'
    ) j
    WINDOW w AS (PARTITION BY volume ORDER BY cycle_s)
),
variant (name, k, p) AS (
    VALUES ('current',    1,  0),
           ('K=10 P=50', 10, 50),
           ('K=10 P=70', 10, 70),
           ('K=20 P=50', 20, 50),
           ('K=20 P=70', 20, 70),
           ('K=30 P=50', 30, 50),
           ('K=30 P=70', 30, 70)
),
state AS MATERIALIZED (
    SELECT j.volume, j.cycle_s, j.rn, j.m, v.name AS variant,
           CASE
               WHEN v.k = 1 THEN CASE j.m WHEN 1 THEN 'Absent' ELSE 'Present' END
               WHEN j.rn < v.k THEN NULL                                             -- window filling
               WHEN 100.0 * (CASE v.k WHEN 10 THEN j.m10 WHEN 20 THEN j.m20 ELSE j.m30 END) >= v.p * v.k
                   THEN 'Absent'
               ELSE 'Present'
           END AS state
    FROM judged j
    CROSS JOIN variant v
),
flip AS (
    SELECT volume, variant,
           (state <> lag(state) OVER w)::int AS flipped                              -- NULL on the first: not a flip
    FROM state
    WHERE state IS NOT NULL
    WINDOW w AS (PARTITION BY volume, variant ORDER BY cycle_s)
),
island AS (
    SELECT volume, variant, state, cycle_s,
           row_number() OVER (PARTITION BY volume, variant ORDER BY cycle_s)
         - row_number() OVER (PARTITION BY volume, variant, state ORDER BY cycle_s) AS grp
    FROM state
    WHERE state IS NOT NULL
),
run AS (
    SELECT volume, variant, state, min(cycle_s) AS start_s, count(*) AS len
    FROM island
    GROUP BY volume, variant, state, grp
),
confirmed AS (
    SELECT volume, variant,
           (state <> lag(state) OVER (PARTITION BY volume, variant ORDER BY start_s))::int AS flipped
    FROM run
    WHERE len >= 2
),
flip_count AS (
    SELECT volume, variant, coalesce(sum(flipped), 0) AS flips FROM flip GROUP BY volume, variant
),
confirmed_count AS (
    SELECT volume, variant, coalesce(sum(flipped), 0) AS flips FROM confirmed GROUP BY volume, variant
),
per_volume AS (
    SELECT s.volume, s.variant, sp.days,
           count(*)                                                    AS judged_cycles,
           100.0 * avg(s.m)                                            AS measurable_pct,
           100.0 * avg((s.state = 'Present')::int)                     AS present_pct,
           100.0 * avg((s.state IS NULL)::int)                         AS filling_pct,
           coalesce(max(fc.flips), 0)                                  AS verdict_flips,
           coalesce(max(cc.flips), 0)                                  AS alert_flips
    FROM state s
    JOIN span sp ON sp.volume = s.volume
    LEFT JOIN flip_count fc      ON fc.volume = s.volume AND fc.variant = s.variant
    LEFT JOIN confirmed_count cc ON cc.volume = s.volume AND cc.variant = s.variant
    GROUP BY s.volume, s.variant, sp.days
),
blind_island AS (
    SELECT volume, cycle_s, rn, m,
           rn - row_number() OVER (PARTITION BY volume, m ORDER BY cycle_s) AS grp
    FROM judged
),
stretch AS (
    SELECT volume, grp,
           min(cycle_s) AS start_s, max(cycle_s) AS end_s,
           min(rn) AS start_rn, max(rn) AS end_rn,
           count(*) AS cycles
    FROM blind_island
    WHERE m = 0
    GROUP BY volume, grp
    HAVING max(cycle_s) - min(cycle_s) + 30 >= 1800                                   -- >= 30 min wall-clock, not a cycle count
),
stretch_state AS (
    SELECT st.volume, s.variant, st.start_s, st.end_s, st.cycles,
           100.0 * avg((s.state = 'Present')::int)                                  AS captured_pct,
           min(s.rn) FILTER (WHERE s.state = 'Present') - st.start_rn + 1          AS cycles_to_present,
           (min(s.cycle_s) FILTER (WHERE s.state = 'Present') - st.start_s) / 60.0 AS minutes_to_present,
           max(s.state) FILTER (WHERE s.rn = st.end_rn)                             AS state_at_end
    FROM stretch st
    JOIN state s ON s.volume = st.volume AND s.rn BETWEEN st.start_rn AND st.end_rn
    GROUP BY st.volume, s.variant, st.start_s, st.end_s, st.cycles, st.start_rn, st.end_rn
),
open_alert AS (
    SELECT DISTINCT a.entity_id AS volume
    FROM alert_instance a
    WHERE a.state = 'Open'
      AND (a.rule_id = 'storage-latency-blind-spot' OR a.fingerprint LIKE '%|storage-latency-blind-spot')
      AND a.entity_id IS NOT NULL
),
latest AS (
    SELECT DISTINCT ON (volume, variant) volume, variant, cycle_s, state
    FROM state
    ORDER BY volume, variant, cycle_s DESC
)

-- 1 verdict-mix: how many volume-cycles fell in each verdict (the gates above).
SELECT '1 verdict-mix'::text AS section, NULL::text AS variant, NULL::text AS volume,
       NULL::numeric AS days, count(*)::bigint AS judged_cycles,
       NULL::numeric AS measurable_pct, NULL::numeric AS present_pct, NULL::numeric AS filling_pct,
       NULL::bigint AS verdict_flips, NULL::numeric AS verdict_flips_per_day,
       NULL::bigint AS alert_flips, NULL::numeric AS alert_flips_per_day,
       NULL::timestamptz AS stretch_start, NULL::numeric AS stretch_minutes, NULL::bigint AS stretch_cycles,
       NULL::numeric AS captured_pct, NULL::bigint AS cycles_to_present, NULL::numeric AS minutes_to_present,
       NULL::timestamptz AS latest_cycle, verdict AS state
FROM verdict
GROUP BY verdict

UNION ALL
-- 2 totals: the estate per variant. Flips per day over the estate's span.
SELECT '2 totals', pv.variant, NULL,
       round(max(e.days), 3), sum(pv.judged_cycles)::bigint,
       round(sum(pv.measurable_pct * pv.judged_cycles) / nullif(sum(pv.judged_cycles), 0), 1),
       round(sum(pv.present_pct * pv.judged_cycles) / nullif(sum(pv.judged_cycles), 0), 1),
       round(sum(pv.filling_pct * pv.judged_cycles) / nullif(sum(pv.judged_cycles), 0), 1),
       sum(pv.verdict_flips)::bigint, round(sum(pv.verdict_flips) / max(e.days), 1),
       sum(pv.alert_flips)::bigint,   round(sum(pv.alert_flips) / max(e.days), 1),
       NULL, NULL, NULL, NULL, NULL, NULL, NULL,
       count(*) || ' volumes; ' ||
       count(*) FILTER (WHERE pv.alert_flips / pv.days >= 1) || ' with >= 1 alert flip a day'
FROM per_volume pv
CROSS JOIN estate e
GROUP BY pv.variant

UNION ALL
-- 3 per-volume: each volume under each variant.
SELECT '3 per-volume', pv.variant, pv.volume,
       round(pv.days, 3), pv.judged_cycles,
       round(pv.measurable_pct, 1), round(pv.present_pct, 1), round(pv.filling_pct, 1),
       pv.verdict_flips::bigint, round(pv.verdict_flips / pv.days, 1),
       pv.alert_flips::bigint,   round(pv.alert_flips / pv.days, 1),
       NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL
FROM per_volume pv

UNION ALL
-- 4 blind-summary: per variant, over the true-blind stretches. stretch_cycles
-- is the number of stretches, captured_pct the share Present at the stretch's
-- end, cycles/minutes_to_present the worst (max) delay; state carries the median.
SELECT '4 blind-summary', ss.variant, NULL,
       NULL, sum(ss.cycles)::bigint, NULL, NULL, NULL, NULL, NULL, NULL, NULL,
       NULL, NULL, count(*)::bigint,
       round(100.0 * avg((ss.state_at_end = 'Present')::int), 1),
       max(ss.cycles_to_present)::bigint, round(max(ss.minutes_to_present), 1),
       NULL,
       'median ' || coalesce(round((percentile_cont(0.5) WITHIN GROUP (ORDER BY ss.minutes_to_present))::numeric, 1)::text, '-')
       || ' min to Present; ' || count(*) FILTER (WHERE ss.cycles_to_present IS NULL) || ' never Present'
FROM stretch_state ss
GROUP BY ss.variant

UNION ALL
-- 5 blind-stretch: each true-blind stretch under each variant.
SELECT '5 blind-stretch', ss.variant, ss.volume,
       NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL,
       to_timestamp(ss.start_s), round((ss.end_s - ss.start_s + 30) / 60.0, 1), ss.cycles,
       round(ss.captured_pct, 1), ss.cycles_to_present::bigint, round(ss.minutes_to_present, 1),
       to_timestamp(ss.end_s), coalesce(ss.state_at_end, 'window filling')
FROM stretch_state ss

UNION ALL
-- 6 open-now: every blind-spot alert Open right now, and the state each variant
-- holds at that volume's latest judged cycle.
SELECT '6 open-now', v.name, o.volume,
       NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL,
       NULL, NULL, NULL, NULL, NULL, NULL,
       to_timestamp(l.cycle_s),
       CASE
           WHEN l.volume IS NULL      THEN 'no judged cycle in the raw window'
           WHEN l.state IS NULL       THEN 'window filling -> NotJudgeable: stays open, stale'
           WHEN l.state = 'Present'   THEN 'Present -> stays open'
           ELSE                            'Absent -> would close (after N = 2)'
       END
FROM open_alert o
CROSS JOIN variant v
LEFT JOIN latest l ON l.volume = o.volume AND l.variant = v.name

ORDER BY section, variant NULLS FIRST, volume NULLS FIRST, stretch_start NULLS FIRST, state;
