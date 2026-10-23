-- Scheduled monthly summary (DB-05): who gets it, and the PDFs that were made.
CREATE TABLE summary_schedules (
    organization_id     uuid PRIMARY KEY REFERENCES organizations(id),
    enabled             boolean NOT NULL DEFAULT false,
    -- Sent on this day of the month for the month before. 28 at most, so every month has the day.
    day_of_month        int NOT NULL DEFAULT 1 CHECK (day_of_month BETWEEN 1 AND 28),
    -- English or Urdu; empty means the society's own language.
    language            text CHECK (language IN ('en', 'ur')),
    recipient_user_ids  uuid[] NOT NULL DEFAULT '{}',
    updated_at          timestamptz NOT NULL DEFAULT now()
);
SELECT apply_tenant_rls('summary_schedules');

CREATE TABLE summary_reports (
    id                  uuid PRIMARY KEY,
    organization_id     uuid NOT NULL REFERENCES organizations(id),
    period_start        date NOT NULL,
    period_end          date NOT NULL,
    language            text NOT NULL,
    -- The costs section is included only when every recipient may see costs; downloading such a file needs the same permission.
    includes_costs      boolean NOT NULL,
    storage_key         text NOT NULL,
    size_bytes          bigint NOT NULL,
    recipient_user_ids  uuid[] NOT NULL DEFAULT '{}',
    created_at          timestamptz NOT NULL DEFAULT now(),
    -- Null when the schedule made it.
    created_by          uuid REFERENCES users(id),
    UNIQUE (organization_id, id),
    -- One summary per month: running the job twice, or asking again, never sends a second one.
    UNIQUE (organization_id, period_start)
);
SELECT apply_tenant_rls('summary_reports');
