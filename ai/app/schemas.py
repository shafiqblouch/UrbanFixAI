"""Typed request/response contracts between CORE and the AI service (AI-02)."""

from __future__ import annotations

from typing import Literal

from pydantic import BaseModel, ConfigDict, Field

Language = Literal["en", "ur", "roman_ur", "mixed"]


class CategoryDef(BaseModel):
    key: str = Field(min_length=1, max_length=64, pattern=r"^[a-z0-9_\-]+$")
    label: str = Field(min_length=1, max_length=120)
    description: str | None = Field(default=None, max_length=300)


class UnderstandRequest(BaseModel):
    text: str = Field(min_length=1, max_length=4000)
    categories: list[CategoryDef] = Field(min_length=1, max_length=60)
    # Names CORE already knows (resident, neighbours named in the form) so they are scrubbed even without titles.
    known_names: list[str] = Field(default_factory=list, max_length=20)
    # Local landmarks ("Madni Masjid", "Green Gate") so "near the mosque" can be turned into a place.
    landmarks: list[str] = Field(default_factory=list, max_length=100)
    language_hint: Language | None = None


class ModelOutput(BaseModel):
    """What the LLM must return; validated strictly (no extra keys), invalid output is rejected and retried."""

    model_config = ConfigDict(extra="forbid")

    category: str
    summary: str = Field(min_length=1, max_length=600)
    location_text: str | None = Field(default=None, max_length=300)
    language: Language
    confidence: float = Field(ge=0, le=1)


class UnderstandResponse(BaseModel):
    category: str
    summary: str
    location_text: str | None
    language: Language
    confidence: float
    needs_more_info: bool
    model: str
    prompt_version: str
    pii_redactions: int
    # Complaint text looked like an instruction aimed at the model; a person should review the result.
    injection_suspected: bool = False


class ProviderStatus(BaseModel):
    llm: str
    vision: str
    speech: str
    embedding: str


class ErrorBody(BaseModel):
    error: str
    detail: str | None = None
