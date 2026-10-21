"""Prompt-injection guard (AI-06). Complaint text is data: it is fenced, cannot close its own fence, and anything
that looks like an instruction raises a flag so a person reviews the result.

The hard guarantees do not depend on spotting attacks: the model's reply is validated against a closed schema and
a closed list of categories (see understanding.py), so an injected instruction cannot change the output format or
invent a category. The detector below only adds visibility."""

from __future__ import annotations

import re
import unicodedata

from app.pii import ScrubbedText

OPEN, CLOSE = "<complaint>", "</complaint>"

_PATTERNS = [
    # English
    r"ignore\s+(?:all\s+|any\s+|the\s+|your\s+)?(?:previous|prior|above|earlier|preceding)?\s*(?:instruction|prompt|rule|direction)s?",
    r"disregard\s+(?:all\s+|any\s+|the\s+|your\s+)?(?:previous|prior|above|earlier)?\s*(?:instruction|prompt|rule|direction)s?",
    r"forget\s+(?:everything|all|your|the)\b",
    r"(?:system|developer|hidden|initial)\s+(?:prompt|message|instruction)",
    r"you\s+are\s+now\b", r"\bact\s+as\b", r"\bpretend\s+(?:to\s+be|you)\b", r"new\s+instructions?",
    r"\b(?:reveal|print|show|repeat|leak)\b.{0,30}\b(?:prompt|instructions|rules)\b",
    r"respond\s+(?:only\s+)?with\b", r"\boutput\s+(?:only\s+)?(?:the\s+)?(?:json|following|text)\b",
    r"\bjailbreak\b", r"\bdeveloper\s+mode\b", r"\boverride\b",
    r"\bset\s+(?:the\s+)?(?:category|priority|confidence)\b", r"\"?category\"?\s*[:=]\s*\"?\w+",
    # markup that tries to escape the fence or fake a conversation
    r"</?\s*complaint\s*>", r"^\s*(?:system|assistant)\s*:", r"```", r"<\|.*?\|>",
    # Roman Urdu
    r"pichli\s+(?:saari\s+)?(?:hidayat|hidayaat|instructions?)", r"(?:hidayat|hidayaat|instructions?)\s+(?:ko\s+)?(?:bhool|nazar\s+andaz|ignore)",
    r"ab\s+tum\s+\w+\s+ho", r"system\s+prompt\s+(?:batao|dikhao)",
    # Urdu
    r"(?:پچھلی|سابقہ|اوپر کی)\s*(?:تمام\s*)?ہدایات", r"ہدایات\s*(?:کو\s*)?(?:نظر\s*انداز|بھول)", r"سسٹم\s*پرامپٹ",
]
_RX = [re.compile(p, re.IGNORECASE | re.MULTILINE) for p in _PATTERNS]


def looks_like_injection(text: str) -> bool:
    folded = unicodedata.normalize("NFKC", text)
    return any(rx.search(folded) for rx in _RX)


def fence(text: ScrubbedText) -> ScrubbedText:
    """Wraps the complaint in a fixed delimiter. Angle brackets are replaced so the text can never close the fence."""
    safe = unicodedata.normalize("NFKC", text).replace("<", "‹").replace(">", "›")
    return ScrubbedText(f"{OPEN}\n{safe}\n{CLOSE}")
