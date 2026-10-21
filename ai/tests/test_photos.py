import io
import json

import pytest
from fastapi.testclient import TestClient
from PIL import Image

from app.config import Settings
from app.main import create_app
from app.photos import ALLOWED_LABELS, PhotoError, check_description, prepare_image
from app.providers import Providers
from app.providers.fake import FakeProvider
from tests.conftest import TOKEN

AUTH = {"Authorization": f"Bearer {TOKEN}"}


def jpeg(w=640, h=480, color=(120, 120, 120), exif=False, fmt="JPEG") -> bytes:
    img = Image.new("RGB", (w, h), color)
    buf = io.BytesIO()
    kwargs = {}
    if exif:
        e = Image.Exif()
        e[0x010F] = "SecretPhoneCo"          # Make
        e[0x8825] = {1: "N", 2: (24.0, 51.0, 38.52), 3: "E", 4: (67.0, 0.0, 4.0)}  # GPS
        kwargs["exif"] = e
    img.save(buf, format=fmt, **kwargs)
    return buf.getvalue()


def reply(labels=("pothole",), description="A possible pothole is visible in the road surface.", confidence=0.8, **extra) -> str:
    return json.dumps({"labels": list(labels), "description": description, "confidence": confidence, **extra})


@pytest.fixture
def fake():
    return FakeProvider()


@pytest.fixture
def client(fake):
    app = create_app(Settings(service_token=TOKEN), Providers(llm=fake, vision=fake, speech=fake, embedding=fake))
    return TestClient(app)


def post(client, data: bytes, name="photo.jpg"):
    return client.post("/v1/photos/interpret", files={"file": (name, data, "image/jpeg")}, headers=AUTH)


def test_valid_interpretation_uses_only_allowed_labels(client, fake):
    fake.vision_replies.append(reply(("pothole", "road_damage")))
    r = post(client, jpeg())
    assert r.status_code == 200
    body = r.json()
    assert set(body["labels"]) <= set(ALLOWED_LABELS)
    assert body["needs_more_info"] is False and body["degraded"] is False
    assert body["prompt_version"] == "v1"


def test_label_list_is_given_to_the_model_in_the_prompt(client, fake):
    fake.vision_replies.append(reply())
    post(client, jpeg())
    assert all(label in fake.sent_text[0] for label in ALLOWED_LABELS)


@pytest.mark.parametrize("bad", [
    reply(("sinkhole",)),                                   # label outside the list
    reply(("pothole", "dangerous")),
    reply(("no_visible_issue", "pothole")),                 # contradictory
    reply(labels=[]),                                       # empty
    reply(extra_key=1),                                     # extra field
    "I think this is a pothole",                            # not JSON
    reply(confidence=3),
])
def test_invalid_output_is_rejected_then_falls_back_safely(client, fake, bad):
    fake.vision_replies += [bad] * 3
    r = post(client, jpeg())
    assert r.status_code == 200
    body = r.json()
    assert body["degraded"] is True and body["needs_more_info"] is True
    assert body["labels"] == ["unclear_image"]
    assert len(fake.images_seen) == 3  # retried the configured number of times


HIDDEN_FAULT_CLAIMS = [
    "A possible pothole, probably caused by a burst underground pipe.",
    "Water is visible; the leak is coming from inside the wall.",
    "This will definitely collapse soon.",
    "The pole appears fine but there is certainly corrosion inside.",
    "Possible crack; structural failure of the foundation is likely.",
    "The road looks damaged due to heavy trucks.",
    "There is a hidden leak beneath the tiles, it seems.",
    "A possible gas leak is smelling near the pipe.",
    "Cable appears exposed; faulty wiring inside the conduit is the root cause.",
    "Pressure drop in the main line appears to be the problem.",
    "The wall may be about to fall.",
    "Underlying damage to the drainage system is visible.",
]


@pytest.mark.parametrize("claim", HIDDEN_FAULT_CLAIMS)
def test_hidden_fault_claims_never_get_through(client, fake, claim):
    fake.vision_replies += [reply(description=claim)] * 3
    body = post(client, jpeg()).json()
    assert body["degraded"] is True
    assert claim not in json.dumps(body)


