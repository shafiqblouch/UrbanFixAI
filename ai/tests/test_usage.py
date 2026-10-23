"""Every AI response says what it used, summed over every attempt, so CORE can meter and cap cost (AI-13)."""

import io
import json

from PIL import Image

from app.providers.base import Usage
from tests.conftest import CATEGORIES

BODY = {"text": "No water since morning in block B", "categories": CATEGORIES}


def good():
    return json.dumps({"category": "water", "summary": "No water.", "location_text": None, "language": "en", "confidence": 0.9})


def jpeg() -> bytes:
    buf = io.BytesIO()
    Image.new("RGB", (640, 480), (120, 120, 120)).save(buf, format="JPEG")
    return buf.getvalue()


def test_understanding_reports_tokens(client, auth, fake):
    fake.llm_replies.append(good())
    usage = client.post("/v1/complaints/understand", json=BODY, headers=auth).json()["usage"]
    assert usage["output_tokens"] == 10 and usage["input_tokens"] > 0


def test_retries_are_counted_because_they_cost_money_too(client, auth, fake):
    fake.llm_replies += ["not json", "still not json", good()]
    usage = client.post("/v1/complaints/understand", json=BODY, headers=auth).json()["usage"]
    assert usage["output_tokens"] == 30


def test_photo_and_comparison_report_tokens(client, auth, fake):
    fake.vision_usage = Usage(1000, 50)
    fake.vision_replies.append(json.dumps({"labels": ["pothole"], "description": "A possible pothole is visible.", "confidence": 0.8}))
    photo = client.post("/v1/photos/interpret", files={"file": ("p.jpg", jpeg(), "image/jpeg")}, headers=auth).json()
    assert photo["usage"] == {"input_tokens": 1000, "output_tokens": 50}

    files = {"before": ("b.jpg", jpeg(), "image/jpeg"), "after": ("a.jpg", jpeg(), "image/jpeg")}
    comparison = client.post("/v1/photos/compare", files=files, headers=auth).json()
    assert comparison["usage"] == {"input_tokens": 1000, "output_tokens": 50}


def test_a_failed_photo_attempt_still_reports_what_it_used(client, auth, fake):
    fake.vision_usage = Usage(100, 5)
    fake.vision_replies += ["bad"] * 5
    body = client.post("/v1/photos/interpret", files={"file": ("p.jpg", jpeg(), "image/jpeg")}, headers=auth).json()
    assert body["degraded"] is True and body["usage"] == {"input_tokens": 300, "output_tokens": 15}


def test_embeddings_estimate_input_tokens(client, auth):
    usage = client.post("/v1/embeddings", json={"texts": ["x" * 400]}, headers=auth).json()["usage"]
    assert usage["input_tokens"] == 101 and usage["output_tokens"] == 0
