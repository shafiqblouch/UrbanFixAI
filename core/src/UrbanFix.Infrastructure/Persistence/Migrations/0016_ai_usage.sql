-- What every AI call used, for per-organization caps and cost per complaint (AI-13). Append-only.
CREATE TABLE ai_usage (
    id               uuid PRIMARY KEY,
    organization_id  uuid NOT NULL REFERENCES organizations(id),
    complaint_id     uuid,
    task             text NOT NULL,
    model            text NOT NULL,
    input_tokens     int NOT NULL DEFAULT 0,
    output_tokens    int NOT NULL DEFAULT 0,
    -- Null when no price is configured for the model: tokens are still counted.
    cost_usd         numeric(14, 6),
    occurred_at      timestamptz NOT NULL DEFAULT now(),
    FOREIGN KEY (organization_id, complaint_id) REFERENCES complaints (organization_id, id)
);
CREATE INDEX ai_usage_month ON ai_usage (organization_id, occurred_at);
CREATE INDEX ai_usage_complaint ON ai_usage (organization_id, complaint_id) WHERE complaint_id IS NOT NULL;
SELECT apply_tenant_rls('ai_usage');

-- Rules-only mode (AI-14): the category was guessed from keywords, not by the AI.
ALTER TABLE complaints DROP CONSTRAINT complaints_ai_status_check;
ALTER TABLE complaints ADD CONSTRAINT complaints_ai_status_check
    CHECK (ai_status IN ('pending', 'done', 'failed', 'skipped', 'rules_only'));
