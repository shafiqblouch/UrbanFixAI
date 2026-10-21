-- Complaints, status history, media, jobs and the material price list.

CREATE TABLE number_sequences (
    organization_id  uuid NOT NULL REFERENCES organizations(id),
    scope            text NOT NULL,
    year             int NOT NULL,
    last_value       int NOT NULL,
    PRIMARY KEY (organization_id, scope, year)
);
SELECT apply_tenant_rls('number_sequences');

CREATE TABLE complaints (
    id                    uuid PRIMARY KEY,
    organization_id       uuid NOT NULL REFERENCES organizations(id),
    reference             text NOT NULL,
    status                text NOT NULL CHECK (status IN
        ('New', 'OnHold', 'UnderReview', 'Assigned', 'InProgress', 'Resolved', 'Closed', 'Rejected', 'Merged')),
    channel               text NOT NULL CHECK (channel IN ('app', 'web', 'whatsapp', 'phone', 'walk_in')),
    description           text NOT NULL,
    category_id           uuid,
    house_id              uuid,
    location_id           uuid,
    location_text         text,
    reported_by_user_id   uuid REFERENCES users(id),
    entered_by_user_id    uuid REFERENCES users(id),
    priority              text NOT NULL DEFAULT 'Normal' CHECK (priority IN ('Low', 'Normal', 'High', 'Urgent')),
    language              text,
    ai_status             text NOT NULL DEFAULT 'pending' CHECK (ai_status IN ('pending', 'done', 'failed', 'skipped')),
    ai_category_key       text,
    ai_summary            text,
    ai_confidence         real,
    ai_model              text,
    ai_prompt_version     text,
    hold_reason           text,
    status_reason         text,
    merged_into_id        uuid,
    created_at            timestamptz NOT NULL DEFAULT now(),
    updated_at            timestamptz NOT NULL DEFAULT now(),
    resolved_at           timestamptz,
    closed_at             timestamptz,
    UNIQUE (organization_id, id),
    UNIQUE (organization_id, reference),
    FOREIGN KEY (organization_id, category_id) REFERENCES categories (organization_id, id),
    FOREIGN KEY (organization_id, house_id) REFERENCES houses (organization_id, id),
    FOREIGN KEY (organization_id, location_id) REFERENCES locations (organization_id, id),
    FOREIGN KEY (organization_id, merged_into_id) REFERENCES complaints (organization_id, id)
);
CREATE INDEX complaints_status ON complaints (organization_id, status, created_at DESC);
CREATE INDEX complaints_reporter ON complaints (organization_id, reported_by_user_id);
SELECT apply_tenant_rls('complaints');

CREATE TABLE complaint_status_history (
    id               uuid PRIMARY KEY,
    organization_id  uuid NOT NULL REFERENCES organizations(id),
    complaint_id     uuid NOT NULL,
    from_status      text,
    to_status        text NOT NULL,
    actor_user_id    uuid,
    reason           text,
    occurred_at      timestamptz NOT NULL DEFAULT now(),
    FOREIGN KEY (organization_id, complaint_id) REFERENCES complaints (organization_id, id)
);
CREATE INDEX complaint_history_complaint ON complaint_status_history (organization_id, complaint_id, occurred_at);
SELECT apply_tenant_rls('complaint_status_history');

CREATE TABLE complaint_media (
    id               uuid PRIMARY KEY,
    organization_id  uuid NOT NULL REFERENCES organizations(id),
    complaint_id     uuid NOT NULL,
    storage_key      text NOT NULL,
    thumb_key        text,
    kind             text NOT NULL CHECK (kind IN ('image', 'audio')),
    mime_type        text NOT NULL,
    size_bytes       bigint NOT NULL CHECK (size_bytes > 0),
    sha256           text NOT NULL,
    width            int,
    height           int,
    quality_flags    text[] NOT NULL DEFAULT '{}',
    latitude         double precision,
    longitude        double precision,
    uploaded_by      uuid REFERENCES users(id),
    created_at       timestamptz NOT NULL DEFAULT now(),
    UNIQUE (organization_id, complaint_id, sha256),
    FOREIGN KEY (organization_id, complaint_id) REFERENCES complaints (organization_id, id)
);
SELECT apply_tenant_rls('complaint_media');

CREATE TABLE jobs (
    id                 uuid PRIMARY KEY,
    organization_id    uuid NOT NULL REFERENCES organizations(id),
    number             text NOT NULL,
    complaint_id       uuid NOT NULL,
    sequence_no        int NOT NULL,
    status             text NOT NULL DEFAULT 'Open' CHECK (status IN ('Open', 'InProgress', 'Completed', 'Cancelled')),
    instructions       text,
    assigned_team_id   uuid,
    created_by         uuid REFERENCES users(id),
    created_at         timestamptz NOT NULL DEFAULT now(),
    UNIQUE (organization_id, number),
    UNIQUE (organization_id, complaint_id, sequence_no),
    FOREIGN KEY (organization_id, complaint_id) REFERENCES complaints (organization_id, id),
    FOREIGN KEY (organization_id, assigned_team_id) REFERENCES teams (organization_id, id)
);
SELECT apply_tenant_rls('jobs');

CREATE TABLE price_items (
    id               uuid PRIMARY KEY,
    organization_id  uuid NOT NULL REFERENCES organizations(id),
    name             text NOT NULL,
    name_ur          text,
    unit             text NOT NULL,
    is_active        boolean NOT NULL DEFAULT true,
    UNIQUE (organization_id, id)
);
CREATE UNIQUE INDEX price_items_name ON price_items (organization_id, lower(name));
SELECT apply_tenant_rls('price_items');

-- Rates are append-only: estimates reference a rate row, so editing history would change past estimates.
CREATE TABLE price_rates (
    id               uuid PRIMARY KEY,
    organization_id  uuid NOT NULL REFERENCES organizations(id),
    item_id          uuid NOT NULL,
    rate             numeric(14, 2) NOT NULL CHECK (rate >= 0),
    effective_from   timestamptz NOT NULL,
    created_by       uuid REFERENCES users(id),
    created_at       timestamptz NOT NULL DEFAULT now(),
    FOREIGN KEY (organization_id, item_id) REFERENCES price_items (organization_id, id)
);
CREATE INDEX price_rates_item ON price_rates (organization_id, item_id, effective_from DESC);
CREATE TRIGGER price_rates_no_change BEFORE UPDATE OR DELETE ON price_rates
    FOR EACH ROW EXECUTE FUNCTION audit_log_immutable();
SELECT apply_tenant_rls('price_rates');
REVOKE UPDATE, DELETE ON price_rates FROM urbanfix_app;
