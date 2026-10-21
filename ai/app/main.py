from __future__ import annotations

from fastapi import APIRouter, Depends, FastAPI, Request
from fastapi.responses import JSONResponse

from app.config import Settings
from app.providers import Providers, build_providers
from app.providers.base import ProviderError
from app.schemas import ErrorBody, ProviderStatus, UnderstandRequest, UnderstandResponse
from app.security import require_service_token
from app.telemetry import setup_telemetry
from app.understanding import InvalidModelOutput, understand

API_VERSION = "v1"


def create_app(settings: Settings | None = None, providers: Providers | None = None) -> FastAPI:
    settings = settings or Settings.from_env()
    app = FastAPI(title="UrbanFix AI", version="0.1.0", docs_url="/docs", openapi_url="/openapi.json")
    app.state.settings = settings
    app.state.providers = providers or build_providers(settings)

    # Unauthenticated: liveness only. Nothing here touches a provider.
    @app.get("/health")
    def health() -> dict[str, str]:
        return {"status": "ok", "service": "ai"}

    v1 = APIRouter(prefix=f"/{API_VERSION}", dependencies=[Depends(require_service_token)])

    @v1.get("/providers", response_model=ProviderStatus)
    def provider_status(request: Request) -> ProviderStatus:
        s: Settings = request.app.state.settings
        return ProviderStatus(
            llm=f"{s.llm_provider}:{s.llm_model}",
            vision=f"{s.vision_provider}:{s.vision_model}",
            speech=f"{s.speech_provider}:{s.speech_model}",
            embedding=f"{s.embedding_provider}:{s.embedding_model}",
        )

    @v1.post(
        "/complaints/understand",
        response_model=UnderstandResponse,
        responses={502: {"model": ErrorBody}},
    )
    async def understand_complaint(body: UnderstandRequest, request: Request):
        try:
            return await understand(body, request.app.state.providers, request.app.state.settings)
        except InvalidModelOutput as exc:
            return JSONResponse(ErrorBody(error="invalid_model_output", detail=str(exc)).model_dump(), status_code=502)
        except ProviderError as exc:
            return JSONResponse(ErrorBody(error="provider_unavailable", detail=str(exc)).model_dump(), status_code=502)

    app.include_router(v1)
    setup_telemetry(app)
    return app


app = create_app()
