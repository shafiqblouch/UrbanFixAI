from __future__ import annotations

import os
from dataclasses import dataclass, field, replace

# Chosen in AI-01 (docs/AI-01-model-selection.md). Provisional until re-run on the real golden set (QA-03).
RECOMMENDED_LLM_PROVIDER = "openrouter"
RECOMMENDED_LLM_MODEL = "anthropic/claude-haiku-5.5"
RECOMMENDED_FALLBACK_MODEL = "deepseek/deepseek-v4.1-flash"
# Vision is NOT yet compared (AI-01 covered text only): same family as the text model until the photo test exists.
RECOMMENDED_VISION_MODEL = "anthropic/claude-haiku-5.5"


@dataclass(frozen=True)
class Settings:
    """All behaviour that varies by environment lives here, so switching provider is configuration only."""

    # Service-to-service auth: CORE sends `Authorization: Bearer <token>`. Two slots allow rotation.
    service_token: str = ""
    service_token_previous: str = ""

    # Provider selection (AI-03). Names are keys in app.providers.registry.
    llm_provider: str = "fake"
    vision_provider: str = "fake"
    speech_provider: str = "fake"
    # No default choice yet: AI-01 has not compared embedding models. Set AI_EMBEDDING_PROVIDER and AI_EMBEDDING_MODEL.
    embedding_provider: str = "unconfigured"

    # Pinned model identifiers: a model change is a reviewed config change, never a silent default.
    llm_model: str = "fake-llm-1"
    vision_model: str = "fake-vision-1"
    speech_model: str = "fake-speech-1"
    embedding_model: str = "fake-embed-1"

    # Pinned prompt versions (files under app/prompts/<name>/<version>.txt).
    prompt_versions: dict[str, str] = field(default_factory=lambda: {"complaint_understanding": "v3", "photo_interpretation": "v1", "speech_transcription": "v1", "photo_comparison": "v1"})

    # Used once if the primary model's provider call fails (AI-01 picked a second vendor on purpose).
    llm_fallback_model: str = ""

    openrouter_api_key: str = ""
    openrouter_base_url: str = "https://openrouter.ai/api/v1"
    request_timeout_seconds: float = 30.0
    max_output_retries: int = 2

    @staticmethod
    def from_env(env: dict[str, str] | None = None) -> Settings:
        e = os.environ if env is None else env
        d = Settings()
        # With an OpenRouter key present, the AI-01 recommendation is the default; env vars still override.
        live = bool(e.get("OPENROUTER_API_KEY"))
        d = replace(
            d,
            llm_provider=RECOMMENDED_LLM_PROVIDER if live else d.llm_provider,
            llm_model=RECOMMENDED_LLM_MODEL if live else d.llm_model,
            llm_fallback_model=RECOMMENDED_FALLBACK_MODEL if live else d.llm_fallback_model,
            vision_provider=RECOMMENDED_LLM_PROVIDER if live else d.vision_provider,
            vision_model=RECOMMENDED_VISION_MODEL if live else d.vision_model,
        )
        return Settings(
            llm_fallback_model=e.get("AI_LLM_FALLBACK_MODEL", d.llm_fallback_model),
            service_token=e.get("AI_SERVICE_TOKEN", d.service_token),
            service_token_previous=e.get("AI_SERVICE_TOKEN_PREVIOUS", d.service_token_previous),
            llm_provider=e.get("AI_LLM_PROVIDER", d.llm_provider),
            vision_provider=e.get("AI_VISION_PROVIDER", d.vision_provider),
            speech_provider=e.get("AI_SPEECH_PROVIDER", d.speech_provider),
            embedding_provider=e.get("AI_EMBEDDING_PROVIDER", d.embedding_provider),
            llm_model=e.get("AI_LLM_MODEL", d.llm_model),
            vision_model=e.get("AI_VISION_MODEL", d.vision_model),
            speech_model=e.get("AI_SPEECH_MODEL", d.speech_model),
            embedding_model=e.get("AI_EMBEDDING_MODEL", d.embedding_model),
            prompt_versions={
                **d.prompt_versions,
                **{
                    k.removeprefix("AI_PROMPT_").lower(): v
                    for k, v in e.items()
                    if k.startswith("AI_PROMPT_")
                },
            },
            openrouter_api_key=e.get("OPENROUTER_API_KEY", d.openrouter_api_key),
            openrouter_base_url=e.get("OPENROUTER_BASE_URL", d.openrouter_base_url),
            request_timeout_seconds=float(e.get("AI_REQUEST_TIMEOUT_SECONDS", d.request_timeout_seconds)),
            max_output_retries=int(e.get("AI_MAX_OUTPUT_RETRIES", d.max_output_retries)),
        )
