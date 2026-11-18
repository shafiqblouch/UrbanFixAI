-- Gate security, RFID/UHF + boom barrier (GT-05): a reader at a lane reads a car's sticker and drives the barrier.
-- A registered resident vehicle (its sticker_code) opens automatically and is logged; an unknown tag is held for a
-- guard to allow or deny in the app. Manual entry/exit (0031) stays as the fallback until the hardware is installed.

-- Readers: one per lane, fixed to a direction, authenticating with their own key. --------------------------------
CREATE TABLE gate_readers (
    id               uuid PRIMARY KEY,
    organization_id  uuid NOT NULL REFERENCES organizations(id),
    gate_id          uuid NOT NULL,
    name             text NOT NULL,
    direction        text NOT NULL CHECK (direction IN ('in', 'out')),
    api_key_hash     text NOT NULL,
    is_active        boolean NOT NULL DEFAULT true,
    last_seen_at     timestamptz,
    created_at       timestamptz NOT NULL DEFAULT now(),
    UNIQUE (organization_id, id),
    FOREIGN KEY (organization_id, gate_id) REFERENCES gates (organization_id, id)
);
CREATE INDEX gate_readers_org ON gate_readers (organization_id);
-- The key is looked up across tenants (the reader proves which society it belongs to), so the hash is globally unique.
CREATE UNIQUE INDEX gate_readers_key ON gate_readers (api_key_hash);
SELECT apply_tenant_rls('gate_readers');

-- Cars held at the barrier, waiting for a guard's allow/deny. ----------------------------------------------------
CREATE TABLE gate_approvals (
    id               uuid PRIMARY KEY,
    organization_id  uuid NOT NULL REFERENCES organizations(id),
    gate_id          uuid,
    reader_id        uuid,
    direction        text NOT NULL CHECK (direction IN ('in', 'out')),
    tag              text,
    vehicle_plate    text,
    status           text NOT NULL DEFAULT 'pending' CHECK (status IN ('pending', 'approved', 'denied', 'expired')),
    decided_by       uuid REFERENCES users(id),
    decided_at       timestamptz,
    event_id         uuid,
    consumed_at      timestamptz,
    created_at       timestamptz NOT NULL DEFAULT now(),
    UNIQUE (organization_id, id),
    FOREIGN KEY (organization_id, gate_id) REFERENCES gates (organization_id, id),
    FOREIGN KEY (organization_id, reader_id) REFERENCES gate_readers (organization_id, id)
);
CREATE INDEX gate_approvals_pending ON gate_approvals (organization_id, status, created_at DESC);
SELECT apply_tenant_rls('gate_approvals');

-- Where an automatic crossing came from, and which reader logged it. ---------------------------------------------
ALTER TABLE gate_events ADD COLUMN source text NOT NULL DEFAULT 'manual' CHECK (source IN ('manual', 'rfid'));
ALTER TABLE gate_events ADD COLUMN reader_id uuid;
ALTER TABLE gate_events ADD CONSTRAINT gate_events_reader_fk FOREIGN KEY (organization_id, reader_id) REFERENCES gate_readers (organization_id, id);

-- A scanned tag is matched against a resident vehicle's sticker_code; the unique index vehicles_sticker
-- on (organization_id, lower(sticker_code)) from 0020_registry already makes that lookup fast.
