"""Photo interpretation (AI-08). The model may only name VISIBLE issues from a fixed label list, with cautious
wording, and may not claim hidden faults. Anything else is rejected and retried, and persistent violations fall back
to a safe "could not interpret" result instead of passing the claim on."""

from __future__ import annotations

import io
import json
import re
from dataclasses import dataclass

from PIL import Image, ImageOps, UnidentifiedImageError
from pydantic import BaseModel, ConfigDict, Field, ValidationError

from app.config import Settings
from app.pii import ScrubbedText
from app.prompts import load_prompt
from app.providers import Providers

MAX_BYTES = 10 * 1024 * 1024
MAX_SIDE = 1280
MIN_SIDE = 200
LOW_CONFIDENCE = 0.5

ALLOWED_LABELS = (
    "pothole", "road_damage", "water_leak", "standing_water", "sewage_overflow", "blocked_drain", "open_manhole",
    "garbage_pile", "illegal_dumping", "broken_streetlight", "exposed_wire", "fallen_tree", "damaged_wall",
    "damaged_fence", "graffiti", "parking_obstruction", "no_visible_issue", "unclear_image",
)
NON_ISSUE = {"no_visible_issue", "unclear_image"}

# Claims about what cannot be seen from a photograph, or certainty about causes and futures.
_HIDDEN_FAULT = [re.compile(p, re.IGNORECASE) for p in [
    r"\bunderground\b", r"\bunderneath\b", r"\bbeneath\b", r"\bbehind (?:the )?(?:wall|plaster|paint|tiles?|panel)",
    r"\binside (?:the |a )?(?:wall|pipe|pipes|cable|cables|conduit|transformer|ceiling|slab|foundation|duct)",
    r"\b(?:internal|hidden|concealed|underlying|latent)\b", r"\bstructural(?:ly)? (?:failure|damage|defect|integrity|weakness|unsafe)",
    r"\bfoundation\b", r"\bcorro(?:ded|sion) (?:inside|within)", r"\broot cause\b", r"\bcaused by\b", r"\bdue to\b",
    r"\bis the cause\b", r"\bwill (?:collapse|burst|fail|flood|explode|cause|lead)\b", r"\b(?:about|going|likely|bound|set) to (?:fall|collapse|fail|burst|break|flood|explode|cause|lead|get worse)\b", r"\beventually\b",
    r"\bdefinitely\b", r"\bcertainly\b", r"\bundoubtedly\b", r"\bclearly (?:a|an|the) (?:fault|defect|leak)\b",
    r"\b(?:faulty|damaged|broken) (?:wiring|pipe ?work|plumbing)\b(?! (?:is |are )?(?:visible|can be seen))",
    r"\bpressure (?:loss|drop)\b", r"\bgas leak\b",
]]
_HEDGE = re.compile(r"\b(?:possible|possibly|appears?|appeared|seems?|looks?|may|might|could|visible|can be seen|not clear|cannot be determined)\b", re.IGNORECASE)


class PhotoError(Exception):
    def __init__(self, code: str) -> None:
        super().__init__(code)
        self.code = code


class ModelPhotoOutput(BaseModel):
    model_config = ConfigDict(extra="forbid")

    labels: list[str] = Field(min_length=1, max_length=4)
    description: str = Field(min_length=1, max_length=400)
    confidence: float = Field(ge=0, le=1)


class PhotoResult(BaseModel):
    labels: list[str]
    description: str
    needs_more_info: bool
    confidence: float
    model: str
    prompt_version: str
    degraded: bool = False


@dataclass(frozen=True)
class Prepared:
    jpeg: bytes
    width: int
    height: int


def prepare_image(data: bytes) -> Prepared:
    """Validate, apply orientation, drop all metadata (EXIF/GPS) and downscale. The provider only sees pixels."""
    if not data:
        raise PhotoError("empty_file")
    if len(data) > MAX_BYTES:
        raise PhotoError("file_too_large")
    try:
        with Image.open(io.BytesIO(data)) as img:
            if img.format not in {"JPEG", "PNG", "WEBP"}:
                raise PhotoError("unsupported_file_type")
            img = ImageOps.exif_transpose(img).convert("RGB")
    except PhotoError:
        raise
    except (UnidentifiedImageError, OSError, ValueError) as exc:
        raise PhotoError("unsupported_file_type" if isinstance(exc, UnidentifiedImageError) else "corrupt_image") from exc

    img.thumbnail((MAX_SIDE, MAX_SIDE))
    out = io.BytesIO()
    img.save(out, format="JPEG", quality=88)  # no exif= argument: nothing is carried over
    return Prepared(out.getvalue(), img.width, img.height)


def check_description(description: str, labels: list[str]) -> str | None:
    """Returns a problem description, or None when the wording is acceptable."""
    for rx in _HIDDEN_FAULT:
        if m := rx.search(description):
            return f"claims something that cannot be seen in a photo ('{m.group(0)}')"
    if not set(labels) <= NON_ISSUE and not _HEDGE.search(description):
        return "must use cautious wording such as 'possible' or 'appears to'"
    return None


def _extract_json(raw: str) -> dict:
    text = raw.strip()
    if text.startswith("```"):
        text = text.strip("`").removeprefix("json").strip()
    return json.loads(text)


def _safe_fallback(model: str, version: str) -> PhotoResult:
    return PhotoResult(
        labels=["unclear_image"],
        description="The photo could not be interpreted reliably; please describe the problem in words.",
        needs_more_info=True, confidence=0.0, model=model, prompt_version=version, degraded=True,
    )


async def interpret(data: bytes, providers: Providers, settings: Settings) -> PhotoResult:
    prepared = prepare_image(data)
    version = settings.prompt_versions["photo_interpretation"]
    prompt = ScrubbedText(load_prompt("photo_interpretation", version).format(labels=", ".join(ALLOWED_LABELS)))

    feedback = ""
    last_model = settings.vision_model
    for _ in range(settings.max_output_retries + 1):
        result = await providers.vision.describe(
            model=settings.vision_model, prompt=ScrubbedText(prompt + feedback), image=prepared.jpeg, mime_type="image/jpeg"
        )
        last_model = result.model
        try:
            out = ModelPhotoOutput.model_validate(_extract_json(result.text))
            unknown = [label for label in out.labels if label not in ALLOWED_LABELS]
            if unknown:
                raise ValueError(f"labels {unknown} are not in the allowed list")
            if len(out.labels) > 1 and out.labels[0] in NON_ISSUE and set(out.labels) & (set(ALLOWED_LABELS) - NON_ISSUE):
                raise ValueError("'no_visible_issue'/'unclear_image' cannot be combined with issue labels")
            if problem := check_description(out.description, out.labels):
                raise ValueError(f"description {problem}")
        except (ValueError, ValidationError) as exc:
            feedback = f"\n\n[Your previous reply was rejected: {str(exc)[:250]}. Reply with valid JSON only.]"
            continue

        small = min(prepared.width, prepared.height) < MIN_SIDE
        return PhotoResult(
            labels=list(dict.fromkeys(out.labels)),
            description=out.description.strip(),
            needs_more_info=out.confidence < LOW_CONFIDENCE or bool(set(out.labels) & NON_ISSUE) or small,
            confidence=out.confidence, model=last_model, prompt_version=version,
        )

    return _safe_fallback(last_model, version)
