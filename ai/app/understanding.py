"""Complaint understanding (AI-05): scrub → prompt → provider → strict validation with bounded retries."""

from __future__ import annotations

import json

from pydantic import ValidationError

from app.config import Settings
from app.injection import fence, looks_like_injection
from app.language import detect_language
from app.pii import ScrubbedText, scrub
from app.prompts import load_prompt
from app.providers import Providers
from app.providers.base import ProviderError
from app.schemas import ModelOutput, UnderstandRequest, UnderstandResponse, UsageBody

LOW_CONFIDENCE = 0.5


class InvalidModelOutput(Exception):
    pass


def _extract_json(raw: str) -> dict:
    text = raw.strip()
    if text.startswith("```"):
        text = text.strip("`")
        text = text.removeprefix("json").strip()
    return json.loads(text)


async def understand(req: UnderstandRequest, providers: Providers, settings: Settings) -> UnderstandResponse:
    version = settings.prompt_versions["complaint_understanding"]
    template = load_prompt("complaint_understanding", version)

    # Category labels are organisation-defined text, so they pass through the scrubber too.
    allowed = {c.key for c in req.categories} | {"other"}
    category_lines = "\n".join(
        f"- {c.key}: {c.label}" + (f" ({c.description})" if c.description else "") for c in req.categories
    )
    landmark_lines = "\n".join(f"- {name[:120]}" for name in req.landmarks) or "(none configured)"
    system = scrub(template.format(categories=category_lines, landmarks=landmark_lines)).text

    scrubbed = scrub(req.text, req.known_names)
    injection_suspected = looks_like_injection(req.text)
    # The complaint is fenced as data; the retry feedback below stays outside the fence.
    user = fence(scrubbed.text)
    feedback = ""

    last_error = "no attempt made"
    model = settings.llm_model
    used_in = used_out = 0
    for _ in range(settings.max_output_retries + 1):
        try:
            result = await providers.llm.complete(
                model=model, system=system, user=ScrubbedText(user + feedback), json_mode=True
            )
        except ProviderError:
            # One switch to the fallback model (a different vendor); if that also fails the error propagates.
            if not settings.llm_fallback_model or model == settings.llm_fallback_model:
                raise
            model = settings.llm_fallback_model
            continue
        used_in += result.usage.input_tokens
        used_out += result.usage.output_tokens
        try:
            output = ModelOutput.model_validate(_extract_json(result.text))
            if output.category not in allowed:
                raise ValueError(f"category '{output.category}' is not one of the allowed keys")
        except (ValueError, ValidationError) as exc:
            last_error = str(exc)[:300]
            feedback = f"\n\n[Your previous reply was rejected: {last_error}. Reply with valid JSON only.]"
            continue

        # Script and word counts are more reliable than the model for Urdu / Roman Urdu / English, so they win when sure.
        detected = detect_language(req.text)
        language = detected.language if detected.confidence >= 0.8 else output.language

        return UnderstandResponse(
            category=output.category,
            summary=scrub(output.summary, req.known_names).text,
            location_text=output.location_text,
            language=language,
            confidence=output.confidence,
            needs_more_info=output.category == "other" or output.confidence < LOW_CONFIDENCE or injection_suspected,
            model=result.model,
            prompt_version=version,
            pii_redactions=sum(scrubbed.counts.values()),
            injection_suspected=injection_suspected,
            usage=UsageBody(input_tokens=used_in, output_tokens=used_out),
        )

    raise InvalidModelOutput(last_error)


__all__ = ["InvalidModelOutput", "ProviderError", "understand"]
