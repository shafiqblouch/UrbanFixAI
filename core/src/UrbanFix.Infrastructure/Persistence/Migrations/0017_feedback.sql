-- Resident feedback on finished work (JB-08): a rating, whether it is fixed, and a comment. One per complaint.
CREATE TABLE complaint_feedback (
    id               uuid PRIMARY KEY,
    organization_id  uuid NOT NULL REFERENCES organizations(id),
    complaint_id     uuid NOT NULL,
    user_id          uuid NOT NULL REFERENCES users(id),
    rating           int NOT NULL CHECK (rating BETWEEN 1 AND 5),
    confirmed_fixed  boolean NOT NULL,
    comment          text,
    created_at       timestamptz NOT NULL DEFAULT now(),
    updated_at       timestamptz NOT NULL DEFAULT now(),
    UNIQUE (organization_id, complaint_id),
    FOREIGN KEY (organization_id, complaint_id) REFERENCES complaints (organization_id, id)
);
SELECT apply_tenant_rls('complaint_feedback');
