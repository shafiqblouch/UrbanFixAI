-- Routing rules (RT-01) and capture of AI suggestion acceptance / override (AI-16).

CREATE TABLE routing_rules (
    id               uuid PRIMARY KEY,
    organization_id  uuid NOT NULL REFERENCES organizations(id),
    name             text NOT NULL,
    -- Null category or null location means "any".
    category_id      uuid,
    location_id      uuid,
    team_id          uuid NOT NULL,
    sort_order       int NOT NULL DEFAULT 0,
    is_active        boolean NOT NULL DEFAULT true,
    FOREIGN KEY (organization_id, category_id) REFERENCES categories (organization_id, id),
    FOREIGN KEY (organization_id, location_id) REFERENCES locations (organization_id, id),
    FOREIGN KEY (organization_id, team_id) REFERENCES teams (organization_id, id)
);
SELECT apply_tenant_rls('routing_rules');

-- One row per complaint and field: what the AI suggested, what the supervisor settled on.
CREATE TABLE ai_feedback (
    id               uuid PRIMARY KEY,
    organization_id  uuid NOT NULL REFERENCES organizations(id),
    complaint_id     uuid NOT NULL,
    field            text NOT NULL,
    suggested        text NOT NULL,
    final_value      text NOT NULL,
    accepted         boolean NOT NULL,
    language         text,
    model            text,
    prompt_version   text,
    confidence       real,
    actor_id         uuid REFERENCES users(id),
    created_at       timestamptz NOT NULL DEFAULT now(),
    UNIQUE (organization_id, complaint_id, field),
    FOREIGN KEY (organization_id, complaint_id) REFERENCES complaints (organization_id, id)
);
SELECT apply_tenant_rls('ai_feedback');
