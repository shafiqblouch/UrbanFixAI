"""PII scrubber (AI-04). Runs before any text leaves the service for a provider.

Order matters: structured identifiers (CNIC, emails, URLs, phones) are matched before names so a digit run is
never half-consumed. Matching is deliberately aggressive: a false positive costs a little context, a miss leaks
a person's details to a third party.
"""

from __future__ import annotations

import re
import unicodedata
from dataclasses import dataclass
from typing import NewType

# Text that has been through the scrubber. Provider interfaces only accept this type, so passing raw text to a
# provider is a type error rather than a code-review item.
ScrubbedText = NewType("ScrubbedText", str)

_URDU_DIGITS = str.maketrans("۰۱۲۳۴۵۶۷۸۹٠١٢٣٤٥٦٧٨٩", "01234567890123456789")

_SEP = r"[\s\-\.\(\)]*"

_PATTERNS: list[tuple[str, re.Pattern[str]]] = [
    ("EMAIL", re.compile(r"[\w.+\-]+@[\w\-]+(?:\.[\w\-]+)+")),
    ("URL", re.compile(r"(?:https?://|www\.)\S+", re.IGNORECASE)),
    # CNIC: 12345-1234567-1, or 13 digits with optional spaces/dashes
    ("CNIC", re.compile(rf"(?<!\d)\d{{5}}{_SEP}\d{{7}}{_SEP}\d(?!\d)")),
    # Mobile: +92 3xx xxxxxxx / 0092 3xx... / 03xx xxxxxxx, any separators
    ("PHONE", re.compile(rf"(?<!\d)(?:\+{_SEP}92|00{_SEP}92|0){_SEP}3\d{{2}}{_SEP}\d{{7}}(?!\d)")),
    # Landline: 021-1234567, +92-21-1234567 (area code 2-3 digits, 6-8 digit subscriber)
    ("PHONE", re.compile(rf"(?<!\d)(?:\+{_SEP}92{_SEP}|0)[1-9]\d{{1,2}}{_SEP}\d{{6,8}}(?!\d)")),
    # Any other long digit run that looks like a phone number (international, odd grouping)
    ("PHONE", re.compile(r"(?<!\d)\+?\d(?:[\s\-]?\d){9,13}(?!\d)")),
]

# Titles are matched case-insensitively, but the name that follows must be capitalised, so ordinary lowercase
# words ("sir please fix the pump") are never swallowed.
_HONORIFIC = re.compile(
    r"\b(?i:mr|mrs|ms|miss|dr|prof|engr|capt|col|maj|gen|sir|madam|haji|hajji|syed|sayed|sheikh|shaikh|"
    r"malik|chaudhry|chaudhary|ch|rana|mian)\b\.?\s+"
    r"[A-Z][\w'\-]+(?:\s+[A-Z][\w'\-]+){0,3}"
)

# "Muhammad ..." is nearly always the start of a name, even in lowercase Roman Urdu, so the next word is taken too.
_STRONG_NAME = re.compile(r"\b(?i:muhammad|mohammad|mohammed|muhammed)\b(?:\s+[\w'\-]+){1,2}")

# Common first names: scrubbed when followed by a capitalised surname.
_WEAK_NAME = re.compile(
    r"\b(?i:ahmed|ahmad|ali|hassan|hussain|hasan|usman|bilal|fatima|ayesha|aisha|zainab|khadija|maryam)\b"
    r"(?:\s+[A-Z][\w'\-]+){1,3}"
)
_URDU_TITLES = re.compile(r"(?:جناب|محترم|محترمہ|صاحب|سید|حاجی|چوہدری|ملک)\s+[؀-ۿ]+(?:\s+[؀-ۿ]+){0,2}")


@dataclass(frozen=True)
class ScrubResult:
    text: ScrubbedText
    counts: dict[str, int]

    @property
    def redacted_anything(self) -> bool:
        return bool(self.counts)


def _normalise(text: str) -> str:
    """Fold Urdu/Arabic digits and compatibility forms so patterns see plain ASCII digits."""
    return unicodedata.normalize("NFKC", text).translate(_URDU_DIGITS)


def _name_regex(name: str) -> re.Pattern[str] | None:
    parts = [p for p in re.split(r"\s+", name.strip()) if len(p) >= 2]
    if not parts:
        return None
    # Each token may also appear alone ("Mr. Raza" for "Ahmed Raza"), so scrub every token of a known name.
    return re.compile("|".join(rf"(?<!\w){re.escape(p)}(?!\w)" for p in parts), re.IGNORECASE)


def scrub(text: str, known_names: list[str] | None = None) -> ScrubResult:
    counts: dict[str, int] = {}

    def sub(label: str, pattern: re.Pattern[str], value: str) -> str:
        def repl(_: re.Match[str]) -> str:
            counts[label] = counts.get(label, 0) + 1
            return f"[{label}]"

        return pattern.sub(repl, value)

    value = _normalise(text)
    for label, pattern in _PATTERNS:
        value = sub(label, pattern, value)

    for name in known_names or []:
        if (rx := _name_regex(_normalise(name))) is not None:
            value = sub("NAME", rx, value)

    value = sub("NAME", _HONORIFIC, value)
    value = sub("NAME", _STRONG_NAME, value)
    value = sub("NAME", _WEAK_NAME, value)
    value = sub("NAME", _URDU_TITLES, value)

    return ScrubResult(ScrubbedText(value), counts)

