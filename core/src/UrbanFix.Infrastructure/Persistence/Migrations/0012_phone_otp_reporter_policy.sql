-- Resident phone-OTP login (ID-05), unknown-sender policy (CP-08), closure confirmation (CP-10), asset reference (CP-11).

ALTER TABLE users ADD COLUMN phone text CHECK (phone ~ '^\+[1-9][0-9]{7,14}$');
CREATE UNIQUE INDEX users_phone_key ON users (phone) WHERE phone IS NOT NULL;

-- Pre-authentication lookup, like find_user_for_login: no tenant context exists yet.
CREATE FUNCTION find_user_by_phone(p_phone text) RETURNS SETOF users
LANGUAGE sql STABLE SECURITY DEFINER SET search_path = public AS
$$ SELECT * FROM users WHERE phone = p_phone $$;
REVOKE ALL ON FUNCTION find_user_by_phone(text) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION find_user_by_phone(text) TO urbanfix_app;

-- One row per code sent. Not tenant-owned: it is read before anyone is signed in. Only a hash of the code is kept.
CREATE TABLE otp_challenges (
    id           uuid PRIMARY KEY,
    phone        text NOT NULL,
    code_hash    text NOT NULL,
    created_at   timestamptz NOT NULL DEFAULT now(),
    expires_at   timestamptz NOT NULL,
    attempts     int NOT NULL DEFAULT 0,
    consumed_at  timestamptz,
    -- False for a number nobody owns: a code is recorded (so timing and limits look the same) but never sent.
    sent         boolean NOT NULL DEFAULT true,
    ip           text
);
CREATE INDEX otp_challenges_phone ON otp_challenges (phone, created_at DESC);
GRANT SELECT, INSERT, UPDATE ON otp_challenges TO urbanfix_app;

ALTER TABLE complaints
    ADD COLUMN reporter_phone text,
    ADD COLUMN reporter_verified boolean NOT NULL DEFAULT true,
    ADD COLUMN asset_ref text,
    ADD COLUMN confirmation_reminded_at timestamptz,
    ADD COLUMN auto_confirmed boolean NOT NULL DEFAULT false;
CREATE INDEX complaints_asset_ref ON complaints (organization_id, lower(asset_ref)) WHERE asset_ref IS NOT NULL;
