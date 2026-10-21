"""Provider registry: names in Settings map to implementations here. Adding a provider means adding a factory."""

from __future__ import annotations

from collections.abc import Callable
from dataclasses import dataclass

from app.config import Settings
from app.providers.base import EmbeddingProvider, LlmProvider, ProviderError, SpeechProvider, VisionProvider
from app.providers.fake import FakeProvider
from app.providers.openrouter import OpenRouterProvider


@dataclass
class Providers:
    llm: LlmProvider
    vision: VisionProvider
    speech: SpeechProvider
    embedding: EmbeddingProvider


def build_providers(settings: Settings) -> Providers:
    cache: dict[str, object] = {}

    def fake() -> object:
        return cache.setdefault("fake", FakeProvider())

    def openrouter() -> object:
        return cache.setdefault(
            "openrouter",
            OpenRouterProvider(settings.openrouter_api_key, settings.openrouter_base_url, settings.request_timeout_seconds),
        )

    factories: dict[str, Callable[[], object]] = {"fake": fake, "openrouter": openrouter}

    def pick(kind: str, name: str, supported: set[str]) -> object:
        if name not in factories or name not in supported:
            raise ProviderError(f"Unknown or unsupported {kind} provider '{name}'")
        return factories[name]()

    return Providers(
        llm=pick("llm", settings.llm_provider, {"fake", "openrouter"}),  # type: ignore[arg-type]
        vision=pick("vision", settings.vision_provider, {"fake", "openrouter"}),  # type: ignore[arg-type]
        speech=pick("speech", settings.speech_provider, {"fake"}),  # type: ignore[arg-type]
        embedding=pick("embedding", settings.embedding_provider, {"fake", "openrouter"}),  # type: ignore[arg-type]
    )
