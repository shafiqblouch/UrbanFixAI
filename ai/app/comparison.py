"""Before/after comparison (AI-12). Advisory: it returns how likely the two photos show the same place and a cautious note
on what visibly changed. It never says the work is good or bad, and it never claims anything hidden (same wording rules
as photo interpretation, AI-08)."""

from __future__ import annotations

from pydantic import BaseModel, ConfigDict, Field, ValidationError

from app.config import Settings
from app.photos import LOW_CONFIDENCE, MIN_SIDE, _extract_json, check_description, prepare_image
from app.pii import ScrubbedText
from app.prompts import load_prompt
from app.providers import Providers
from app.schemas import UsageBody

SAME_PLACE_MIN = 0.5


class ModelComparison(BaseModel):
    model_config = ConfigDict(extra="forbid")

    same_location_likelihood: float = Field(ge=0, le=1)
    change_note: str = Field(min_length=1, max_length=500)
    confidence: float = Field(ge=0, le=1)


class ComparisonResult(BaseModel):
    same_location_likelihood: float
    change_note: str
    needs_more_info: bool
    confidence: float
    model: str
    prompt_version: str
    degraded: bool = False
    advisory: bool = True  # always: a person decides
    usage: UsageBody = Field(default_factory=UsageBody)


def _fallback(model: str, version: str) -> ComparisonResult:
    return ComparisonResult(
        same_location_likelihood=0.0, change_note="The photos could not be compared reliably; please check them by eye.",
        needs_more_info=True, confidence=0.0, model=model, prompt_version=version, degraded=True,
    )


async def compare(before: bytes, after: bytes, providers: Providers, settings: Settings) -> ComparisonResult:
    first, second = prepare_image(before), prepare_image(after)
    version = settings.prompt_versions["photo_comparison"]
    prompt = ScrubbedText(load_prompt("photo_comparison", version))

    feedback = ""
    last_model = settings.vision_model
    used_in = used_out = 0
    for _ in range(settings.max_output_retries + 1):
        result = await providers.vision.describe_images(
            model=settings.vision_model, prompt=ScrubbedText(prompt + feedback), images=[first.jpeg, second.jpeg], mime_type="image/jpeg"
        )
        last_model = result.model
        used_in += result.usage.input_tokens
        used_out += result.usage.output_tokens
        try:
            out = ModelComparison.model_validate(_extract_json(result.text))
            # Same wording rules as a single photo. A "no change" note needs no hedge, a described change does.
            if problem := check_description(out.change_note, ["change"]):
                raise ValueError(f"change_note {problem}")
        except (ValueError, ValidationError) as exc:
            feedback = f"\n\n[Your previous reply was rejected: {str(exc)[:250]}. Reply with valid JSON only.]"
            continue

        small = min(first.width, first.height, second.width, second.height) < MIN_SIDE
        return ComparisonResult(
            same_location_likelihood=out.same_location_likelihood,
            # When the photos look like different places, a described "change" would mislead; say so instead.
            change_note=out.change_note.strip(),
            needs_more_info=out.confidence < LOW_CONFIDENCE or out.same_location_likelihood < SAME_PLACE_MIN or small,
            confidence=out.confidence, model=last_model, prompt_version=version,
            usage=UsageBody(input_tokens=used_in, output_tokens=used_out),
        )

    fallback = _fallback(last_model, version)
    fallback.usage = UsageBody(input_tokens=used_in, output_tokens=used_out)
    return fallback
