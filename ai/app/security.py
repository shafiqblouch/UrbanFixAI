from __future__ import annotations

import hmac

from fastapi import HTTPException, Request, status

from app.config import Settings


def require_service_token(request: Request) -> None:
    """Service-to-service auth. Fails closed: with no token configured, every call is rejected."""
    settings: Settings = request.app.state.settings
    header = request.headers.get("authorization", "")
    scheme, _, presented = header.partition(" ")
    valid = [t for t in (settings.service_token, settings.service_token_previous) if t]

    ok = scheme.lower() == "bearer" and bool(presented) and any(
        hmac.compare_digest(presented.encode(), token.encode()) for token in valid
    )
    if not ok:
        raise HTTPException(status.HTTP_401_UNAUTHORIZED, "invalid service token", headers={"WWW-Authenticate": "Bearer"})
