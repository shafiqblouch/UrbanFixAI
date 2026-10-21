-- Foundation schema: tenancy, identity, audit, outbox. Tenant isolation is enforced by PostgreSQL row-level
-- security, not by application filters. The application connects as urbanfix_app, which is not the table owner.

CREATE FUNCTION app_org_id() RETURNS uuid LANGUAGE sql STABLE AS
$$ SELECT nullif(current_setting('app.org_id', true), '')::uuid $$;

CREATE FUNCTION app_user_id() RETURNS uuid LANGUAGE sql STABLE AS
$$ SELECT nullif(current_setting('app.user_id', true), '')::uuid $$;

CREATE TABLE organizations (
    id          uuid PRIMARY KEY,
    name        text NOT NULL,
    slug        text NOT NULL UNIQUE,
    created_at  timestamptz NOT NULL DEFAULT now()
);

-- True when the current tenant context is the platform operator organization (read through the owner so the
-- organizations policy does not recurse into itself).
CREATE FUNCTION is_platform_context() RETURNS boolean LANGUAGE sql STABLE SECURITY DEFINER SET search_path = public AS
$$ SELECT EXISTS (SELECT 1 FROM organizations WHERE id = app_org_id() AND slug = 'platform') $$;

CREATE TABLE users (
    id                     uuid PRIMARY KEY,
    email                  text NOT NULL UNIQUE,
    display_name           text NOT NULL,
    password_hash          text NOT NULL,
    totp_secret_protected  text,
    totp_enabled           boolean NOT NULL DEFAULT false,
    is_active              boolean NOT NULL DEFAULT true,
    created_at             timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT users_email_lower CHECK (email = lower(email))
);

