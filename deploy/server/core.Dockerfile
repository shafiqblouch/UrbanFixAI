# Core API image for the single-server stack. Unlike core/Dockerfile, this builds from the REPO ROOT as context
# so the sibling contracts/ folder (embedded as safety-terms.json by UrbanFix.Complaints) is present.
# Build context in docker-compose.yml is ../.. (the repo root).
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY . .
ARG SIXLABORS_LICENSE_KEY=""
# Debug build is allowed without a Six Labors key (testing); set the key for a real Release build.
RUN cd core \
    && if [ -n "$SIXLABORS_LICENSE_KEY" ]; then CONFIG=Release; else CONFIG=Debug; fi \
    && dotnet publish src/UrbanFix.Api/UrbanFix.Api.csproj -c $CONFIG -o /app "-p:SixLaborsLicenseKey=$SIXLABORS_LICENSE_KEY"

FROM mcr.microsoft.com/dotnet/aspnet:9.0
# QuestPDF (SkiaSharp) needs fontconfig on Linux.
RUN apt-get update && apt-get install -y --no-install-recommends libfontconfig1 fonts-dejavu-core \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://0.0.0.0:8080
CMD ["sh", "-c", "ASPNETCORE_URLS=http://0.0.0.0:${PORT:-8080} exec dotnet UrbanFix.Api.dll"]
