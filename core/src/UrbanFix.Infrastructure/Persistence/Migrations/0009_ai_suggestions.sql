-- Every AI suggestion attempt is kept, with its confidence and model version (AI-15).
CREATE TABLE ai_suggestions (
    id               uuid PRIMARY KEY,
    organization_id  uuid NOT NULL REFERENCES organizations(id),
    complaint_id     uuid NOT NULL,
    task             text NOT NULL CHECK (task IN ('understanding')),
    status           text NOT NULL CHECK (status IN ('done', 'failed')),
    attempt          int NOT NULL DEFAULT 1,
    category_key     text,
    summary          text,
    language         text,
    location_text    text,
    needs_more_info  boolean,
    confidence       real,
    model            text,
    prompt_version   text,
    error            text,
    created_at       timestamptz NOT NULL DEFAULT now(),
    FOREIGN KEY (organization_id, complaint_id) REFERENCES complaints (organization_id, id)
);
CREATE INDEX ai_suggestions_complaint ON ai_suggestions (organization_id, complaint_id, created_at);
SELECT apply_tenant_rls('ai_suggestions');
