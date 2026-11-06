-- The AI's judgement of how likely a complaint is genuine and actionable rather than a prank / test / abuse (AI-05).
-- Advisory only: a low value flags the complaint for a human reviewer; it never rejects or routes anything by itself.
ALTER TABLE complaints ADD COLUMN ai_plausibility real;
ALTER TABLE complaints ADD COLUMN ai_plausibility_reason text;
ALTER TABLE complaints ADD CONSTRAINT complaints_ai_plausibility_range CHECK (ai_plausibility IS NULL OR ai_plausibility BETWEEN 0 AND 1);

-- Kept on every understanding attempt too, so the history shows what each attempt judged (AI-15).
ALTER TABLE ai_suggestions ADD COLUMN plausibility real;
ALTER TABLE ai_suggestions ADD COLUMN plausibility_reason text;
ALTER TABLE ai_suggestions ADD CONSTRAINT ai_suggestions_plausibility_range CHECK (plausibility IS NULL OR plausibility BETWEEN 0 AND 1);
