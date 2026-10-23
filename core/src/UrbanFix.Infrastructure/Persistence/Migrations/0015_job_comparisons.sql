-- Advisory before/after comparison of a job's evidence photos (AI-12). One row per pair of photos compared.
CREATE TABLE job_comparisons (
    id                        uuid PRIMARY KEY,
    organization_id           uuid NOT NULL REFERENCES organizations(id),
    job_id                    uuid NOT NULL,
    before_media_id           uuid NOT NULL,
    after_media_id            uuid NOT NULL,
    same_location_likelihood  real NOT NULL,
    change_note               text NOT NULL,
    needs_more_info           boolean NOT NULL,
    confidence                real NOT NULL,
    degraded                  boolean NOT NULL DEFAULT false,
    model                     text,
    prompt_version            text,
    requested_by              uuid,
    created_at                timestamptz NOT NULL DEFAULT now(),
    FOREIGN KEY (organization_id, job_id) REFERENCES jobs (organization_id, id)
);
CREATE INDEX job_comparisons_job ON job_comparisons (organization_id, job_id, created_at DESC);
SELECT apply_tenant_rls('job_comparisons');
