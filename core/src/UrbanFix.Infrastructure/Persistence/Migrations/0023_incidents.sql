-- Incidents group complaints about the same problem (DP-01); duplicate suggestions are what the system proposes (DP-03, DP-04).
CREATE TABLE incidents (
    id                    uuid PRIMARY KEY,
    organization_id       uuid NOT NULL REFERENCES organizations(id),
    title                 text NOT NULL,
    status                text NOT NULL DEFAULT 'open' CHECK (status IN ('open', 'closed')),
    primary_complaint_id  uuid,
    created_by            uuid REFERENCES users(id),
    created_at            timestamptz NOT NULL DEFAULT now(),
    UNIQUE (organization_id, id)
);
SELECT apply_tenant_rls('incidents');

-- A complaint belongs to at most one incident.
ALTER TABLE complaints ADD COLUMN incident_id uuid;
ALTER TABLE complaints ADD CONSTRAINT complaints_incident_fk FOREIGN KEY (organization_id, incident_id) REFERENCES incidents (organization_id, id);
CREATE INDEX complaints_incident ON complaints (organization_id, incident_id) WHERE incident_id IS NOT NULL;

CREATE TABLE duplicate_suggestions (
    id                uuid PRIMARY KEY,
    organization_id   uuid NOT NULL REFERENCES organizations(id),
    complaint_a_id    uuid NOT NULL,
    complaint_b_id    uuid NOT NULL,
    score             real NOT NULL,
    text_similarity   real,
    distance_metres   double precision,
    hours_apart       double precision NOT NULL,
    same_category     boolean NOT NULL,
    reason            text NOT NULL,
    status            text NOT NULL DEFAULT 'Pending' CHECK (status IN ('Pending', 'Accepted', 'Rejected')),
    decided_by        uuid REFERENCES users(id),
    decided_at        timestamptz,
    note              text,
    created_at        timestamptz NOT NULL DEFAULT now(),
    CHECK (complaint_a_id < complaint_b_id),
    -- A pair is suggested once, ever: a rejected pair is not suggested again.
    UNIQUE (organization_id, complaint_a_id, complaint_b_id),
    FOREIGN KEY (organization_id, complaint_a_id) REFERENCES complaints (organization_id, id),
    FOREIGN KEY (organization_id, complaint_b_id) REFERENCES complaints (organization_id, id)
);
CREATE INDEX duplicate_suggestions_status ON duplicate_suggestions (organization_id, status, score DESC);
SELECT apply_tenant_rls('duplicate_suggestions');
