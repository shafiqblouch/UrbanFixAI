-- Voice-note transcription (AI-09).
ALTER TABLE complaint_media
    ADD COLUMN transcript text,
    ADD COLUMN transcript_confidence real,
    ADD COLUMN transcript_language text,
    ADD COLUMN transcript_status text CHECK (transcript_status IN ('pending', 'done', 'failed', 'skipped'));

-- More AI tasks leave suggestion rows (AI-15): voice transcription now, photo and before/after checks next.
ALTER TABLE ai_suggestions DROP CONSTRAINT ai_suggestions_task_check;
ALTER TABLE ai_suggestions ADD CONSTRAINT ai_suggestions_task_check
    CHECK (task IN ('understanding', 'transcription', 'photo', 'before_after'));

-- Photo interpretation (AI-08 wired into CORE, AI-10 safety flag from images).
ALTER TABLE complaint_media
    ADD COLUMN ai_status text CHECK (ai_status IN ('done', 'failed', 'skipped')),
    ADD COLUMN ai_labels text[] NOT NULL DEFAULT '{}',
    ADD COLUMN ai_description text,
    ADD COLUMN ai_needs_more_info boolean;
