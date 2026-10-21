FROM alpine:3 AS certs

FROM pgvector/pgvector:pg16

# Plain-HTTP apt is blocked on some networks, so use HTTPS. The base image ships no CA bundle, so borrow
# one from alpine to bootstrap; TLS verification stays on.
COPY --from=certs /etc/ssl/certs/ca-certificates.crt /etc/ssl/certs/ca-certificates.crt
RUN sed -i 's#http://#https://#g' /etc/apt/sources.list.d/* \
    && apt-get -o Acquire::https::CaInfo=/etc/ssl/certs/ca-certificates.crt update \
    && apt-get -o Acquire::https::CaInfo=/etc/ssl/certs/ca-certificates.crt install -y --no-install-recommends \
        ca-certificates postgresql-16-postgis-3 postgresql-16-postgis-3-scripts \
    && rm -rf /var/lib/apt/lists/*
