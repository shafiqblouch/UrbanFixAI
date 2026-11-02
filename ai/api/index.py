"""Vercel serverless entrypoint for the AI service.

Vercel's Python runtime serves any ASGI `app` exported from a file under `api/`.
The whole FastAPI app is already built in app.main, so this just re-exports it;
all behaviour (routes, auth, providers) is unchanged from running it under uvicorn.
vercel.json rewrites every path to this function, so /health, /v1/* and /docs all land here.
"""

from __future__ import annotations

from app.main import app  # noqa: F401  (re-exported for the Vercel runtime)
