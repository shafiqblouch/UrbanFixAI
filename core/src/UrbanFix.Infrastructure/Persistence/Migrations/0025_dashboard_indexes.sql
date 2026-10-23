-- Dashboard query tuning (DB-06): the dashboards read complaints by when they were created or resolved, and jobs by status.
CREATE INDEX IF NOT EXISTS complaints_created ON complaints (organization_id, created_at DESC);
CREATE INDEX IF NOT EXISTS complaints_resolved ON complaints (organization_id, resolved_at) WHERE resolved_at IS NOT NULL;
CREATE INDEX IF NOT EXISTS jobs_status ON jobs (organization_id, status);
CREATE INDEX IF NOT EXISTS jobs_created ON jobs (organization_id, created_at DESC);
CREATE INDEX IF NOT EXISTS complaint_history_reopen ON complaint_status_history (organization_id, to_status, from_status);
CREATE INDEX IF NOT EXISTS complaint_feedback_complaint ON complaint_feedback (organization_id, complaint_id);
