-- WhatsApp channel (WA-02 to WA-06, NT-02, NT-07).

-- Which organization a business number belongs to. The webhook arrives with no signed-in user, so the lookup is a narrow
-- SECURITY DEFINER function, like find_user_for_login.
CREATE TABLE whatsapp_channels (
    id               uuid PRIMARY KEY,
    organization_id  uuid NOT NULL REFERENCES organizations(id),
    phone_number_id  text NOT NULL UNIQUE,
    display_number   text NOT NULL,
    is_active        boolean NOT NULL DEFAULT true
);
SELECT apply_tenant_rls('whatsapp_channels');

CREATE FUNCTION find_whatsapp_channel(p_phone_number_id text) RETURNS uuid
LANGUAGE sql STABLE SECURITY DEFINER SET search_path = public AS
$$ SELECT organization_id FROM whatsapp_channels WHERE phone_number_id = p_phone_number_id AND is_active $$;
REVOKE ALL ON FUNCTION find_whatsapp_channel(text) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION find_whatsapp_channel(text) TO urbanfix_app;

-- Every message in or out, kept as received. The unique key is what makes a replayed webhook harmless.
CREATE TABLE whatsapp_messages (
    id               uuid PRIMARY KEY,
    organization_id  uuid NOT NULL REFERENCES organizations(id),
    direction        text NOT NULL CHECK (direction IN ('in', 'out')),
    wa_message_id    text,
    wa_id            text NOT NULL,
    type             text NOT NULL,
    body             text,
    media_id         text,
    raw              jsonb,
    status           text NOT NULL CHECK (status IN ('received', 'processed', 'ignored', 'failed', 'sent', 'delivered', 'read')),
    error            text,
    complaint_id     uuid,
    created_at       timestamptz NOT NULL DEFAULT now(),
    processed_at     timestamptz,
    updated_at       timestamptz NOT NULL DEFAULT now()
);
CREATE UNIQUE INDEX whatsapp_messages_dedupe ON whatsapp_messages (organization_id, direction, wa_message_id) WHERE wa_message_id IS NOT NULL;
CREATE INDEX whatsapp_messages_sender ON whatsapp_messages (organization_id, wa_id, created_at DESC);
SELECT apply_tenant_rls('whatsapp_messages');

-- Where each person's chat has got to, so it can pick up after a long pause (WA-04), and consent (WA-06).
CREATE TABLE whatsapp_conversations (
    id               uuid PRIMARY KEY,
    organization_id  uuid NOT NULL REFERENCES organizations(id),
    phone            text NOT NULL,
    state            text NOT NULL DEFAULT 'idle',
    state_data       jsonb NOT NULL DEFAULT '{}',
    language         text,
    last_inbound_at  timestamptz,
    last_outbound_at timestamptz,
    opted_in_at      timestamptz,
    opted_out_at     timestamptz,
    complaint_id     uuid,
    updated_at       timestamptz NOT NULL DEFAULT now(),
    UNIQUE (organization_id, phone)
);
SELECT apply_tenant_rls('whatsapp_conversations');

-- Delivery receipts from the provider (NT-02, NT-07).
ALTER TABLE notification_deliveries
    ADD COLUMN provider_message_id text,
    ADD COLUMN delivered_at timestamptz,
    ADD COLUMN read_at timestamptz;
CREATE INDEX notification_deliveries_provider ON notification_deliveries (provider_message_id) WHERE provider_message_id IS NOT NULL;

ALTER TABLE notification_deliveries DROP CONSTRAINT notification_deliveries_status_check;
ALTER TABLE notification_deliveries ADD CONSTRAINT notification_deliveries_status_check
    CHECK (status IN ('Pending', 'Sent', 'Delivered', 'Read', 'Failed'));

-- The values a notification was rendered from, kept so a provider template can be filled in later (NT-02).
ALTER TABLE notifications ADD COLUMN template_values jsonb NOT NULL DEFAULT '{}';
