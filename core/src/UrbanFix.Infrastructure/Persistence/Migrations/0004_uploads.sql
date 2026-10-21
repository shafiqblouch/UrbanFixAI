-- Resumable uploads: a client sends a file in chunks and can ask how many bytes the server already has.
CREATE TABLE upload_sessions (
    id               uuid PRIMARY KEY,
    organization_id  uuid NOT NULL REFERENCES organizations(id),
    user_id          uuid NOT NULL REFERENCES users(id),
    mime_type        text NOT NULL,
    total_bytes      bigint NOT NULL CHECK (total_bytes > 0),
    received_bytes   bigint NOT NULL DEFAULT 0 CHECK (received_bytes >= 0),
    chunk_keys       text[] NOT NULL DEFAULT '{}',
    created_at       timestamptz NOT NULL DEFAULT now(),
    completed_at     timestamptz,
    CHECK (received_bytes <= total_bytes)
);
SELECT apply_tenant_rls('upload_sessions');
