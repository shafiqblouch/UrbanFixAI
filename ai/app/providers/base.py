"""Provider interfaces (AI-03). Every method takes ScrubbedText, never str, so PII scrubbing cannot be skipped."""

from __future__ import annotations

from dataclasses import dataclass
from typing import Protocol

from app.pii import ScrubbedText


@dataclass(frozen=True)
class Usage:
    input_tokens: int = 0
    output_tokens: int = 0


@dataclass(frozen=True)
class LlmResult:
    text: str
    model: str
    usage: Usage = Usage()


@dataclass(frozen=True)
class VisionResult:
    text: str
    model: str
    usage: Usage = Usage()


@dataclass(frozen=True)
class SpeechResult:
    text: str
    confidence: float
    language: str
    model: str


@dataclass(frozen=True)
class EmbeddingResult:
    vectors: list[list[float]]
    model: str


class ProviderError(Exception):
    """Provider call failed (network, quota, bad response). Callers fall back to rules-only mode."""


class LlmProvider(Protocol):
    async def complete(self, *, model: str, system: ScrubbedText, user: ScrubbedText, json_mode: bool) -> LlmResult: ...


class VisionProvider(Protocol):
    async def describe(self, *, model: str, prompt: ScrubbedText, image: bytes, mime_type: str) -> VisionResult: ...

    async def describe_images(self, *, model: str, prompt: ScrubbedText, images: list[bytes], mime_type: str) -> VisionResult: ...


class SpeechProvider(Protocol):
    # Audio carries no text to scrub; it is stripped of identity at the source by not sending any metadata.
    async def transcribe(self, *, model: str, audio: bytes, mime_type: str, language_hint: str | None) -> SpeechResult: ...


class EmbeddingProvider(Protocol):
    async def embed(self, *, model: str, texts: list[ScrubbedText]) -> EmbeddingResult: ...


class Unconfigured:
    """Stands in for a provider nobody has chosen yet (AI-01 has not compared embedding models), so a missing choice
    fails loudly instead of returning meaningless vectors."""

    def __init__(self, kind: str) -> None:
        self._kind = kind

    async def embed(self, **_: object):
        raise ProviderError(f"No {self._kind} provider is configured")
