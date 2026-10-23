-- Platform administration: feature flags per organization (FD-13, PA-02) and support access (PA-05).

CREATE TABLE organization_features (
    organization_id  uuid NOT NULL REFERENCES organizations(id),
    feature          text NOT NULL,
    enabled          boolean NOT NULL,
    updated_by       uuid REFERENCES users(id),
    updated_at       timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (organization_id, feature)
);
SELECT apply_tenant_rls('organization_features');

-- A person from the platform team looking at one society's data, read-only, for a limited time, with a reason (PA-05).
-- The application role is given no access: only the platform service on the owner connection reads or writes this table.
CREATE TABLE support_sessions (
    id               uuid PRIMARY KEY,
    organization_id  uuid NOT NULL REFERENCES organizations(id),
    admin_user_id    uuid NOT NULL REFERENCES users(id),
    reason           text NOT NULL,
    started_at       timestamptz NOT NULL DEFAULT now(),
    expires_at       timestamptz NOT NULL,
    ended_at         timestamptz,
    ended_by         uuid REFERENCES users(id)
);
CREATE INDEX support_sessions_org ON support_sessions (organization_id, started_at DESC);

-- A support session signs in as the platform person but acts inside the society it was opened for.
ALTER TABLE sessions ADD COLUMN support_session_id uuid REFERENCES support_sessions(id);
