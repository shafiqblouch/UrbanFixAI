-- Store stock ledger (JC-06), low-stock alerts (JC-08), physical counts (JC-09), material requests (TA-09).

-- Append-only: movements are never edited or deleted; a mistake is corrected by another movement.
CREATE TABLE stock_movements (
    id               uuid PRIMARY KEY,
    organization_id  uuid NOT NULL REFERENCES organizations(id),
    item_id          uuid NOT NULL,
    kind             text NOT NULL CHECK (kind IN ('receive', 'adjust', 'issue', 'return')),
    quantity         numeric(14, 3) NOT NULL CHECK (quantity <> 0),
    job_id           uuid,
    request_id       uuid,
    count_id         uuid,
    reason           text NOT NULL,
    user_id          uuid NOT NULL REFERENCES users(id),
    created_at       timestamptz NOT NULL DEFAULT now(),
    CHECK ((kind IN ('receive', 'return') AND quantity > 0) OR (kind = 'issue' AND quantity < 0) OR kind = 'adjust'),
    CHECK (kind <> 'adjust' OR length(btrim(reason)) > 0),
    FOREIGN KEY (organization_id, item_id) REFERENCES price_items (organization_id, id),
    FOREIGN KEY (organization_id, job_id) REFERENCES jobs (organization_id, id)
);
CREATE INDEX stock_movements_item ON stock_movements (organization_id, item_id, created_at);
CREATE INDEX stock_movements_job ON stock_movements (organization_id, job_id) WHERE job_id IS NOT NULL;
SELECT apply_tenant_rls('stock_movements');
REVOKE UPDATE, DELETE ON stock_movements FROM urbanfix_app;

CREATE TABLE stock_settings (
    id               uuid PRIMARY KEY,
    organization_id  uuid NOT NULL REFERENCES organizations(id),
    item_id          uuid NOT NULL,
    low_threshold    numeric(14, 3) NOT NULL CHECK (low_threshold >= 0),
    low_alerted_at   timestamptz,
    UNIQUE (organization_id, item_id),
    FOREIGN KEY (organization_id, item_id) REFERENCES price_items (organization_id, id)
);
SELECT apply_tenant_rls('stock_settings');

CREATE TABLE stock_counts (
    id               uuid PRIMARY KEY,
    organization_id  uuid NOT NULL REFERENCES organizations(id),
    counted_by       uuid NOT NULL REFERENCES users(id),
    note             text,
    counted_at       timestamptz NOT NULL DEFAULT now(),
    UNIQUE (organization_id, id)
);
SELECT apply_tenant_rls('stock_counts');

CREATE TABLE stock_count_lines (
    id               uuid PRIMARY KEY,
    organization_id  uuid NOT NULL REFERENCES organizations(id),
    count_id         uuid NOT NULL,
    item_id          uuid NOT NULL,
    expected         numeric(14, 3) NOT NULL,
    counted          numeric(14, 3) NOT NULL CHECK (counted >= 0),
    reason           text,
    FOREIGN KEY (organization_id, count_id) REFERENCES stock_counts (organization_id, id),
    FOREIGN KEY (organization_id, item_id) REFERENCES price_items (organization_id, id)
);
SELECT apply_tenant_rls('stock_count_lines');

CREATE TABLE stock_requests (
    id               uuid PRIMARY KEY,
    organization_id  uuid NOT NULL REFERENCES organizations(id),
    job_id           uuid NOT NULL,
    requested_by     uuid NOT NULL REFERENCES users(id),
    status           text NOT NULL DEFAULT 'Open' CHECK (status IN ('Open', 'Fulfilled', 'Rejected', 'Cancelled')),
    note             text,
    created_at       timestamptz NOT NULL DEFAULT now(),
    decided_by       uuid REFERENCES users(id),
    decided_at       timestamptz,
    decision_note    text,
    UNIQUE (organization_id, id),
    FOREIGN KEY (organization_id, job_id) REFERENCES jobs (organization_id, id)
);
CREATE INDEX stock_requests_open ON stock_requests (organization_id, status, created_at);
SELECT apply_tenant_rls('stock_requests');

CREATE TABLE stock_request_lines (
    id               uuid PRIMARY KEY,
    organization_id  uuid NOT NULL REFERENCES organizations(id),
    request_id       uuid NOT NULL,
    item_id          uuid NOT NULL,
    quantity         numeric(14, 3) NOT NULL CHECK (quantity > 0),
    FOREIGN KEY (organization_id, request_id) REFERENCES stock_requests (organization_id, id),
    FOREIGN KEY (organization_id, item_id) REFERENCES price_items (organization_id, id)
);
SELECT apply_tenant_rls('stock_request_lines');