def test_a_claim_on_the_first_try_is_retried_and_a_clean_answer_is_accepted(client, fake):
    fake.vision_replies += [reply(description="Probably caused by an underground leak."), reply()]
    body = post(client, jpeg()).json()
    assert body["degraded"] is False and "pothole" in body["labels"]
    assert any("rejected" in t for t in fake.sent_text)  # the model was told why


@pytest.mark.parametrize("ok", [
    "A possible pothole is visible in the road surface.",
    "Water appears to be pooling on the road near the drain cover.",
    "Rubbish looks piled up beside the wall.",
    "The street light on the pole does not appear to be lit.",
    "It cannot be determined from the photo what is causing the water.",
])
def test_cautious_visible_descriptions_pass(ok):
    assert check_description(ok, ["water_leak"]) is None


def test_confident_statements_without_hedging_are_refused_for_issue_labels():
    assert check_description("There is a pothole.", ["pothole"]) is not None
    assert check_description("The photo is too dark.", ["unclear_image"]) is None


def test_low_confidence_unclear_and_tiny_photos_ask_for_more_information(client, fake):
    fake.vision_replies.append(reply(confidence=0.3))
    assert post(client, jpeg()).json()["needs_more_info"] is True
    fake.vision_replies.append(reply(("unclear_image",), "The photo is too dark to see a problem.", 0.9))
    assert post(client, jpeg()).json()["needs_more_info"] is True
    fake.vision_replies.append(reply())
    assert post(client, jpeg(120, 90)).json()["needs_more_info"] is True


def test_exif_and_gps_never_reach_the_provider(client, fake):
    fake.vision_replies.append(reply())
    original = jpeg(exif=True)
    assert Image.open(io.BytesIO(original)).getexif()  # sanity: the input has metadata
    post(client, original)
    sent = Image.open(io.BytesIO(fake.images_seen[0]))
    assert not sent.getexif() and b"SecretPhoneCo" not in fake.images_seen[0]


def test_large_photos_are_downscaled_and_orientation_is_applied(client, fake):
    fake.vision_replies.append(reply())
    post(client, jpeg(3000, 2000))
    sent = Image.open(io.BytesIO(fake.images_seen[0]))
    assert max(sent.size) <= 1280


@pytest.mark.parametrize("data,status,code", [
    pytest.param(b"", 400, "empty_file", id="empty"),
    pytest.param(b"%PDF-1.4 not an image", 415, "unsupported_file_type", id="pdf"),
    pytest.param(b"\xff\xd8\xff\xe0garbage-after-jpeg-magic", 400, "corrupt_image", id="corrupt"),
    # explicit id: pytest would otherwise build an 11 MB escaped string for the bytes value\n    pytest.param(b"\x00" * (11 * 1024 * 1024), 413, "file_too_large", id="too-large"),
])
def test_bad_files_are_rejected_before_any_provider_call(client, fake, data, status, code):
    r = post(client, data)
    assert r.status_code == status and r.json()["error"] == code
    assert fake.images_seen == []


def test_png_and_webp_are_accepted(client, fake):
    for fmt in ("PNG", "WEBP"):
        fake.vision_replies.append(reply())
        assert post(client, jpeg(fmt=fmt)).status_code == 200


def test_photo_endpoint_requires_the_service_token(client):
    r = client.post("/v1/photos/interpret", files={"file": ("p.jpg", jpeg(), "image/jpeg")})
    assert r.status_code == 401


def test_prepare_image_reports_dimensions():
    p = prepare_image(jpeg(800, 600))
    assert (p.width, p.height) == (800, 600)
    with pytest.raises(PhotoError):
        prepare_image(b"")


def test_provider_failure_is_a_502(fake):
    from app.providers.base import ProviderError

    class Down(FakeProvider):
        async def describe(self, **_):
            raise ProviderError("quota")

    d = Down()
    c = TestClient(create_app(Settings(service_token=TOKEN), Providers(llm=d, vision=d, speech=d, embedding=d)))
    r = post(c, jpeg())
    assert r.status_code == 502 and r.json()["error"] == "provider_unavailable"
