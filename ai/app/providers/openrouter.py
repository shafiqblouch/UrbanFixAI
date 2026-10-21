"""OpenRouter (OpenAI-compatible) provider for LLM, vision and embeddings. Speech comes from a dedicated
provider once AI-01 picks one."""

from __future__ import annotations

import base64

import httpx

from app.pii import ScrubbedText
from app.providers.base import EmbeddingResult, LlmResult, ProviderError, Usage, VisionResult


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

    async def embed(self, *, model: str, texts: list[ScrubbedText]) -> EmbeddingResult:
        data = await self._post("/embeddings", {"model": model, "input": list(texts)})
        try:
            vectors = [item["embedding"] for item in sorted(data["data"], key=lambda i: i["index"])]
        except (KeyError, TypeError) as exc:
            raise ProviderError("OpenRouter embeddings response malformed") from exc
        return EmbeddingResult(vectors, data.get("model", model))
