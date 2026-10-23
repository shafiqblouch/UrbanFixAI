-- Approval workflow (ID-10), estimate approval gate (JC-03), overrun flag (JC-05), chargeable jobs (JB-10).

CREATE TABLE approvals (
    id                uuid PRIMARY KEY,
    organization_id   uuid NOT NULL REFERENCES organizations(id),
    kind              text NOT NULL,
    subject_id        uuid NOT NULL,
    job_id            uuid,
    amount            numeric(14, 2) NOT NULL,
    requested_by      uuid NOT NULL REFERENCES users(id),
    requested_at      timestamptz NOT NULL DEFAULT now(),
    expires_at        timestamptz NOT NULL,
    approvals_needed  int NOT NULL DEFAULT 1 CHECK (approvals_needed BETWEEN 1 AND 2),
    status            text NOT NULL DEFAULT 'Pending' CHECK (status IN ('Pending', 'Approved', 'Rejected', 'Expired', 'Cancelled')),
    decided_at        timestamptz,
    note              text,
    UNIQUE (organization_id, id)
);
CREATE INDEX approvals_pending ON approvals (organization_id, status, requested_at);
SELECT apply_tenant_rls('approvals');

-- Each person's answer is a row of its own and is never changed.
CREATE TABLE approval_decisions (
    id               uuid PRIMARY KEY,
    organization_id  uuid NOT NULL REFERENCES organizations(id),
    approval_id      uuid NOT NULL,
    user_id          uuid NOT NULL REFERENCES users(id),
    approve          boolean NOT NULL,
    note             text,
    decided_at       timestamptz NOT NULL DEFAULT now(),
    UNIQUE (organization_id, approval_id, user_id),
    FOREIGN KEY (organization_id, approval_id) REFERENCES approvals (organization_id, id)
);
SELECT apply_tenant_rls('approval_decisions');

ALTER TABLE estimates
    ADD COLUMN approval_status text NOT NULL DEFAULT 'NotRequired' CHECK (approval_status IN ('NotRequired', 'Pending', 'Approved', 'Rejected', 'Expired')),
    ADD COLUMN approval_id uuid;

ALTER TABLE jobs
    ADD COLUMN overrun_flagged_at timestamptz,
    ADD COLUMN chargeable boolean NOT NULL DEFAULT false,
    ADD COLUMN charge_house_id uuid,
    ADD CONSTRAINT jobs_chargeable_needs_house CHECK (NOT chargeable OR charge_house_id IS NOT NULL);
