"""Text embeddings for similar-complaint search (AI-11). Text is scrubbed of names and numbers before it leaves the service."""

from __future__ import annotations

from pydantic import BaseModel, Field

from app.config import Settings
from app.pii import scrub
from app.providers import Providers
from app.providers.base import ProviderError
from app.schemas import UsageBody


class EmbedRequest(BaseModel):
    texts: list[str] = Field(min_length=1, max_length=32)
    known_names: list[str] = Field(default_factory=list, max_length=20)


class EmbedResponse(BaseModel):
    vectors: list[list[float]]
    model: str
    dimensions: int
    pii_redactions: int
    usage: UsageBody = UsageBody()


async def embed_texts(req: EmbedRequest, providers: Providers, settings: Settings) -> EmbedResponse:
    cleaned = [scrub(t[:4000], req.known_names) for t in req.texts]
    result = await providers.embedding.embed(model=settings.embedding_model, texts=[c.text for c in cleaned])
    if len(result.vectors) != len(cleaned) or not result.vectors or any(len(v) != len(result.vectors[0]) for v in result.vectors):
        raise ProviderError("Embedding provider returned vectors of the wrong shape")
    return EmbedResponse(
        vectors=result.vectors, model=result.model, dimensions=len(result.vectors[0]),
        pii_redactions=sum(sum(c.counts.values()) for c in cleaned),
        # Embedding APIs bill input only; about four characters to a token is close enough for a cap.
        usage=UsageBody(input_tokens=sum(len(c.text) for c in cleaned) // 4 + 1),
    )
