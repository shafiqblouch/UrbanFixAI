"""Language detection for Urdu script, Roman Urdu and English (AI-07).

Deterministic and offline: script counts decide Urdu vs Latin, and a Roman Urdu marker list separates Roman Urdu
from English. Cheap enough to run on every complaint and used to cross-check the language the LLM reports.
"""

from __future__ import annotations

import re
import unicodedata
from dataclasses import dataclass
from typing import Literal

Language = Literal["en", "ur", "roman_ur", "mixed"]

# Everyday Roman Urdu words that are not English words. Short ambiguous ones ("is", "us", "to", "me", "par") are left
# out on purpose, so English text is not misread.
_ROMAN_URDU = frozenset(["hai", "hain", "tha", "thi", "thay", "ho", "hota", "hoti", "hoti", "raha", "rahi", "rahe", "nahi", "nahin", "nahi", "na", "ka", "ke", "ki", "ko", "se", "mein", "main", "mera", "meri", "mere", "hamara", "hamari", "hamare", "aap", "aapka", "tum", "yeh", "ye", "woh", "wo", "kya", "kyun", "kaise", "kab", "kahan", "kon", "kaun", "bohat", "bahut", "bhi", "hi", "ab", "abhi", "kal", "aaj", "subah", "raat", "din", "hafte", "mahine", "pani", "bijli", "gali", "sarak", "kachra", "gutter", "naali", "naaliyan", "badbu", "shor", "chori", "kharab", "band", "chal", "chalti", "chalta", "chalte", "aa", "aata", "aati", "aate", "ja", "jata", "jati", "jate", "kar", "karen", "karein", "karna", "kiya", "kijiye", "kijiyega", "dein", "den", "dijiye", "bhej", "bhejein", "meherbani", "mehrbani", "shukriya", "jaldi", "foran", "fauran", "masla", "masail", "ghar", "makan", "mohalla", "ilaqa", "wapas", "phir", "lekin", "aur", "ya", "agar", "jab", "tak", "liye", "lie", "wala", "wali", "wale", "sirf", "bilkul", "zaroor", "shayad", "kuch", "koi", "sab", "sara", "saari", "log", "bachay", "bachon", "bacha"])

_WORD = re.compile(r"[A-Za-z']+")
_ARABIC = re.compile(r"[؀-ۿݐ-ݿﭐ-﷿ﹰ-﻿]")
_LATIN = re.compile(r"[A-Za-z]")


@dataclass(frozen=True)
class Detection:
    language: Language
    confidence: float


def _script(token: str) -> str | None:
    """'ar' / 'la' by the token's letters (digits and symbols do not count), or None if it has no letters."""
    arabic = sum(1 for c in token if c.isalpha() and _ARABIC.match(c))
    latin = sum(1 for c in token if c.isalpha() and _LATIN.match(c))
    if arabic == latin == 0:
        return None
    return "ar" if arabic >= latin else "la"


def detect_language(text: str) -> Detection:
    text = unicodedata.normalize("NFKC", text)
    scripts = [s for s in (_script(t) for t in text.split()) if s]
    if not scripts:
        return Detection("en", 0.0)

    # Word-level shares, so one long English word inside an Urdu sentence does not flip the result.
    arabic_share = scripts.count("ar") / len(scripts)
    if arabic_share >= 0.7:
        return Detection("ur", min(1.0, 0.7 + arabic_share * 0.3))
    if arabic_share >= 0.2:
        return Detection("mixed", 0.8)

    words = [w.lower() for w in _WORD.findall(text)]
    if not words:
        return Detection("en", 0.3)
    hits = sum(w in _ROMAN_URDU for w in words)
    share = hits / len(words)
    if hits >= 2 and share >= 0.25:
        return Detection("roman_ur", min(1.0, 0.6 + share))
    if hits == 1 and len(words) <= 4:
        return Detection("roman_ur", 0.55)
    if hits >= 1 and share >= 0.12:
        return Detection("mixed" if share < 0.2 else "roman_ur", 0.6)
    return Detection("en", min(1.0, 0.7 + (1 - share) * 0.3))