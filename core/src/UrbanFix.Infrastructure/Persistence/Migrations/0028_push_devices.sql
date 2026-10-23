-- Push notification devices (FD-16): the phones and browsers a person has allowed to be sent notifications.
CREATE TABLE push_devices (
    id               uuid PRIMARY KEY,
    organization_id  uuid NOT NULL REFERENCES organizations(id),
    user_id          uuid NOT NULL REFERENCES users(id),
    kind             text NOT NULL CHECK (kind IN ('fcm', 'web')),
    -- The FCM registration token, or for a browser the push service address it gave us.
    token            text NOT NULL,
    -- Browsers only: the two keys needed to encrypt a message for that browser (JSON: p256dh, auth).
    keys             text,
    label            text,
    created_at       timestamptz NOT NULL DEFAULT now(),
    last_sent_at     timestamptz,
    failures         int NOT NULL DEFAULT 0,
    last_error       text,
    -- Set when the push service says the device is gone, or the person turns notifications off. The row stays, so history is kept.
    revoked_at       timestamptz,
    UNIQUE (organization_id, token)
);
CREATE INDEX push_devices_user ON push_devices (organization_id, user_id) WHERE revoked_at IS NULL;
SELECT apply_tenant_rls('push_devices');
