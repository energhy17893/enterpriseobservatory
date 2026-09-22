using Npgsql;

namespace EnterpriseObservatory.Persistence.Postgres;

/// <summary>
/// The schema, and the only thing that changes it.
/// </summary>
/// <remarks>
/// <para>
/// Migrations are append-only and never edited once released, for the same
/// reason SQLite's were: an installation that already ran migration 2 will
/// never run it again, so changing it changes nothing except what a reader
/// believes happened.
/// </para>
/// <para>
/// PostgreSQL has no <c>PRAGMA user_version</c>, so the version lives in a
/// table — written in the same transaction as the migration it accounts for.
/// An interrupted upgrade therefore leaves the version at the last migration
/// that actually completed, rather than at the one that was attempted.
/// </para>
/// </remarks>
internal static class PostgresSchema
{
    private static readonly string[] Migrations =
    [
        // --- 1: measurements -------------------------------------------------
        //
        // Shaped by volume in a way the state tables are not. A mid-sized
        // estate produces on the order of ten thousand samples every sampling
        // interval, so the width of one row is the whole design: repeating the
        // entity id and the counter name on every sample would multiply the
        // table by roughly ten and make every range scan read that much more.
        """
        CREATE TABLE series (
            id         bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
            entity_id  text   NOT NULL,
            counter    text   NOT NULL,
            instance   text   NOT NULL,
            unit       text   NOT NULL,
            rollup     text   NOT NULL
        );

        CREATE UNIQUE INDEX ux_series ON series (entity_id, counter, instance);

        -- Listing what was recorded for one entity is how the interface offers
        -- a real menu instead of a fixed one that is wrong for half the kinds.
        CREATE INDEX ix_series_entity ON series (entity_id);

        -- Timestamps as bigint Unix seconds rather than timestamptz. Eight
        -- bytes, integer comparison for the range scan every query performs,
        -- and no time zone to be misread — the product's clock is UTC
        -- throughout and storing an offset would invite the question of which
        -- one applied.
        CREATE TABLE sample (
            series_id bigint           NOT NULL REFERENCES series (id) ON DELETE CASCADE,
            at_utc    bigint           NOT NULL,
            value     double precision NOT NULL,
            PRIMARY KEY (series_id, at_utc)
        );

        -- Five numbers per bucket, not one. Storing only the average is how a
        -- host pinned at 100% for two minutes inside an hour averages to 3%
        -- and disappears. Sum and count rather than the average itself, so a
        -- coarser bucket folds from finer ones exactly — which is what makes
        -- keeping hourly history for years cost almost nothing.
        CREATE TABLE bucket (
            series_id    bigint           NOT NULL REFERENCES series (id) ON DELETE CASCADE,
            resolution   text             NOT NULL,
            start_utc    bigint           NOT NULL,
            min_value    double precision NOT NULL,
            max_value    double precision NOT NULL,
            sum_value    double precision NOT NULL,
            sample_count integer          NOT NULL,
            last_value   double precision NOT NULL,
            PRIMARY KEY (series_id, resolution, start_utc)
        );

        -- Retention deletes by age across every series, which the primary key
        -- cannot serve: it leads with series_id.
        CREATE INDEX ix_bucket_age ON bucket (resolution, start_utc);

        -- How far each resolution has been folded. Written in the same
        -- transaction as the buckets it accounts for, so an interrupted pass
        -- resumes from where it actually got to rather than where it intended.
        CREATE TABLE compaction (
            resolution       text   NOT NULL PRIMARY KEY,
            completed_to_utc bigint NOT NULL
        );
        """,

        // --- 2: state --------------------------------------------------------
        //
        // The six SQLite migrations that built this are not replayed here. They
        // are that database's history, not this one's, and an installation of
        // this build starts empty — so the tables are created in the shape they
        // ended up in. Append-only applies from here.
        //
        // State and measurements share one database, unlike the two SQLite
        // files ADR-0012 separated. That separation was about file mechanics: a
        // metric history churning one file should not be able to take alerting
        // down with it. With a server there is no shared file to contend for,
        // and one database is one backup, one connection and one operational
        // story (ADR-0016).
        //
        // Timestamps here are timestamptz rather than the bigint the
        // measurement tables use. State is small and read by people — an
        // alert's history is evidence in an incident review — so it is worth
        // the width to have the database itself understand the value. The
        // measurement tables made the opposite trade for the opposite reason.
        """
        CREATE TABLE entity (
            id                 text        NOT NULL PRIMARY KEY,
            kind               text        NOT NULL,
            display_name       text        NOT NULL,
            source_instance_id text        NOT NULL,
            health             text        NOT NULL,
            observation_state  text        NOT NULL,
            last_seen_utc      timestamptz NOT NULL
        );

        CREATE INDEX ix_entity_source ON entity (source_instance_id);
        CREATE INDEX ix_entity_kind ON entity (kind);

        CREATE TABLE identity_mark (
            entity_id text NOT NULL REFERENCES entity (id) ON DELETE CASCADE,
            kind      text NOT NULL,
            value     text NOT NULL,
            source    text NOT NULL,
            PRIMARY KEY (entity_id, kind, value, source)
        );

        -- No foreign key to entity: a relationship may be observed before
        -- either end has been collected, and refusing it would let the order
        -- two independent collectors happen to run in decide what the graph
        -- contains.
        CREATE TABLE relationship (
            from_id         text        NOT NULL,
            to_id           text        NOT NULL,
            kind            text        NOT NULL,
            observed_at_utc timestamptz NOT NULL,
            PRIMARY KEY (from_id, to_id, kind)
        );

        CREATE INDEX ix_relationship_to ON relationship (to_id);

        CREATE TABLE relationship_evidence (
            from_id     text NOT NULL,
            to_id       text NOT NULL,
            kind        text NOT NULL,
            mark_kind   text NOT NULL,
            mark_value  text NOT NULL,
            mark_source text NOT NULL,
            PRIMARY KEY (from_id, to_id, kind, mark_kind, mark_value, mark_source),
            FOREIGN KEY (from_id, to_id, kind)
                REFERENCES relationship (from_id, to_id, kind) ON DELETE CASCADE
        );

        CREATE TABLE alert_instance (
            fingerprint             text        NOT NULL PRIMARY KEY,
            scope                   text        NOT NULL,
            severity                text        NOT NULL,
            state                   text        NOT NULL,
            title                   text        NOT NULL,
            description             text        NOT NULL,
            category                text        NOT NULL,
            source                  text        NOT NULL,
            entity_id               text        NULL,
            is_derived              boolean     NOT NULL,
            consecutive_hits        integer     NOT NULL,
            is_confirmed            boolean     NOT NULL,
            cleared_by_operator     boolean     NOT NULL,
            pending_notification    text        NOT NULL,
            suppressed_by_window_id text        NULL,
            first_seen_utc          timestamptz NOT NULL,
            last_seen_utc           timestamptz NOT NULL,
            silenced_until_utc      timestamptz NULL
        );

        -- A reconciliation reads one scope at a time and treats what it reads
        -- as the whole truth, so this index is on the path of every cycle.
        CREATE INDEX ix_alert_scope ON alert_instance (scope);
        CREATE INDEX ix_alert_entity ON alert_instance (entity_id);

        CREATE TABLE alert_transition (
            fingerprint text        NOT NULL REFERENCES alert_instance (fingerprint) ON DELETE CASCADE,
            ordinal     integer     NOT NULL,
            from_state  text        NOT NULL,
            to_state    text        NOT NULL,
            reason      text        NOT NULL,
            at_utc      timestamptz NOT NULL,
            actor       text        NULL,
            PRIMARY KEY (fingerprint, ordinal)
        );

        CREATE TABLE flap_history (
            fingerprint text NOT NULL PRIMARY KEY,
            scope       text NOT NULL,
            object_name text NOT NULL
        );

        CREATE INDEX ix_flap_scope ON flap_history (scope);

        CREATE TABLE flap_cessation (
            fingerprint   text        NOT NULL REFERENCES flap_history (fingerprint) ON DELETE CASCADE,
            ordinal       integer     NOT NULL,
            ceased_at_utc timestamptz NOT NULL,
            PRIMARY KEY (fingerprint, ordinal)
        );

        -- Keyed by source and role together: one vCenter is read by two
        -- collectors that fail independently. See ADR-0009.
        CREATE TABLE collector_health (
            instance_id          text        NOT NULL,
            role                 text        NOT NULL,
            health               text        NOT NULL,
            last_success_utc     timestamptz NULL,
            consecutive_failures integer     NOT NULL,
            is_backing_off       boolean     NOT NULL,
            last_failure_detail  text        NULL,
            last_attempt_utc     timestamptz NULL,
            last_failure_kind    text        NULL,
            PRIMARY KEY (instance_id, role)
        );

        -- Everything a reachable source could not read. All of them, not the
        -- first: keeping only one hid an entire class of measurement behind an
        -- unrelated message about a different entity kind.
        CREATE TABLE collector_partial_failure (
            instance_id text NOT NULL,
            role        text NOT NULL,
            kind        text NOT NULL,
            target      text NOT NULL,
            detail      text NOT NULL,
            PRIMARY KEY (instance_id, role, target, detail)
        );

        CREATE TABLE user_account (
            username           text        NOT NULL PRIMARY KEY,
            password_hash      text        NOT NULL,
            role               text        NOT NULL,
            created_utc        timestamptz NOT NULL,
            last_signed_in_utc timestamptz NULL,
            failed_attempts    integer     NOT NULL,
            locked_until_utc   timestamptz NULL
        );

        CREATE TABLE maintenance_window (
            id              text        NOT NULL PRIMARY KEY,
            title           text        NOT NULL,
            reason          text        NOT NULL,
            start_utc       timestamptz NOT NULL,
            end_utc         timestamptz NOT NULL,
            declared_by     text        NOT NULL,
            declared_at_utc timestamptz NOT NULL
        );

        -- Retention sweeps by end time across every window.
        CREATE INDEX ix_window_end ON maintenance_window (end_utc);

        -- No rows means the window covers the whole estate, which is what a
        -- datacentre power test actually is. Deliberately expressible and
        -- deliberately blunt: everything goes quiet, including the failure the
        -- test was meant to reveal.
        CREATE TABLE maintenance_window_entity (
            window_id text NOT NULL REFERENCES maintenance_window (id) ON DELETE CASCADE,
            entity_id text NOT NULL,
            PRIMARY KEY (window_id, entity_id)
        );

        -- password_protected holds ciphertext, never a password. See ADR-0015
        -- for what that protects and what it does not: it is a narrower claim
        -- than "the password is encrypted", and the narrower one is the true
        -- one.
        CREATE TABLE source_connection (
            instance_id        text        NOT NULL PRIMARY KEY,
            kind               text        NOT NULL,
            base_address       text        NOT NULL,
            username           text        NOT NULL,
            password_protected text        NOT NULL,
            accept_untrusted   boolean     NOT NULL,
            page_size          integer     NOT NULL,
            is_enabled         boolean     NOT NULL,
            created_utc        timestamptz NOT NULL,
            created_by         text        NOT NULL,
            password_set_utc   timestamptz NULL
        );
        """,

        // --- coverage --------------------------------------------------------
        //
        // What each source managed to read, property by property. State rather
        // than history: one row per source, object type and property, replaced
        // every inventory cycle. A time series of coverage would answer "was
        // this readable last Tuesday", which is a real question and a later
        // one; the question this table exists for is whether a rule's silence
        // right now is a verdict or a gap.
        //
        // measured_at_utc is on every row rather than per source, because a
        // report that cannot say when it was taken invites being read as
        // current after the collector has stopped running.
        """
        CREATE TABLE property_coverage (
            instance_id     text        NOT NULL,
            object_type     text        NOT NULL,
            property        text        NOT NULL,
            asked           integer     NOT NULL,
            answered        integer     NOT NULL,
            measured_at_utc timestamptz NOT NULL,
            PRIMARY KEY (instance_id, object_type, property)
        );
        """,

        // --- 4: events -------------------------------------------------------
        //
        // vCenter's event stream, as it said it (roadmap M2.1). History rather
        // than state, and bounded: rows older than the retention window are
        // deleted every inventory cycle, so ix_source_event_created is on the
        // path of both the sweep and the "newest first" listing.
        //
        // The key includes the creation time as well as vCenter's own key.
        // Keys increase within one vCenter's database, but a rebuilt vCenter
        // starts counting again, and keying on the number alone would silently
        // drop every event it wrote that collided with an old one.
        //
        // type_id beside event_class, never instead of it: every esx.problem
        // arrives as the one class EventEx, and the type id is the only column
        // that tells a lost storage path from an isolated host.
        //
        // event_cursor is where each source's stream was read up to, and when
        // it was last read or failed to be. A row per source that has ever been
        // asked, including one that has only ever failed — "could not read" has
        // to be sayable, and an absent row cannot say it.
        """
        CREATE TABLE source_event (
            source_instance_id    text        NOT NULL,
            event_key             bigint      NOT NULL,
            created_at_utc        timestamptz NOT NULL,
            chain_id              bigint      NULL,
            event_class           text        NOT NULL,
            type_id               text        NOT NULL,
            severity              text        NULL,
            message               text        NOT NULL,
            user_name             text        NULL,
            datacenter_name       text        NULL,
            compute_resource_ref  text        NULL,
            compute_resource_name text        NULL,
            host_ref              text        NULL,
            host_name             text        NULL,
            vm_ref                text        NULL,
            vm_name               text        NULL,
            datastore_ref         text        NULL,
            datastore_name        text        NULL,
            PRIMARY KEY (source_instance_id, event_key, created_at_utc)
        );

        CREATE INDEX ix_source_event_created ON source_event (created_at_utc);

        CREATE TABLE event_cursor (
            source_instance_id text        NOT NULL PRIMARY KEY,
            mark_key           bigint      NULL,
            mark_created_utc   timestamptz NULL,
            last_attempt_utc   timestamptz NULL,
            last_success_utc   timestamptz NULL,
            last_failure       text        NULL,
            last_gap_utc       timestamptz NULL
        );
        """,

        // --- 5: event read paths ---------------------------------------------
        //
        // One index per way the events are read, beside the time index the
        // sweep and the listing use.
        //
        // IEventHistory.Find asks one source for a few types in a window, so
        // (source, type, time) takes it straight to its rows instead of
        // filtering every source's events in the window.
        //
        // IEventStore.OfTypes asks every source for a set of types, matched
        // without regard to case, since a time. An expression index on the
        // folded type is what lets upper(type_id) = ANY(...) use an index at
        // all; the time column after it bounds each type's range.
        """
        CREATE INDEX ix_source_event_source_type_created
            ON source_event (source_instance_id, type_id, created_at_utc);

        CREATE INDEX ix_source_event_type_upper_created
            ON source_event (upper(type_id), created_at_utc);
        """,

        // --- 6: compliance (roadmap M3.2) -------------------------------------
        //
        // Findings in their own table, never in alert_instance: a finding does
        // not close itself, is expected by the hundred on the first day, and is
        // accepted rather than resolved. Keeping it apart is what keeps it out
        // of the inbox (product-architecture §2).
        //
        // The verdict is stored and the state is not. Whether an exception
        // still covers a finding depends on the clock, so it is worked out on
        // every read and an expired exception needs no job to undo it.
        //
        // The catalogue release is part of the key: a finding that cannot say
        // which edition of the guide it was judged against cannot be defended.
        //
        // Exceptions are keyed by control id, not release, so re-issuing the
        // same edition keeps them; entity_id NULL means every entity the
        // control applies to. expires_utc is NOT NULL on purpose — an
        // exception with no end is the forgotten kind.
        """
        CREATE TABLE compliance_finding (
            catalogue_release  text        NOT NULL,
            control_id         text        NOT NULL,
            entity_id          text        NOT NULL,
            entity_name        text        NOT NULL,
            verdict            text        NOT NULL,
            reason             text        NULL,
            observed           text        NULL,
            expected           text        NOT NULL,
            first_seen_utc     timestamptz NOT NULL,
            last_evaluated_utc timestamptz NOT NULL,
            accepted_by        text        NULL,
            accepted_at_utc    timestamptz NULL,
            accepted_reason    text        NULL,
            PRIMARY KEY (catalogue_release, control_id, entity_id)
        );

        CREATE TABLE compliance_exception (
            id             text        NOT NULL PRIMARY KEY,
            control_id     text        NOT NULL,
            entity_id      text        NULL,
            reason         text        NOT NULL,
            owner          text        NOT NULL,
            created_by     text        NOT NULL,
            created_at_utc timestamptz NOT NULL,
            expires_utc    timestamptz NOT NULL
        );
        """,

        // --- 7: compliance history and staleness (architecture review 2) -----
        //
        // compliance_transition is the audit trail M5's reports read: one row
        // each time a finding's verdict changes, including the first verdict a
        // finding ever had (from_verdict NULL) and its withdrawal when the host
        // or control leaves the evaluation (to_verdict NULL). Only changes are
        // written — a verdict that holds for a year is one row, not one per
        // cycle. evidence_utc is when the setting behind the new verdict was
        // read; at_utc is when the evaluation recorded it. Pruned by at_utc on
        // every evaluation; the retention is PostgresComplianceStore's.
        //
        // stale marks a finding whose host's source did not report in the
        // cycle that produced it: the last verdict that could be reached, not
        // a current one. DEFAULT false fills the rows already there, which
        // were all written by an evaluation that did not know the difference.
        //
        // removed_by / removed_at_utc make withdrawing an exception a fact on
        // the record rather than a DELETE. A withdrawn exception covers
        // nothing from that moment and stays readable afterwards.
        """
        CREATE TABLE compliance_transition (
            id                bigint      GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
            catalogue_release text        NOT NULL,
            control_id        text        NOT NULL,
            entity_id         text        NOT NULL,
            from_verdict      text        NULL,
            to_verdict        text        NULL,
            observed          text        NULL,
            evidence_utc      timestamptz NULL,
            at_utc            timestamptz NOT NULL,
            CHECK (from_verdict IS NOT NULL OR to_verdict IS NOT NULL)
        );

        -- The report reads one finding's history; retention sweeps by age.
        CREATE INDEX ix_compliance_transition_finding
            ON compliance_transition (catalogue_release, control_id, entity_id, at_utc);
        CREATE INDEX ix_compliance_transition_at ON compliance_transition (at_utc);

        ALTER TABLE compliance_finding ADD COLUMN stale boolean NOT NULL DEFAULT false;

        ALTER TABLE compliance_exception ADD COLUMN removed_by     text        NULL;
        ALTER TABLE compliance_exception ADD COLUMN removed_at_utc timestamptz NULL;
        """,

        // --- 8: scheduled email reports (roadmap M5.4) ------------------------
        //
        // One row, not a table keyed by name: an estate has several vCenters
        // but this product sends through one relay, the same way the previous
        // product's SQLite database held one set of SMTP settings. The boolean
        // primary key with its check constraint is the same device
        // schema_version uses above, for the same reason -- it makes a second
        // row a constraint violation rather than a query nobody thought to
        // write.
        //
        // password_protected holds ciphertext from the same ISecretProtector
        // that protects source_connection's passwords, under its own purpose
        // string so the two cannot be swapped. See ADR-0015.
        """
        CREATE TABLE smtp_settings (
            id                 boolean     PRIMARY KEY DEFAULT true CHECK (id),
            host               text        NOT NULL,
            port               integer     NOT NULL,
            tls_mode           text        NOT NULL,
            from_address       text        NOT NULL,
            username           text        NOT NULL,
            password_protected text        NOT NULL,
            allow_unencrypted  boolean     NOT NULL,
            password_set_utc   timestamptz NULL
        );

        -- Recipients as text[] rather than a child table: a subscription's
        -- list is short, read as a whole every time and never queried by one
        -- address, so a join would buy nothing an array does not already give.
        CREATE TABLE report_subscription (
            id             text        NOT NULL PRIMARY KEY,
            recipients     text[]      NOT NULL,
            frequency      text        NOT NULL,
            day_of_week    integer     NOT NULL,
            hour_local     integer     NOT NULL,
            time_zone_id   text        NOT NULL,
            kind           text        NOT NULL,
            is_enabled     boolean     NOT NULL,
            last_sent_utc  timestamptz NULL,
            last_error     text        NULL,
            created_by     text        NOT NULL,
            created_utc    timestamptz NOT NULL
        );
        """,

        // --- 9: re-folding late samples (architecture review 3) ---------------
        //
        // The earliest bucket of each tier whose source changed below its
        // watermark after that bucket had been folded. Samples are stamped with
        // vCenter's own sample time and datastores arrive as historical 300 s
        // data up to twenty minutes old, so a sample landing in a bucket that
        // was already summarised is normal operation, not an edge case.
        // Without this the sample stayed in raw for two days and never reached
        // the tiers kept for a month and a quarter. NULL means nothing is
        // waiting; the rows already there were written by a store that could
        // not tell. See PostgresObservationStore.Fold.
        """
        ALTER TABLE compaction ADD COLUMN dirty_from_utc bigint NULL;
        """,

        // --- 10: report subscription audit trail (architecture review 3) ------
        //
        // Any operator could edit or delete anyone's subscription with no
        // record of who did it. These two columns are that record: null for
        // a subscription nobody has edited since it was created, set by
        // every PUT after. Not a history table -- one edit overwriting the
        // last is the same trade ReportSubscription.LastError already makes,
        // and a subscription is edited by hand rarely enough that "who
        // touched it last" is the question that gets asked, not "every time
        // it changed".
        """
        ALTER TABLE report_subscription ADD COLUMN last_modified_by  text        NULL;
        ALTER TABLE report_subscription ADD COLUMN last_modified_utc timestamptz NULL;
        """,

        // --- 11: the subject in a finding's identity (K1) ---------------------
        //
        // A check may judge several subjects of one entity -- a DRS rule by
        // its uuid, an HBA -- so a finding is (release, control, entity,
        // subject). The subject is the cause the operator will fix, never the
        // symptom; subject_label is how it is shown and never identity, so a
        // renamed rule keeps its finding and its acceptance. Existing rows get
        // '' and every vendor-guide finding keeps it: their identity is
        // unchanged in value, only wider in shape.
        //
        // A finding whose subject leaves the evaluation is deleted with a
        // to_verdict NULL transition; accepted_by / accepted_reason carry its
        // acceptance there, so the row goes and the decision stays on record.
        //
        // An exception's subject NULL means every subject -- what every
        // exception written before this migration meant, and still means.
        """
        ALTER TABLE compliance_finding    ADD COLUMN subject       text NOT NULL DEFAULT '';
        ALTER TABLE compliance_finding    ADD COLUMN subject_label text NULL;
        ALTER TABLE compliance_finding    DROP CONSTRAINT compliance_finding_pkey;
        ALTER TABLE compliance_finding    ADD PRIMARY KEY (catalogue_release, control_id, entity_id, subject);
        ALTER TABLE compliance_transition ADD COLUMN subject         text NOT NULL DEFAULT '';
        ALTER TABLE compliance_transition ADD COLUMN subject_label   text NULL;
        ALTER TABLE compliance_transition ADD COLUMN accepted_by     text NULL;
        ALTER TABLE compliance_transition ADD COLUMN accepted_reason text NULL;
        ALTER TABLE compliance_exception  ADD COLUMN subject text NULL;
        """,

        // --- 12: reserved for K2 -------------------------------------------
        //
        // Permanently empty. It was held for the K2 package, which had not
        // landed when 13 did; the schema version is the length of this list,
        // so the slot stays as a no-op to keep the numbering contiguous.
        // Installations have run builds carrying it, so it must never be
        // replaced in place -- K2 (and anything after it) appends after 13.
        """
        SELECT 1;
        """,

        // --- 13: the source-level collection gap (T0.4, handover 3) ----------
        //
        // A stretch of time a whole source was not read -- the service was
        // stopped, or could not reach the vCenter -- and how far it has been
        // read again since. Filled oldest slice first with whatever budget the
        // live read leaves over; what is past the host's real-time retention
        // by the time the fill gets there is recorded in lost_before_utc and
        // the row closed 'unrecoverable', never deleted.
        //
        // Source level, NOT entity level: one host disconnected for twenty
        // minutes leaves no row here. That is covered only as far as the live
        // read's few samples back reach.
        //
        // No unique constraint: a restart during a fill leaves the first gap
        // open and records a second, and the fill takes the oldest gap_from
        // first. Times are bigint Unix seconds like the measurement tables they
        // describe; the audit times are timestamptz like the other state.
        // gap_from is exclusive (the newest sample already held) and gap_to
        // inclusive, vim25's own startTime/endTime convention.
        """
        CREATE TABLE collection_gap (
            id                 bigint      GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
            source_instance_id text        NOT NULL,
            gap_from_utc       bigint      NOT NULL,
            gap_to_utc         bigint      NOT NULL,
            filled_to_utc      bigint      NOT NULL,
            state              text        NOT NULL CHECK (state IN ('open', 'filled', 'unrecoverable')),
            lost_before_utc    bigint      NULL,
            opened_at_utc      timestamptz NOT NULL,
            closed_at_utc      timestamptz NULL,
            CHECK (gap_from_utc <= filled_to_utc AND filled_to_utc <= gap_to_utc)
        );

        CREATE INDEX ix_collection_gap_source ON collection_gap (source_instance_id, gap_from_utc);

        COMMENT ON TABLE collection_gap IS
            'Source-level collection outages (service stopped or vCenter unreachable) and how far each '
            'has been read again. Does NOT cover entity-level gaps such as one host disconnected for '
            '20 minutes; those are covered only within the live read''s few samples back.';
        """,

        // --- 14: three-valued evaluation and the durable alert history ------
        //
        // ADR-0026 and its design note §4. Two things, in one migration because
        // both change what an alert row means.
        //
        // alert_instance gains the three-valued state: the rule that owns the
        // alert (NULL = a direct producer, two-valued at N = 1), when its last
        // fresh verdict's evidence is from, whether it is stale and why, and
        // how many fresh absences it has had in a row. evidence_at_utc starts
        // as last_seen_utc, so every open alert starts fresh: no alert changes
        // state because of the deploy. rule_id is read from the fingerprint's
        // last segment, the check id, with the same map RuleCheckIds holds in
        // code (a test keeps them equal); an unmapped row stays NULL, which is
        // today's behaviour.
        //
        // alert_history replaces alert_transition. The old table hung off
        // alert_instance by ON DELETE CASCADE and was rewritten with the scope
        // every cycle, so a resolved alert's transitions went with it the
        // cycle it retired -- after the outage of 22 September nothing in the
        // database said a vCenter had been gone for four hours. The new table
        // has no foreign key, is appended to and never rewritten, and keys a
        // row by the episode (the instance's first_seen_utc) as well as the
        // fingerprint, because a fingerprint can live more than once. Each row
        // carries what a report needs to show an alert that no longer exists.
        // Kept 90 days, the hourly tier's retention (ADR-0017), swept by the
        // compaction pass -- only for episodes that have ended.
        //
        // The old rows are copied across, then the old table dropped rather
        // than kept beside it: two tables holding the same transitions would
        // need two writes per transition, forever, to stay equal. Every row is
        // copied, including one whose instance is already gone (a LEFT JOIN:
        // CASCADE made that impossible, but the migration does not assume it),
        // and a guard refuses the DROP unless alert_history holds at least as
        // many rows. Like every migration this runs in one transaction with its
        // version bump (Apply, PostgresDatabase.Write), so a failure anywhere
        // leaves version 13 and alert_transition as they were.
        """
        ALTER TABLE alert_instance ADD COLUMN rule_id            text        NULL;
        ALTER TABLE alert_instance ADD COLUMN evidence_at_utc    timestamptz NULL;
        ALTER TABLE alert_instance ADD COLUMN stale_since_utc    timestamptz NULL;
        ALTER TABLE alert_instance ADD COLUMN stale_reason       text        NULL;
        ALTER TABLE alert_instance ADD COLUMN stale_detail       text        NULL;
        ALTER TABLE alert_instance ADD COLUMN consecutive_absent integer     NOT NULL DEFAULT 0;

        UPDATE alert_instance SET evidence_at_utc = last_seen_utc;
        ALTER TABLE alert_instance ALTER COLUMN evidence_at_utc SET NOT NULL;

        UPDATE alert_instance a SET rule_id = m.rule_id
        FROM (VALUES
            ('fault-counter',                'fault-counters'),
            ('peer-outlier',                 'peer-outliers'),
            ('cpu-host-saturated',           'cpu-contention'),
            ('cpu-limit-reached',            'cpu-contention'),
            ('cpu-ready-outlier',            'cpu-contention'),
            ('cpu-costop-oversized',         'cpu-contention'),
            ('cpu-width-unreadable',         'cpu-contention'),
            ('memory-host-pressure',         'memory-pressure'),
            ('memory-guest-pressure',        'memory-pressure'),
            ('memory-limit-pressure',        'memory-pressure'),
            ('storage-layer',                'storage-layer-split'),
            ('shared-volume',                'shared-volume-latency'),
            ('storage-latency-blind-spot',   'storage-latency-blind-spot'),
            ('net-dropped-packets',          'dropped-packets'),
            ('storage-noisy-neighbour',      'storage-noisy-neighbour'),
            ('storage-path-redundancy',      'storage-path-redundancy'),
            ('remote-logging',               'remote-logging'),
            ('datastore-time-to-full',       'datastore-time-to-full'),
            ('datastore-overcommitted',      'datastore-time-to-full'),
            ('datastore-history-unreadable', 'datastore-time-to-full'),
            ('collection-coverage',          'collection-coverage')
        ) AS m(check_id, rule_id)
        WHERE regexp_replace(a.fingerprint, '^.*\|', '') = m.check_id;

        -- One check id per condition of the event table.
        UPDATE alert_instance SET rule_id = 'vcenter-events'
        WHERE regexp_replace(fingerprint, '^.*\|', '') LIKE 'vcenter-events:%';

        -- The four rules K2 retired keep owning their open alarms, which keeps
        -- them open until the continuity evaluation resolves them as moved.
        UPDATE alert_instance a SET rule_id = m.rule_id
        FROM (VALUES
            ('cluster-ha-scorecard'),
            ('drs-rule-violation'),
            ('multipath-single-point-of-failure'),
            ('cluster-n-plus-one')
        ) AS m(rule_id)
        WHERE a.rule_id IS NULL
          AND (regexp_replace(a.fingerprint, '^.*\|', '') = m.rule_id
               OR regexp_replace(a.fingerprint, '^.*\|', '') LIKE m.rule_id || '-%');

        CREATE INDEX ix_alert_rule ON alert_instance (scope, rule_id);

        CREATE TABLE alert_history (
            id                     bigint      GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
            fingerprint            text        NOT NULL,
            episode_first_seen_utc timestamptz NOT NULL,
            ordinal                integer     NOT NULL,
            from_state             text        NOT NULL,
            to_state               text        NOT NULL,
            reason                 text        NOT NULL,
            at_utc                 timestamptz NOT NULL,
            actor                  text        NULL,
            detail                 text        NULL,
            rule_id                text        NULL,
            evidence_at_utc        timestamptz NULL,
            -- What the alert was. NULL only on a transition copied from
            -- alert_transition whose instance was already gone: kept as
            -- evidence, with nothing invented about the alert it belonged to.
            scope                  text        NULL,
            severity               text        NULL,
            title                  text        NULL,
            category               text        NULL,
            source                 text        NULL,
            entity_id              text        NULL,
            is_derived             boolean     NULL,
            last_seen_utc          timestamptz NULL,
            UNIQUE (fingerprint, episode_first_seen_utc, ordinal)
        );

        -- The sweep and the report's "resolved in the window" both read by time.
        CREATE INDEX ix_alert_history_at ON alert_history (at_utc);

        INSERT INTO alert_history (
            fingerprint, episode_first_seen_utc, ordinal, from_state, to_state, reason, at_utc,
            actor, detail, rule_id, evidence_at_utc, scope, severity, title, category, source,
            entity_id, is_derived, last_seen_utc)
        SELECT t.fingerprint,
               -- An orphan's episode is its own first transition: alert_transition
               -- held one life per fingerprint, so this is unique and stable.
               COALESCE(a.first_seen_utc, min(t.at_utc) OVER (PARTITION BY t.fingerprint)),
               t.ordinal, t.from_state, t.to_state, t.reason, t.at_utc,
               t.actor, NULL, a.rule_id, NULL, a.scope, a.severity, a.title, a.category, a.source,
               a.entity_id, a.is_derived, a.last_seen_utc
        FROM alert_transition t
        LEFT JOIN alert_instance a ON a.fingerprint = t.fingerprint
        -- A duplicate (possible only where alert_transition lost its key) is
        -- not written twice; the guard below then refuses the whole migration
        -- rather than let the counts quietly differ.
        ON CONFLICT (fingerprint, episode_first_seen_utc, ordinal) DO NOTHING;

        -- The DROP below cannot be undone. Every transition must be across
        -- first, or nothing happens: the exception rolls back this migration's
        -- transaction, alert_transition included, and the version stays at 13.
        DO $guard$
        BEGIN
            IF (SELECT count(*) FROM alert_history) < (SELECT count(*) FROM alert_transition) THEN
                RAISE EXCEPTION
                    'migration 14: alert_history holds % rows but alert_transition % -- not dropping it',
                    (SELECT count(*) FROM alert_history), (SELECT count(*) FROM alert_transition);
            END IF;
        END
        $guard$;

        DROP TABLE alert_transition;

        COMMENT ON TABLE alert_history IS
            'Every alert transition, appended and never rewritten; outlives the alert_instance row. '
            'An episode is one life of a fingerprint (its first_seen_utc). Kept 90 days after the '
            'episode ends (ADR-0017 hourly tier), swept by the compaction pass. ADR-0026.';
        """,

        // Migration 15 (F2): cycles skipped because a previous read of the
        // same source was still running, counted next to the rest of that
        // source's collection numbers instead of only in a log line.
        """
        ALTER TABLE collector_health ADD COLUMN skipped_cycles integer NOT NULL DEFAULT 0;
        """,
    ];

