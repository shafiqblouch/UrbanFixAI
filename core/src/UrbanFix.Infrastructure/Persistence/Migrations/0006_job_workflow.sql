-- Job workflow (JB-02 to JB-06) and estimates / actual costs (JC-02, JC-04).

ALTER TABLE categories ADD COLUMN requires_costs boolean NOT NULL DEFAULT false;

ALTER TABLE jobs DROP CONSTRAINT jobs_status_check;
ALTER TABLE jobs ADD CONSTRAINT jobs_status_check CHECK (status IN
    ('Open', 'Accepted', 'InProgress', 'PendingVerification', 'Completed', 'Returned', 'Cancelled'));
ALTER TABLE jobs
    ADD COLUMN assigned_user_id uuid REFERENCES users(id),
    ADD COLUMN accepted_at      timestamptz,
    ADD COLUMN started_at       timestamptz,
    ADD COLUMN submitted_at     timestamptz,
    ADD COLUMN completed_at     timestamptz,
    ADD COLUMN completion_note  text,
    -- Snapshot of the category's rules when the job is created, so later configuration edits do not move the goalposts.
    ADD COLUMN evidence_rule    text NOT NULL DEFAULT 'photo' CHECK (evidence_rule IN ('photo', 'checklist', 'note', 'none')),
    ADD COLUMN requires_costs   boolean NOT NULL DEFAULT false,
    ADD COLUMN checklist        jsonb NOT NULL DEFAULT '[]',
    ADD UNIQUE (organization_id, id);
CREATE INDEX jobs_assignee ON jobs (organization_id, assigned_user_id, status);

ALTER TABLE complaint_media
    ADD COLUMN job_id uuid,
    ADD COLUMN stage  text CHECK (stage IN ('before', 'after', 'progress')),
    ADD FOREIGN KEY (organization_id, job_id) REFERENCES jobs (organization_id, id),
    ADD CHECK ((job_id IS NULL) = (stage IS NULL));
CREATE INDEX complaint_media_job ON complaint_media (organization_id, job_id) WHERE job_id IS NOT NULL;

-- The job timeline: every update with its author. Append-only.
CREATE TABLE job_updates (
    id               uuid PRIMARY KEY,
    organization_id  uuid NOT NULL REFERENCES organizations(id),
    job_id           uuid NOT NULL,
    author_id        uuid REFERENCES users(id),
    kind             text NOT NULL,
    from_status      text,
    to_status        text,
    note             text,
    latitude         double precision,
    longitude        double precision,
    created_at       timestamptz NOT NULL DEFAULT now(),
    FOREIGN KEY (organization_id, job_id) REFERENCES jobs (organization_id, id)
);
CREATE INDEX job_updates_job ON job_updates (organization_id, job_id, created_at);
CREATE TRIGGER job_updates_no_change BEFORE UPDATE OR DELETE ON job_updates
    FOR EACH ROW EXECUTE FUNCTION audit_log_immutable();
SELECT apply_tenant_rls('job_updates');
REVOKE UPDATE, DELETE ON job_updates FROM urbanfix_app;

-- Estimates are versioned: a revision is a new row and the earlier one is marked Superseded.
CREATE TABLE estimates (
    id               uuid PRIMARY KEY,
    organization_id  uuid NOT NULL REFERENCES organizations(id),
    job_id           uuid NOT NULL,
    version          int NOT NULL,
    status           text NOT NULL DEFAULT 'Current' CHECK (status IN ('Current', 'Superseded')),
    note             text,
    total            numeric(14, 2) NOT NULL CHECK (total >= 0),
    created_by       uuid REFERENCES users(id),
    created_at       timestamptz NOT NULL DEFAULT now(),
    UNIQUE (organization_id, id),
    UNIQUE (organization_id, job_id, version),
    FOREIGN KEY (organization_id, job_id) REFERENCES jobs (organization_id, id)
);
CREATE UNIQUE INDEX estimates_one_current ON estimates (organization_id, job_id) WHERE status = 'Current';
SELECT apply_tenant_rls('estimates');

CREATE TABLE estimate_lines (
    id               uuid PRIMARY KEY,
    organization_id  uuid NOT NULL REFERENCES organizations(id),
    estimate_id      uuid NOT NULL,
    kind             text NOT NULL CHECK (kind IN ('material', 'labour', 'transport', 'other')),
    description      text NOT NULL,
    price_item_id    uuid,
    rate_id          uuid REFERENCES price_rates(id),
    quantity         numeric(14, 3) NOT NULL CHECK (quantity > 0),
    unit             text NOT NULL,
    unit_rate        numeric(14, 2) NOT NULL CHECK (unit_rate >= 0),
    amount           numeric(14, 2) NOT NULL CHECK (amount >= 0),
    FOREIGN KEY (organization_id, estimate_id) REFERENCES estimates (organization_id, id),
    FOREIGN KEY (organization_id, price_item_id) REFERENCES price_items (organization_id, id)
);
SELECT apply_tenant_rls('estimate_lines');

-- Actual costs recorded at completion (same line shape as an estimate).
CREATE TABLE job_costs (
    id               uuid PRIMARY KEY,
    organization_id  uuid NOT NULL REFERENCES organizations(id),
    job_id           uuid NOT NULL,
    kind             text NOT NULL CHECK (kind IN ('material', 'labour', 'transport', 'other')),
    description      text NOT NULL,
    price_item_id    uuid,
    rate_id          uuid REFERENCES price_rates(id),
    quantity         numeric(14, 3) NOT NULL CHECK (quantity > 0),
    unit             text NOT NULL,
    unit_rate        numeric(14, 2) NOT NULL CHECK (unit_rate >= 0),
    amount           numeric(14, 2) NOT NULL CHECK (amount >= 0),
    recorded_by      uuid REFERENCES users(id),
    created_at       timestamptz NOT NULL DEFAULT now(),
    FOREIGN KEY (organization_id, job_id) REFERENCES jobs (organization_id, id),
    FOREIGN KEY (organization_id, price_item_id) REFERENCES price_items (organization_id, id)
);
CREATE INDEX job_costs_job ON job_costs (organization_id, job_id);
SELECT apply_tenant_rls('job_costs');
