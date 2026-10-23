-- Notifications (NT-01, NT-05, NT-06): per-user inbox, per-channel delivery log, user preferences, job escalation marker.

ALTER TABLE users
    ADD COLUMN preferred_locale text CHECK (preferred_locale IN ('en', 'ur')),
    ADD COLUMN notify_channels  text[] NOT NULL DEFAULT '{}';

ALTER TABLE jobs ADD COLUMN escalated_at timestamptz;

CREATE TABLE notifications (
    id               uuid PRIMARY KEY,
    organization_id  uuid NOT NULL REFERENCES organizations(id),
    user_id          uuid NOT NULL REFERENCES users(id),
    template         text NOT NULL,
    -- The message is rendered once, in the recipient's language, when the event is handled.
    locale           text NOT NULL,
    title            text NOT NULL,
    body             text NOT NULL,
    link             text,
    -- event id + template + recipient: handling the same event twice never creates a second message.
    dedupe_key       text NOT NULL,
    created_at       timestamptz NOT NULL DEFAULT now(),
    read_at          timestamptz,
    UNIQUE (organization_id, id),
    UNIQUE (organization_id, dedupe_key)
);
CREATE INDEX notifications_user ON notifications (organization_id, user_id, created_at DESC);
SELECT apply_tenant_rls('notifications');

CREATE TABLE notification_deliveries (
    id               uuid PRIMARY KEY,
    organization_id  uuid NOT NULL REFERENCES organizations(id),
    notification_id  uuid NOT NULL,
    channel          text NOT NULL,
    status           text NOT NULL DEFAULT 'Pending' CHECK (status IN ('Pending', 'Sent', 'Failed')),
    attempts         int NOT NULL DEFAULT 0,
    last_error       text,
    created_at       timestamptz NOT NULL DEFAULT now(),
    last_attempt_at  timestamptz,
    sent_at          timestamptz,
    UNIQUE (organization_id, notification_id, channel),
    FOREIGN KEY (organization_id, notification_id) REFERENCES notifications (organization_id, id)
);
CREATE INDEX notification_deliveries_status ON notification_deliveries (organization_id, status, created_at DESC);
SELECT apply_tenant_rls('notification_deliveries');
