-- Priority engine (RT-04), SLA policies (RT-06) and per-category auto-routing (RT-02).

ALTER TABLE locations ADD COLUMN risk_level int NOT NULL DEFAULT 0 CHECK (risk_level BETWEEN 0 AND 2);

ALTER TABLE categories
    ADD COLUMN auto_route boolean NOT NULL DEFAULT false,
    ADD COLUMN auto_route_threshold real NOT NULL DEFAULT 0.9 CHECK (auto_route_threshold BETWEEN 0.5 AND 1);

-- A complaint may now carry the highest level, "Safety"; category defaults stay at or below "Urgent".
ALTER TABLE complaints DROP CONSTRAINT complaints_priority_check;
ALTER TABLE complaints ADD CONSTRAINT complaints_priority_check CHECK (priority IN ('Low', 'Normal', 'High', 'Urgent', 'Safety'));

ALTER TABLE complaints
    ADD COLUMN safety_flag boolean NOT NULL DEFAULT false,
    ADD COLUMN due_at timestamptz,
    ADD COLUMN respond_by timestamptz,
    ADD COLUMN auto_routed boolean NOT NULL DEFAULT false;

CREATE TABLE sla_policies (
    id               uuid PRIMARY KEY,
    organization_id  uuid NOT NULL REFERENCES organizations(id),
    category_id      uuid,
    priority         text NOT NULL CHECK (priority IN ('Low', 'Normal', 'High', 'Urgent', 'Safety')),
    respond_hours    int NOT NULL CHECK (respond_hours > 0),
    resolve_hours    int NOT NULL CHECK (resolve_hours >= respond_hours),
    business_hours_only boolean NOT NULL DEFAULT false,
    is_active        boolean NOT NULL DEFAULT true,
    FOREIGN KEY (organization_id, category_id) REFERENCES categories (organization_id, id)
);
-- One active policy per category and priority; the "any category" policy has a null category.
CREATE UNIQUE INDEX sla_policies_scope ON sla_policies (organization_id, COALESCE(category_id, '00000000-0000-0000-0000-000000000000'::uuid), priority) WHERE is_active;
SELECT apply_tenant_rls('sla_policies');