CREATE TABLE memberships (
    id               uuid PRIMARY KEY,
    organization_id  uuid NOT NULL REFERENCES organizations(id),
    user_id          uuid NOT NULL REFERENCES users(id),
    role             text NOT NULL CHECK (role IN
        ('Resident','Guard','Technician','Office','Supervisor','Accounts','Committee','Manager','PlatformAdmin')),
    department_id    uuid,
    gate_id          uuid,
    is_active        boolean NOT NULL DEFAULT true,
    created_at       timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX memberships_org_user ON memberships (organization_id, user_id);
CREATE INDEX memberships_user ON memberships (user_id);

CREATE TABLE sessions (
    id               uuid PRIMARY KEY,
    user_id          uuid NOT NULL REFERENCES users(id),
    organization_id  uuid REFERENCES organizations(id),
    token_hash       text NOT NULL UNIQUE,
    totp_verified    boolean NOT NULL DEFAULT false,
    created_at       timestamptz NOT NULL DEFAULT now(),
    expires_at       timestamptz NOT NULL,
    revoked_at       timestamptz,
    ip               text,
    device           text
);
CREATE INDEX sessions_user ON sessions (user_id);

CREATE TABLE audit_log (
    id               uuid PRIMARY KEY,
    organization_id  uuid NOT NULL REFERENCES organizations(id),
    actor_user_id    uuid,
    action           text NOT NULL,
    entity_type      text NOT NULL,
    entity_id        text NOT NULL,
    before           jsonb,
    after            jsonb,
    occurred_at      timestamptz NOT NULL DEFAULT now(),
    ip               text,
    device           text
);
CREATE INDEX audit_log_entity ON audit_log (organization_id, entity_type, entity_id, occurred_at);
CREATE INDEX audit_log_time ON audit_log (organization_id, occurred_at DESC);

CREATE FUNCTION audit_log_immutable() RETURNS trigger LANGUAGE plpgsql AS
$$ BEGIN RAISE EXCEPTION 'audit_log is append-only'; END $$;
CREATE TRIGGER audit_log_no_change BEFORE UPDATE OR DELETE ON audit_log
    FOR EACH ROW EXECUTE FUNCTION audit_log_immutable();
CREATE TRIGGER audit_log_no_truncate BEFORE TRUNCATE ON audit_log
    FOR EACH STATEMENT EXECUTE FUNCTION audit_log_immutable();

-- Infrastructure table, not tenant data: only the dispatcher reads it; each message carries its organization.
CREATE TABLE outbox_messages (
    id               uuid PRIMARY KEY,
    organization_id  uuid NOT NULL,
    type             text NOT NULL,
    payload          jsonb NOT NULL,
    occurred_at      timestamptz NOT NULL,
    attempts         int NOT NULL DEFAULT 0,
    next_attempt_at  timestamptz NOT NULL DEFAULT now(),
    processed_at     timestamptz,
    dead_lettered_at timestamptz,
    error            text
);
CREATE INDEX outbox_pending ON outbox_messages (next_attempt_at)
    WHERE processed_at IS NULL AND dead_lettered_at IS NULL;

-- Row-level security -------------------------------------------------------------------------------------

ALTER TABLE memberships ENABLE ROW LEVEL SECURITY;
ALTER TABLE memberships FORCE ROW LEVEL SECURITY;
CREATE POLICY memberships_tenant ON memberships
    USING (organization_id = app_org_id()) WITH CHECK (organization_id = app_org_id());
-- A user can always see their own memberships, across organizations (needed to list and switch organizations).
CREATE POLICY memberships_own ON memberships FOR SELECT USING (user_id = app_user_id());

ALTER TABLE audit_log ENABLE ROW LEVEL SECURITY;
ALTER TABLE audit_log FORCE ROW LEVEL SECURITY;
CREATE POLICY audit_log_tenant ON audit_log
    USING (organization_id = app_org_id()) WITH CHECK (organization_id = app_org_id());

ALTER TABLE organizations ENABLE ROW LEVEL SECURITY;
CREATE POLICY organizations_read ON organizations FOR SELECT USING (
    id = app_org_id()
    OR is_platform_context()
    OR id IN (SELECT organization_id FROM memberships WHERE user_id = app_user_id()));
CREATE POLICY organizations_create ON organizations FOR INSERT WITH CHECK (is_platform_context());

ALTER TABLE users ENABLE ROW LEVEL SECURITY;
ALTER TABLE users FORCE ROW LEVEL SECURITY;
CREATE POLICY users_visible ON users FOR SELECT USING (
    id = app_user_id()
    OR id IN (SELECT user_id FROM memberships WHERE organization_id = app_org_id()));
CREATE POLICY users_create ON users FOR INSERT WITH CHECK (app_org_id() IS NOT NULL);
CREATE POLICY users_update ON users FOR UPDATE USING (
    id = app_user_id()
    OR id IN (SELECT user_id FROM memberships WHERE organization_id = app_org_id()));

ALTER TABLE sessions ENABLE ROW LEVEL SECURITY;
ALTER TABLE sessions FORCE ROW LEVEL SECURITY;
CREATE POLICY sessions_own ON sessions USING (user_id = app_user_id()) WITH CHECK (user_id = app_user_id());

-- Pre-authentication lookups (no tenant context exists yet). Narrow SECURITY DEFINER functions instead of
-- opening the tables.
CREATE FUNCTION find_user_for_login(p_email text) RETURNS SETOF users
LANGUAGE sql STABLE SECURITY DEFINER SET search_path = public AS
$$ SELECT * FROM users WHERE email = lower(p_email) $$;

CREATE FUNCTION find_session_by_token(p_hash text) RETURNS SETOF sessions
LANGUAGE sql STABLE SECURITY DEFINER SET search_path = public AS
$$ SELECT * FROM sessions WHERE token_hash = p_hash $$;

-- Grants --------------------------------------------------------------------------------------------------

REVOKE ALL ON FUNCTION find_user_for_login(text), find_session_by_token(text) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION find_user_for_login(text), find_session_by_token(text) TO urbanfix_app;

GRANT SELECT, INSERT ON organizations TO urbanfix_app;
GRANT SELECT, INSERT, UPDATE ON users TO urbanfix_app;
GRANT SELECT, INSERT, UPDATE, DELETE ON memberships TO urbanfix_app;
GRANT SELECT, INSERT, UPDATE ON sessions TO urbanfix_app;
GRANT SELECT, INSERT ON audit_log TO urbanfix_app;
GRANT SELECT, INSERT, UPDATE ON outbox_messages TO urbanfix_app;
