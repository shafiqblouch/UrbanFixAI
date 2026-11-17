-- Gate security: checkpoints (GT-00), pre-authorised visitor passes (GT-03), and the entry/exit log (GT-01).
-- Behind the per-society "gate" feature flag; the directory of vehicles and daily help (0020) feeds the lookups.

CREATE TABLE gates (
    id               uuid PRIMARY KEY,
    organization_id  uuid NOT NULL REFERENCES organizations(id),
    name             text NOT NULL,
    is_active        boolean NOT NULL DEFAULT true,
    created_at       timestamptz NOT NULL DEFAULT now(),
    UNIQUE (organization_id, id)
);
CREATE INDEX gates_org ON gates (organization_id);
SELECT apply_tenant_rls('gates');

-- So a gate event can point at a vehicle within the same tenant (the plate index already enforces uniqueness).
ALTER TABLE vehicles ADD CONSTRAINT vehicles_org_id UNIQUE (organization_id, id);

CREATE TABLE gate_passes (
    id               uuid PRIMARY KEY,
    organization_id  uuid NOT NULL REFERENCES organizations(id),
    code             text NOT NULL,
    visitor_name     text NOT NULL,
    phone            text CHECK (phone ~ '^\+[1-9][0-9]{7,14}$'),
    vehicle_plate    text,
    plate_key        text,
    house_id         uuid NOT NULL,
    kind             text NOT NULL DEFAULT 'visitor' CHECK (kind IN ('visitor', 'guest', 'delivery', 'help', 'contractor')),
    purpose          text,
    valid_from       timestamptz NOT NULL,
    valid_to         timestamptz NOT NULL,
    max_uses         int CHECK (max_uses IS NULL OR max_uses > 0),
    used_count       int NOT NULL DEFAULT 0,
    status           text NOT NULL DEFAULT 'active' CHECK (status IN ('active', 'cancelled')),
    created_by       uuid REFERENCES users(id),
    created_at       timestamptz NOT NULL DEFAULT now(),
    CHECK (valid_to > valid_from),
    UNIQUE (organization_id, id),
    FOREIGN KEY (organization_id, house_id) REFERENCES houses (organization_id, id)
);
CREATE UNIQUE INDEX gate_passes_code ON gate_passes (organization_id, code);
CREATE INDEX gate_passes_house ON gate_passes (organization_id, house_id);
CREATE INDEX gate_passes_status ON gate_passes (organization_id, status, valid_to);
SELECT apply_tenant_rls('gate_passes');

CREATE TABLE gate_events (
    id                uuid PRIMARY KEY,
    organization_id   uuid NOT NULL REFERENCES organizations(id),
    gate_id           uuid,
    direction         text NOT NULL CHECK (direction IN ('in', 'out')),
    subject_type      text NOT NULL CHECK (subject_type IN ('resident', 'vehicle', 'help', 'visitor', 'delivery')),
    house_id          uuid,
    vehicle_id        uuid,
    help_id           uuid,
    pass_id           uuid,
    resident_user_id  uuid,
    subject_name      text,
    phone             text,
    vehicle_plate     text,
    plate_key         text,
    photo_key         text,
    decision          text NOT NULL DEFAULT 'allowed' CHECK (decision IN ('allowed', 'denied')),
    note              text,
    recorded_by       uuid REFERENCES users(id),
    recorded_at       timestamptz NOT NULL DEFAULT now(),
    FOREIGN KEY (organization_id, gate_id) REFERENCES gates (organization_id, id),
    FOREIGN KEY (organization_id, pass_id) REFERENCES gate_passes (organization_id, id),
    FOREIGN KEY (organization_id, vehicle_id) REFERENCES vehicles (organization_id, id),
    FOREIGN KEY (organization_id, help_id) REFERENCES daily_help (organization_id, id),
    FOREIGN KEY (organization_id, house_id) REFERENCES houses (organization_id, id)
);
CREATE INDEX gate_events_recent ON gate_events (organization_id, recorded_at DESC);
CREATE INDEX gate_events_gate ON gate_events (organization_id, gate_id, recorded_at DESC);
CREATE INDEX gate_events_plate ON gate_events (organization_id, plate_key) WHERE plate_key IS NOT NULL;
SELECT apply_tenant_rls('gate_events');
