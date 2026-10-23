"""Voice-note transcription (AI-09). Audio goes to the speech provider; the transcript comes back with a confidence
the app shows beside the audio. The confidence is the model's own estimate, so it is a hint for staff, not a score."""

from __future__ import annotations

from pydantic import BaseModel

from app.config import Settings
from app.providers import Providers
from app.schemas import Language, UsageBody

MAX_BYTES = 10 * 1024 * 1024
LOW_CONFIDENCE = 0.6
MAX_TRANSCRIPT = 4000

# Container formats the speech provider can take, by the type the server detected (see Media.cs in CORE).
SUPPORTED_TYPES = {"audio/mpeg", "audio/wav", "audio/ogg", "audio/mp4", "audio/aac", "audio/webm"}


class SpeechError(Exception):
    def __init__(self, code: str) -> None:
        super().__init__(code)
        self.code = code


class TranscribeResponse(BaseModel):
    transcript: str
    confidence: float
    language: Language
    low_confidence: bool
    model: str
    prompt_version: str
    # Speech providers report no tokens; the call itself is what gets counted.
    usage: UsageBody = UsageBody()


async def transcribe(audio: bytes, mime_type: str, language_hint: str | None, providers: Providers, settings: Settings) -> TranscribeResponse:
    if not audio:
        raise SpeechError("empty_file")
    if len(audio) > MAX_BYTES:
        raise SpeechError("file_too_large")
    if mime_type.split(";")[0].strip().lower() not in SUPPORTED_TYPES:
        raise SpeechError("unsupported_file_type")

    result = await providers.speech.transcribe(model=settings.speech_model, audio=audio, mime_type=mime_type, language_hint=language_hint)
    text = result.text.strip()[:MAX_TRANSCRIPT]
    language = result.language if result.language in {"en", "ur", "roman_ur", "mixed"} else "mixed"
    confidence = 0.0 if not text else min(max(result.confidence, 0.0), 1.0)
    return TranscribeResponse(
        transcript=text, confidence=confidence, language=language,  # type: ignore[arg-type]
        low_confidence=confidence < LOW_CONFIDENCE, model=result.model,
        prompt_version=settings.prompt_versions["speech_transcription"],
    )
