-- Notices and broadcast announcements (NT-08). A notice reaches its audience as ordinary notifications, so channels,
-- retries, delivery receipts and read state all work as they do for every other message.
CREATE TABLE notices (
    id               uuid PRIMARY KEY,
    organization_id  uuid NOT NULL REFERENCES organizations(id),
    title            text NOT NULL,
    body             text NOT NULL,
    title_ur         text,
    body_ur          text,
    -- Who it is for: everyone, current owners, or current tenants; optionally only the houses under one location (a street).
    audience_kind    text NOT NULL CHECK (audience_kind IN ('all', 'owners', 'tenants')),
    location_id      uuid,
    status           text NOT NULL DEFAULT 'Scheduled' CHECK (status IN ('Scheduled', 'Sent', 'Cancelled')),
    scheduled_at     timestamptz,
    sent_at          timestamptz,
    created_by       uuid NOT NULL REFERENCES users(id),
    created_at       timestamptz NOT NULL DEFAULT now(),
    UNIQUE (organization_id, id),
    FOREIGN KEY (organization_id, location_id) REFERENCES locations (organization_id, id)
);
CREATE INDEX notices_status ON notices (organization_id, status, scheduled_at);
SELECT apply_tenant_rls('notices');

-- The notification a recipient received for a notice. Delivered and read counts are read from these.
ALTER TABLE notifications ADD COLUMN notice_id uuid;
ALTER TABLE notifications ADD CONSTRAINT notifications_notice_fk FOREIGN KEY (organization_id, notice_id) REFERENCES notices (organization_id, id);
CREATE INDEX notifications_notice ON notifications (organization_id, notice_id) WHERE notice_id IS NOT NULL;
