-- Gate visitor approvals (GT-04): a pass can be pending a resident's answer, or denied by them.
-- Residents also pre-authorise their own visitors (GT-03); those are ordinary 'active' passes they created.

ALTER TABLE gate_passes DROP CONSTRAINT gate_passes_status_check;
ALTER TABLE gate_passes ADD CONSTRAINT gate_passes_status_check
    CHECK (status IN ('active', 'cancelled', 'pending', 'denied'));

ALTER TABLE gate_passes ADD COLUMN decided_by uuid REFERENCES users(id);
ALTER TABLE gate_passes ADD COLUMN decided_at timestamptz;
