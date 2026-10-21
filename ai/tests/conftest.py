import pytest
from fastapi.testclient import TestClient

from app.config import Settings
from app.main import create_app
from app.providers import Providers
from app.providers.fake import FakeProvider

TOKEN = "test-service-token"
CATEGORIES = [
    {"key": "water", "label": "Water supply"},
    {"key": "electricity", "label": "Electricity", "description": "power, wires, street lights"},
]


@pytest.fixture
def fake() -> FakeProvider:
    return FakeProvider()


@pytest.fixture
def settings() -> Settings:
    return Settings(service_token=TOKEN)


@pytest.fixture
def client(settings: Settings, fake: FakeProvider) -> TestClient:
    app = create_app(settings, Providers(llm=fake, vision=fake, speech=fake, embedding=fake))
    return TestClient(app)


@pytest.fixture
def auth() -> dict[str, str]:
    return {"Authorization": f"Bearer {TOKEN}"}
