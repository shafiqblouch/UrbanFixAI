-- Embeddings for similar-complaint search (AI-11). The vector column has no fixed size on purpose: the embedding model
-- is not chosen yet, and rows are only ever compared with rows from the same model. Once a model is chosen, add an
-- HNSW index on its size; until then an exact scan inside one organization is fast enough for pilot volumes.
CREATE EXTENSION IF NOT EXISTS vector;

CREATE TABLE complaint_embeddings (
    id               uuid PRIMARY KEY,
    organization_id  uuid NOT NULL REFERENCES organizations(id),
    complaint_id     uuid NOT NULL,
    -- "text" is the complaint wording; "photo" is the cautious description of one photo (a stand-in until an image model is chosen).
    source           text NOT NULL CHECK (source IN ('text', 'photo')),
    media_id         uuid,
    model            text NOT NULL,
    embedding        vector NOT NULL,
    created_at       timestamptz NOT NULL DEFAULT now(),
    FOREIGN KEY (organization_id, complaint_id) REFERENCES complaints (organization_id, id)
);
CREATE UNIQUE INDEX complaint_embeddings_unique ON complaint_embeddings
    (organization_id, complaint_id, source, COALESCE(media_id, '00000000-0000-0000-0000-000000000000'::uuid), model);
SELECT apply_tenant_rls('complaint_embeddings');
