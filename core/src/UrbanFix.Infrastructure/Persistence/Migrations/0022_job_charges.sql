-- Review of repairs to be charged to a house (JC-11). Posting to the house's account waits for billing.
CREATE TABLE job_charges (
    id               uuid PRIMARY KEY,
    organization_id  uuid NOT NULL REFERENCES organizations(id),
    job_id           uuid NOT NULL,
    house_id         uuid NOT NULL,
    proposed_amount  numeric(14, 2) NOT NULL CHECK (proposed_amount >= 0),
    final_amount     numeric(14, 2) CHECK (final_amount >= 0),
    status           text NOT NULL DEFAULT 'Pending' CHECK (status IN ('Pending', 'Approved', 'Adjusted', 'Rejected', 'Cancelled')),
    decided_by       uuid REFERENCES users(id),
    decided_at       timestamptz,
    note             text,
    created_at       timestamptz NOT NULL DEFAULT now(),
    FOREIGN KEY (organization_id, job_id) REFERENCES jobs (organization_id, id),
    FOREIGN KEY (organization_id, house_id) REFERENCES houses (organization_id, id)
);
-- One live charge per job; a cancelled one (the flag was cleared) does not count.
CREATE UNIQUE INDEX job_charges_live ON job_charges (organization_id, job_id) WHERE status <> 'Cancelled';
CREATE INDEX job_charges_status ON job_charges (organization_id, status, created_at);
SELECT apply_tenant_rls('job_charges');
