"""OpenRouter (OpenAI-compatible) provider for LLM, vision and embeddings. Speech goes through a chat model that
accepts audio input; AI-01 has not yet compared models for Urdu speech, so the model is a configured choice."""

from __future__ import annotations

import base64
import json
from typing import ClassVar

import httpx

from app.pii import ScrubbedText
from app.prompts import load_prompt
from app.providers.base import EmbeddingResult, LlmResult, ProviderError, SpeechResult, Usage, VisionResult


class OpenRouterProvider:
    def __init__(self, api_key: str, base_url: str, timeout: float, client: httpx.AsyncClient | None = None) -> None:
        if not api_key:
            raise ProviderError("OPENROUTER_API_KEY is not set")
        self._client = client or httpx.AsyncClient(
            base_url=base_url,
            timeout=timeout,
            headers={"Authorization": f"Bearer {api_key}", "X-Title": "UrbanFix"},
        )

    async def _post(self, path: str, body: dict) -> dict:
        try:
            response = await self._client.post(path, json=body)
            response.raise_for_status()
            return response.json()
        except (httpx.HTTPError, ValueError) as exc:
            raise ProviderError(f"OpenRouter request failed: {exc.__class__.__name__}") from exc

    @staticmethod
    def _content(data: dict) -> str:
        try:
            return data["choices"][0]["message"]["content"]
        except (KeyError, IndexError, TypeError) as exc:
            raise ProviderError("OpenRouter response had no message content") from exc

    @staticmethod
    def _usage(data: dict) -> Usage:
        u = data.get("usage") or {}
        return Usage(int(u.get("prompt_tokens", 0)), int(u.get("completion_tokens", 0)))

    async def complete(self, *, model: str, system: ScrubbedText, user: ScrubbedText, json_mode: bool) -> LlmResult:
        body: dict = {
            "model": model,
            "temperature": 0,
            "messages": [{"role": "system", "content": system}, {"role": "user", "content": user}],
        }
        if json_mode:
            body["response_format"] = {"type": "json_object"}
        data = await self._post("/chat/completions", body)
        return LlmResult(self._content(data), data.get("model", model), self._usage(data))

    async def describe(self, *, model: str, prompt: ScrubbedText, image: bytes, mime_type: str) -> VisionResult:
        url = f"data:{mime_type};base64,{base64.b64encode(image).decode()}"
        data = await self._post(
            "/chat/completions",
            {
                "model": model,
                "temperature": 0,
                "messages": [
                    {
                        "role": "user",
                        "content": [{"type": "text", "text": prompt}, {"type": "image_url", "image_url": {"url": url}}],
                    }
                ],
            },
        )
        return VisionResult(self._content(data), data.get("model", model), self._usage(data))

    # OpenRouter names the container, not the MIME type.
    _AUDIO_FORMATS: ClassVar[dict[str, str]] = {"audio/mpeg": "mp3", "audio/wav": "wav", "audio/ogg": "ogg", "audio/mp4": "m4a", "audio/aac": "aac"}

    async def transcribe(self, *, model: str, audio: bytes, mime_type: str, language_hint: str | None, prompt_version: str = "v1") -> SpeechResult:
        fmt = self._AUDIO_FORMATS.get(mime_type.split(";")[0].strip().lower())
        if fmt is None:
            raise ProviderError(f"Audio type {mime_type} is not supported by the speech provider")
        prompt = load_prompt("speech_transcription", prompt_version).format(hint=language_hint or "none")
        data = await self._post(
            "/chat/completions",
            {
                "model": model,
                "temperature": 0,
                "response_format": {"type": "json_object"},
                "messages": [
                    {
                        "role": "user",
                        "content": [
                            {"type": "text", "text": prompt},
                            {"type": "input_audio", "input_audio": {"data": base64.b64encode(audio).decode(), "format": fmt}},
                        ],
                    }
                ],
            },
        )
        try:
            body = json.loads(self._content(data).strip().strip("`").removeprefix("json").strip())
            return SpeechResult(str(body["transcript"]), float(body["confidence"]), str(body["language"]), data.get("model", model), str(body.get("translation_en") or ""))
        except (ValueError, KeyError, TypeError) as exc:
            raise ProviderError("Speech response was not the expected JSON") from exc

    async def describe_images(self, *, model: str, prompt: ScrubbedText, images: list[bytes], mime_type: str) -> VisionResult:
        parts: list[dict] = [{"type": "text", "text": prompt}]
        for image in images:
            url = f"data:{mime_type};base64,{base64.b64encode(image).decode()}"
            parts.append({"type": "image_url", "image_url": {"url": url}})
        data = await self._post("/chat/completions", {"model": model, "temperature": 0, "messages": [{"role": "user", "content": parts}]})
        return VisionResult(self._content(data), data.get("model", model), self._usage(data))

    async def embed(self, *, model: str, texts: list[ScrubbedText]) -> EmbeddingResult:
        data = await self._post("/embeddings", {"model": model, "input": list(texts)})
        try:
            vectors = [item["embedding"] for item in sorted(data["data"], key=lambda i: i["index"])]
        except (KeyError, TypeError) as exc:
            raise ProviderError("OpenRouter embeddings response malformed") from exc
        return EmbeddingResult(vectors, data.get("model", model))
