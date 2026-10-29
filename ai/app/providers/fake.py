"""Deterministic provider for tests and local development. Records everything it is sent."""

from __future__ import annotations

import hashlib
import json

from app.pii import ScrubbedText
from app.providers.base import EmbeddingResult, LlmResult, SpeechResult, Usage, VisionResult


class FakeProvider:
    """Implements all four provider interfaces."""

    def __init__(self) -> None:
        self.sent_text: list[str] = []
        self.llm_replies: list[str] = []  # queued raw replies; used before the default
        self.vision_replies: list[str] = []
        self.images_seen: list[bytes] = []
        self.vision_usage = Usage(0, 0)
        self.speech_replies: list[SpeechResult] = []
        self.audio_seen: list[tuple[bytes, str]] = []

    async def complete(self, *, model: str, system: ScrubbedText, user: ScrubbedText, json_mode: bool) -> LlmResult:
        self.sent_text += [system, user]
        if self.llm_replies:
            return LlmResult(self.llm_replies.pop(0), model, Usage(len(user), 10))
        reply = {
            "category": "other",
            "summary": user[:120],
            "location_text": None,
            "language": "en",
            "confidence": 0.5,
        }
        return LlmResult(json.dumps(reply), model, Usage(len(user), 10))

    async def describe(self, *, model: str, prompt: ScrubbedText, image: bytes, mime_type: str) -> VisionResult:
        self.sent_text.append(prompt)
        self.images_seen.append(image)
        if self.vision_replies:
            return VisionResult(self.vision_replies.pop(0), model, self.vision_usage)
        return VisionResult("possible visible issue", model, self.vision_usage)

    async def describe_images(self, *, model: str, prompt: ScrubbedText, images: list[bytes], mime_type: str) -> VisionResult:
        self.sent_text.append(prompt)
        self.images_seen.extend(images)
        if self.vision_replies:
            return VisionResult(self.vision_replies.pop(0), model, self.vision_usage)
        return VisionResult('{"same_location_likelihood": 0.7, "change_note": "The area appears to look different.", "confidence": 0.6}', model, self.vision_usage)

    async def transcribe(self, *, model: str, audio: bytes, mime_type: str, language_hint: str | None, prompt_version: str = "v1") -> SpeechResult:
        self.audio_seen.append((audio, mime_type))
        if self.speech_replies:
            return self.speech_replies.pop(0)
        return SpeechResult("fake transcript", 0.9, language_hint or "en", model)

    async def embed(self, *, model: str, texts: list[ScrubbedText]) -> EmbeddingResult:
        self.sent_text += list(texts)
        return EmbeddingResult([_hash_vector(t) for t in texts], model)


def _hash_vector(text: str, dims: int = 8) -> list[float]:
    digest = hashlib.sha256(text.encode()).digest()
    return [b / 255 for b in digest[:dims]]
