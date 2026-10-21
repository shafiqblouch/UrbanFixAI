# AI-01: model selection spike (complaint understanding)

**Status: partial.** Text classification is compared. Photos, voice transcription and embeddings are **not** tested yet.

## Recommendation

Use **`anthropic/claude-haiku-5.5`** via OpenRouter for complaint understanding, with **`deepseek/deepseek-v4.1-flash`** as the configured fallback (it is cheaper and just as accurate here, but slower at the tail and a different vendor, which is useful when one is down).

This is a provisional choice. The test set is too easy to separate the models on accuracy (see limits), so the pick rests on latency, cost, and reliable JSON output. **Re-run on the real golden set (QA-03) before the POC demo**: `python spike/run_spike.py <models...>`.

## Results (60 complaints: 20 English, 20 Roman Urdu, 20 Urdu script; 7 categories plus "other")

| Model | Accuracy | Urdu script | Valid JSON | p50 | p95 | Cost per 1,000 complaints |
|---|---|---|---|---|---|---|
| anthropic/claude-haiku-5.5 | 100% | 100% | 100% | 1.3 s | 2.0 s | $0.11 |
| deepseek/deepseek-v4.1-flash | 100% | 100% | 100% | 1.9 s | 3.7 s | $0.08 |
| openai/gpt-5.6-luna | 100% | 100% | 100% | 2.6 s | 3.7 s | $0.14 |
| google/gemini-3.5-flash-lite | 100% | 100% | 100% | 1.3 s | 2.3 s | $0.27 |
| qwen/qwen3.7-flash | 97% | 90% | 100% | 14.8 s | 20.5 s | $0.15 |
| mistralai/mistral-large-4-0 | 100% | 100% | 100% | 7.8 s | 18.7 s | $3.85 |
| anthropic/claude-sonnet-5.5 | 100% | 100% | 100% | 1.3 s | 2.2 s | $1.89 |

Qwen's two misses were both Urdu script (a drainage complaint called "roads", a transformer-spark complaint called "noise"), and it was far slower. The larger models (Mistral Large, Sonnet) cost 15 to 35 times more for no gain on this set.

All figures come through the production pipeline (PII scrub, versioned prompt, strict schema validation with retries), so they include any retry cost. Raw output is in `ai/spike/results.json`.

## Limits of this result

- **Synthetic data.** The 60 complaints in `ai/spike/dataset.json` were written by the assistant, not collected from residents, and the Urdu and Roman Urdu text has not been reviewed by a native speaker. They are clean and unambiguous, which is why almost every model scores 100%.
- **Missing hard cases.** Real complaints include mixed topics, vague text, typos, voice-note transcripts, and very short messages. Accuracy differences will show up there.
- **Category only.** Priority, duplicate detection, safety flags (AI-10) and location extraction are not scored.
- **Not tested:** the 50 photos (vision), Urdu voice transcription, multilingual embeddings. Speech has no provider wired up yet.
- **Single run.** Latency depends on OpenRouter routing at the time of the test.

## Operational notes

- Cost at the recommended model is about $0.11 per 1,000 complaints, so the AI budget is negligible next to staff time.
- Personal details are scrubbed before any provider call (AI-04). OpenRouter then routes to the model vendor, so confirm each vendor's data-retention terms before pilot.
- The OpenRouter key lives in `ai/.env` (gitignored). The key was shared in a chat, so **rotate it** before it is used for anything beyond this spike.
