-- Escalation scheduler (RT-07), levels and contacts (RT-08).

CREATE TABLE escalations (
    id               uuid PRIMARY KEY,
    organization_id  uuid NOT NULL REFERENCES organizations(id),
    complaint_id     uuid NOT NULL,
    job_id           uuid,
    rule             text NOT NULL,
    level            int NOT NULL CHECK (level BETWEEN 1 AND 5),
    since            timestamptz NOT NULL,
    raised_at        timestamptz NOT NULL DEFAULT now(),
    resolved_at      timestamptz,
    resolution       text,
    -- Each rule fires once per level for a complaint, ever; a recurrence reopens nothing, it is a new complaint state worth a human look.
    UNIQUE (organization_id, complaint_id, rule, level),
    FOREIGN KEY (organization_id, complaint_id) REFERENCES complaints (organization_id, id)
);
CREATE INDEX escalations_open ON escalations (organization_id) WHERE resolved_at IS NULL;
SELECT apply_tenant_rls('escalations');

CREATE TABLE escalation_levels (
    id               uuid PRIMARY KEY,
    organization_id  uuid NOT NULL REFERENCES organizations(id),
    level            int NOT NULL CHECK (level BETWEEN 1 AND 5),
    after_hours      int NOT NULL CHECK (after_hours >= 0),
    role             text NOT NULL,
    UNIQUE (organization_id, level)
);
SELECT apply_tenant_rls('escalation_levels');