    public static int Current => Migrations.Length;

    /// <summary>Brings the database up to <see cref="Current"/>.</summary>
    /// <exception cref="InvalidOperationException">If it is newer than this build.</exception>
    public static void Apply(PostgresDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);

        var version = database.Write(connection =>
        {
            // Created first and separately: every later migration is recorded
            // in it, so it cannot itself be one of them.
            using var create = connection.CreateCommand();
            create.CommandText = """
                CREATE TABLE IF NOT EXISTS schema_version (
                    id         boolean PRIMARY KEY DEFAULT true CHECK (id),
                    version    integer NOT NULL,
                    applied_at timestamptz NOT NULL DEFAULT now()
                );

                INSERT INTO schema_version (version) VALUES (0)
                ON CONFLICT (id) DO NOTHING;
                """;
            create.ExecuteNonQuery();

            using var read = connection.CreateCommand();
            read.CommandText = "SELECT version FROM schema_version;";
            return Convert.ToInt32(
                read.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
        });

        if (version > Current)
        {
            throw new InvalidOperationException(
                $"The database is at schema version {version} but this build understands " +
                $"{Current}. It was written by a newer version of the product. Upgrade rather " +
                "than downgrade: an older binary writing to a newer schema corrupts it slowly " +
                "and without saying so.");
        }

        for (var next = version; next < Current; next++)
        {
            var sql = Migrations[next];
            var target = next + 1;

            database.Write(connection =>
            {
                using var command = connection.CreateCommand();

                // One transaction, one version bump. PostgreSQL runs DDL
                // transactionally — unlike most engines — so a migration that
                // fails halfway leaves nothing behind, which is the property
                // that makes hand-written migrations safe here.
                command.CommandText = sql;
                command.ExecuteNonQuery();

                using var bump = connection.CreateCommand();
                bump.CommandText = "UPDATE schema_version SET version = @version, applied_at = now();";
                bump.Parameters.AddWithValue("version", target);
                bump.ExecuteNonQuery();
            });
        }
    }
}
