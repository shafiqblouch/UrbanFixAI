"""Tracing and error reporting (FD-09). Everything is off unless configured, so tests and laptops stay quiet."""

from __future__ import annotations

import logging
import os

from fastapi import FastAPI

log = logging.getLogger("urbanfix.ai")


def setup_telemetry(app: FastAPI, env: dict[str, str] | None = None) -> None:
    e = os.environ if env is None else env

    if endpoint := e.get("OTEL_EXPORTER_OTLP_ENDPOINT"):
        from opentelemetry import trace
        from opentelemetry.exporter.otlp.proto.http.trace_exporter import OTLPSpanExporter
        from opentelemetry.instrumentation.fastapi import FastAPIInstrumentor
        from opentelemetry.sdk.resources import Resource
        from opentelemetry.sdk.trace import TracerProvider
        from opentelemetry.sdk.trace.export import BatchSpanProcessor

        provider = TracerProvider(resource=Resource.create({"service.name": "urbanfix-ai"}))
        provider.add_span_processor(BatchSpanProcessor(OTLPSpanExporter(endpoint=endpoint.rstrip("/") + "/v1/traces")))
        trace.set_tracer_provider(provider)
        # Continues the trace CORE started: the W3C traceparent header is read by the instrumentation.
        FastAPIInstrumentor.instrument_app(app, tracer_provider=provider, excluded_urls="health")
        log.info("tracing enabled -> %s", endpoint)

    if dsn := e.get("SENTRY_DSN"):
        import sentry_sdk

        sentry_sdk.init(dsn=dsn, environment=e.get("ENVIRONMENT", "dev"), send_default_pii=False)
