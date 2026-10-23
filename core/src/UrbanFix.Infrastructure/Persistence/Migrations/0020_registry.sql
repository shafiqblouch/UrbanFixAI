-- Vehicles (OR-08), daily help (OR-09), duplicate review (OR-11).

CREATE TABLE vehicles (
    id               uuid PRIMARY KEY,
    organization_id  uuid NOT NULL REFERENCES organizations(id),
    plate            text NOT NULL,
    plate_key        text NOT NULL CHECK (plate_key <> ''),
    type             text NOT NULL CHECK (type IN ('car', 'motorbike', 'rickshaw', 'van', 'other')),
    house_id         uuid,
    sticker_code     text,
    status           text NOT NULL DEFAULT 'active' CHECK (status IN ('active', 'inactive')),
    note             text,
    created_at       timestamptz NOT NULL DEFAULT now(),
    FOREIGN KEY (organization_id, house_id) REFERENCES houses (organization_id, id)
);
-- "ABC-123" and "abc 123" are one vehicle.
CREATE UNIQUE INDEX vehicles_plate ON vehicles (organization_id, plate_key);
CREATE UNIQUE INDEX vehicles_sticker ON vehicles (organization_id, lower(sticker_code)) WHERE sticker_code IS NOT NULL;
CREATE INDEX vehicles_house ON vehicles (organization_id, house_id);
SELECT apply_tenant_rls('vehicles');

CREATE TABLE daily_help (
    id                uuid PRIMARY KEY,
    organization_id   uuid NOT NULL REFERENCES organizations(id),
    name              text NOT NULL,
    phone             text CHECK (phone ~ '^\+[1-9][0-9]{7,14}$'),
    id_number_masked  text,
    photo_key         text,
    role              text NOT NULL CHECK (role IN ('maid', 'driver', 'cook', 'gardener', 'guard', 'nanny', 'other')),
    status            text NOT NULL DEFAULT 'active' CHECK (status IN ('active', 'inactive')),
    allowed_days      text[] NOT NULL DEFAULT '{}',
    allowed_from      time,
    allowed_to        time,
    created_at        timestamptz NOT NULL DEFAULT now(),
    UNIQUE (organization_id, id)
);
CREATE INDEX daily_help_phone ON daily_help (organization_id, phone);
SELECT apply_tenant_rls('daily_help');

CREATE TABLE daily_help_houses (
    id               uuid PRIMARY KEY,
    organization_id  uuid NOT NULL REFERENCES organizations(id),
    help_id          uuid NOT NULL,
    house_id         uuid NOT NULL,
    UNIQUE (organization_id, help_id, house_id),
    FOREIGN KEY (organization_id, help_id) REFERENCES daily_help (organization_id, id),
    FOREIGN KEY (organization_id, house_id) REFERENCES houses (organization_id, id)
);
SELECT apply_tenant_rls('daily_help_houses');

CREATE TABLE directory_dismissals (
    id               uuid PRIMARY KEY,
    organization_id  uuid NOT NULL REFERENCES organizations(id),
    kind             text NOT NULL,
    key              text NOT NULL,
    dismissed_by     uuid REFERENCES users(id),
    dismissed_at     timestamptz NOT NULL DEFAULT now(),
    UNIQUE (organization_id, kind, key)
);
SELECT apply_tenant_rls('directory_dismissals');
