-- Landmarks (OR-03): places residents name instead of addresses ("near Madni Masjid"). Searchable, optionally tied
-- to a node of the location tree, and fed into the AI prompt so "near the mosque" can be understood.
CREATE TABLE landmarks (
    id               uuid PRIMARY KEY,
    organization_id  uuid NOT NULL REFERENCES organizations(id),
    name             text NOT NULL,
    name_ur          text,
    aliases          text[] NOT NULL DEFAULT '{}',
    location_id      uuid,
    latitude         double precision CHECK (latitude BETWEEN -90 AND 90),
    longitude        double precision CHECK (longitude BETWEEN -180 AND 180),
    is_active        boolean NOT NULL DEFAULT true,
    UNIQUE (organization_id, id),
    FOREIGN KEY (organization_id, location_id) REFERENCES locations (organization_id, id)
);
CREATE UNIQUE INDEX landmarks_name ON landmarks (organization_id, lower(name));
SELECT apply_tenant_rls('landmarks');
