"""Complaint understanding (AI-05): scrub → prompt → provider → strict validation with bounded retries."""

from __future__ import annotations

import json

from pydantic import ValidationError

from app.config import Settings
from app.pii import ScrubbedText, scrub
from app.prompts import load_prompt
from app.providers import Providers
from app.providers.base import ProviderError
from app.schemas import ModelOutput, UnderstandRequest, UnderstandResponse

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
    system = scrub(template.format(categories=category_lines)).text

    scrubbed = scrub(req.text, req.known_names)
    user = scrubbed.text
    feedback = ""

    last_error = "no attempt made"
    for _ in range(settings.max_output_retries + 1):
        result = await providers.llm.complete(
            model=settings.llm_model,
            system=system,
            user=ScrubbedText(user + feedback),
            json_mode=True,
        )
        try:
            output = ModelOutput.model_validate(_extract_json(result.text))
            if output.category not in allowed:
                raise ValueError(f"category '{output.category}' is not one of the allowed keys")
        except (ValueError, ValidationError) as exc:
            last_error = str(exc)[:300]
            feedback = f"\n\n[Your previous reply was rejected: {last_error}. Reply with valid JSON only.]"
            continue

        return UnderstandResponse(
            category=output.category,
            summary=scrub(output.summary, req.known_names).text,
            location_text=output.location_text,
            language=output.language,
            confidence=output.confidence,
            needs_more_info=output.category == "other" or output.confidence < LOW_CONFIDENCE,
            model=result.model,
            prompt_version=version,
            pii_redactions=sum(scrubbed.counts.values()),
        )

    raise InvalidModelOutput(last_error)


__all__ = ["InvalidModelOutput", "ProviderError", "understand"]
