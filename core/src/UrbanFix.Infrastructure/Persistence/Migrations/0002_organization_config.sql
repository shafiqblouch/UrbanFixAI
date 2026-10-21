-- Organization configuration: settings, locations, departments/teams, categories, houses, resident links.
-- Every table carries organization_id, has RLS enforced, and cross-table references are composite
-- (organization_id, id) so a row can never point at another tenant's row.

CREATE FUNCTION apply_tenant_rls(tbl regclass) RETURNS void LANGUAGE plpgsql AS $$
BEGIN
    EXECUTE format('ALTER TABLE %s ENABLE ROW LEVEL SECURITY', tbl);
    EXECUTE format('ALTER TABLE %s FORCE ROW LEVEL SECURITY', tbl);
    EXECUTE format('CREATE POLICY tenant_isolation ON %s USING (organization_id = app_org_id()) WITH CHECK (organization_id = app_org_id())', tbl);
    EXECUTE format('GRANT SELECT, INSERT, UPDATE, DELETE ON %s TO urbanfix_app', tbl);
END $$;

CREATE TABLE organization_settings (
    organization_id    uuid PRIMARY KEY REFERENCES organizations(id),
    display_name       text,
    timezone           text NOT NULL DEFAULT 'Asia/Karachi',
    locale             text NOT NULL DEFAULT 'en' CHECK (locale IN ('en', 'ur')),
    location_template  text NOT NULL DEFAULT 'society' CHECK (location_template IN ('society', 'building')),
    primary_color      text CHECK (primary_color ~ '^#[0-9A-Fa-f]{6}$'),
    logo_key           text,
    settings           jsonb NOT NULL DEFAULT '{}',
    updated_at         timestamptz NOT NULL DEFAULT now()
);
SELECT apply_tenant_rls('organization_settings');

CREATE TABLE locations (
    id               uuid PRIMARY KEY,
    organization_id  uuid NOT NULL REFERENCES organizations(id),
    parent_id        uuid,
    kind             text NOT NULL CHECK (kind IN ('sector', 'street', 'block', 'house', 'building', 'floor', 'unit')),
    name             text NOT NULL,
    name_ur          text,
    path             text NOT NULL,
    depth            int NOT NULL,
    latitude         double precision CHECK (latitude BETWEEN -90 AND 90),
    longitude        double precision CHECK (longitude BETWEEN -180 AND 180),
    sort_order       int NOT NULL DEFAULT 0,
    is_active        boolean NOT NULL DEFAULT true,
    created_at       timestamptz NOT NULL DEFAULT now(),
    UNIQUE (organization_id, id),
    FOREIGN KEY (organization_id, parent_id) REFERENCES locations (organization_id, id)
);
CREATE UNIQUE INDEX locations_sibling_name ON locations
    (organization_id, coalesce(parent_id, '00000000-0000-0000-0000-000000000000'::uuid), lower(name));
CREATE INDEX locations_path ON locations (organization_id, path text_pattern_ops);
CREATE INDEX locations_parent ON locations (organization_id, parent_id);
SELECT apply_tenant_rls('locations');

CREATE TABLE departments (
    id               uuid PRIMARY KEY,
    organization_id  uuid NOT NULL REFERENCES organizations(id),
    name             text NOT NULL,
    name_ur          text,
    is_active        boolean NOT NULL DEFAULT true,
    UNIQUE (organization_id, id)
);
CREATE UNIQUE INDEX departments_name ON departments (organization_id, lower(name));
SELECT apply_tenant_rls('departments');

CREATE TABLE teams (
    id               uuid PRIMARY KEY,
    organization_id  uuid NOT NULL REFERENCES organizations(id),
    department_id    uuid NOT NULL,
    name             text NOT NULL,
    is_active        boolean NOT NULL DEFAULT true,
    UNIQUE (organization_id, id),
    FOREIGN KEY (organization_id, department_id) REFERENCES departments (organization_id, id)
);
CREATE UNIQUE INDEX teams_name ON teams (organization_id, department_id, lower(name));
SELECT apply_tenant_rls('teams');

CREATE TABLE team_members (
    id               uuid PRIMARY KEY,
    organization_id  uuid NOT NULL REFERENCES organizations(id),
    team_id          uuid NOT NULL,
    user_id          uuid NOT NULL REFERENCES users(id),
    is_supervisor    boolean NOT NULL DEFAULT false,
    UNIQUE (organization_id, team_id, user_id),
    FOREIGN KEY (organization_id, team_id) REFERENCES teams (organization_id, id)
);
SELECT apply_tenant_rls('team_members');

CREATE TABLE categories (
    id                uuid PRIMARY KEY,
    organization_id   uuid NOT NULL REFERENCES organizations(id),
    key               text NOT NULL CHECK (key ~ '^[a-z0-9_\-]{1,64}$'),
    name_en           text NOT NULL,
    name_ur           text,
    department_id     uuid,
    default_priority  text NOT NULL DEFAULT 'Normal' CHECK (default_priority IN ('Low', 'Normal', 'High', 'Urgent')),
    evidence_rule     text NOT NULL DEFAULT 'photo' CHECK (evidence_rule IN ('photo', 'checklist', 'note', 'none')),
    checklist         jsonb NOT NULL DEFAULT '[]',
    is_active         boolean NOT NULL DEFAULT true,
    sort_order        int NOT NULL DEFAULT 0,
    UNIQUE (organization_id, id),
    UNIQUE (organization_id, key),
    FOREIGN KEY (organization_id, department_id) REFERENCES departments (organization_id, id)
);
SELECT apply_tenant_rls('categories');

CREATE TABLE houses (
    id               uuid PRIMARY KEY,
    organization_id  uuid NOT NULL REFERENCES organizations(id),
    location_id      uuid NOT NULL,
    plot_size_sqyd   numeric(10, 2) CHECK (plot_size_sqyd > 0),
    house_category   text,
    status           text NOT NULL DEFAULT 'occupied' CHECK (status IN ('occupied', 'vacant', 'under_construction')),
    UNIQUE (organization_id, id),
    UNIQUE (organization_id, location_id),
    FOREIGN KEY (organization_id, location_id) REFERENCES locations (organization_id, id)
);
SELECT apply_tenant_rls('houses');

CREATE TABLE resident_links (
    id               uuid PRIMARY KEY,
    organization_id  uuid NOT NULL REFERENCES organizations(id),
    house_id         uuid NOT NULL,
    user_id          uuid NOT NULL REFERENCES users(id),
    relation         text NOT NULL CHECK (relation IN ('owner', 'tenant', 'family')),
    from_date        date NOT NULL,
    to_date          date,
    created_at       timestamptz NOT NULL DEFAULT now(),
    CHECK (to_date IS NULL OR to_date >= from_date),
    FOREIGN KEY (organization_id, house_id) REFERENCES houses (organization_id, id)
);
-- One current owner and one current tenant per house; any number of family members.
CREATE UNIQUE INDEX resident_links_current ON resident_links (organization_id, house_id, relation)
    WHERE to_date IS NULL AND relation IN ('owner', 'tenant');
CREATE INDEX resident_links_user ON resident_links (organization_id, user_id);
SELECT apply_tenant_rls('resident_links');
