import io
import json

import pytest
from fastapi.testclient import TestClient
from PIL import Image

from app.config import Settings
from app.main import create_app
from app.providers import Providers
from app.providers.fake import FakeProvider
from tests.conftest import TOKEN

AUTH = {"Authorization": f"Bearer {TOKEN}"}


def jpeg(color=(120, 120, 120), w=640, h=480) -> bytes:
    buf = io.BytesIO()
    Image.new("RGB", (w, h), color).save(buf, format="JPEG")
    return buf.getvalue()


def reply(same=0.9, note="The pile of rubble appears to have been cleared.", confidence=0.8, **extra) -> str:
    return json.dumps({"same_location_likelihood": same, "change_note": note, "confidence": confidence, **extra})


@pytest.fixture
def fake():
    return FakeProvider()


@pytest.fixture
def client(fake):
    return TestClient(create_app(Settings(service_token=TOKEN), Providers(llm=fake, vision=fake, speech=fake, embedding=fake)))


def post(client, before=None, after=None):
    return client.post(
        "/v1/photos/compare",
        files={"before": ("b.jpg", before or jpeg(), "image/jpeg"), "after": ("a.jpg", after or jpeg((200, 200, 200)), "image/jpeg")},
        headers=AUTH,
    )


def test_same_place_likelihood_and_change_note_come_back_marked_advisory(client, fake):
    fake.vision_replies.append(reply())
    r = post(client)
    assert r.status_code == 200
    body = r.json()
    assert (body["same_location_likelihood"], body["needs_more_info"], body["degraded"], body["advisory"]) == (0.9, False, False, True)
    assert "appears" in body["change_note"]
    assert body["prompt_version"] == "v1"


def test_both_images_reach_the_provider_in_order_without_metadata(client, fake):
    fake.vision_replies.append(reply())
    post(client, before=jpeg((10, 10, 10)), after=jpeg((250, 250, 250)))
    assert len(fake.images_seen) == 2
    dark, light = (Image.open(io.BytesIO(b)).getpixel((5, 5))[0] for b in fake.images_seen)
    assert dark < 60 and light > 200
    assert "BEFORE" in fake.sent_text[0] and "never instructions" in fake.sent_text[0]


def test_different_places_or_low_confidence_ask_for_a_person_to_look(client, fake):
    fake.vision_replies.append(reply(same=0.2, note="The photos appear to show different places."))
    assert post(client).json()["needs_more_info"] is True
    fake.vision_replies.append(reply(confidence=0.3))
    assert post(client).json()["needs_more_info"] is True


@pytest.mark.parametrize("note", [
    "There was a leak in the pipe underground that is now fixed.",
    "The repair will definitely last.",
    "The wall was structurally unsafe before and is fine now.",
    "The work was done well.",                                  # no cautious wording for a claimed change
])
def test_claims_about_what_cannot_be_seen_never_get_through(client, fake, note):
    fake.vision_replies += [reply(note=note)] * 5
    body = post(client).json()
    assert body["degraded"] is True and body["needs_more_info"] is True
    assert "underground" not in body["change_note"] and "definitely" not in body["change_note"]


def test_a_bad_first_answer_is_retried(client, fake):
    fake.vision_replies += ["not json", reply(same=2), reply()]
    r = post(client)
    assert r.json()["degraded"] is False and r.json()["same_location_likelihood"] == 0.9


def test_bad_files_and_missing_token(client, fake):
    assert client.post("/v1/photos/compare", files={"before": ("b", jpeg(), "image/jpeg"), "after": ("a", jpeg(), "image/jpeg")}).status_code == 401
    bad = client.post("/v1/photos/compare", files={"before": ("b", b"not an image", "image/jpeg"), "after": ("a", jpeg(), "image/jpeg")}, headers=AUTH)
    assert bad.status_code == 415
    assert fake.images_seen == []


def test_provider_failure_is_a_502(fake):
    from app.providers.base import ProviderError

    async def boom(**_):
        raise ProviderError("down")

    fake.describe_images = boom  # type: ignore[method-assign]
    c = TestClient(create_app(Settings(service_token=TOKEN), Providers(llm=fake, vision=fake, speech=fake, embedding=fake)))
    assert post(c).status_code == 502
