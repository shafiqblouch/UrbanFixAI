FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY . .
# SixLabors.ImageSharp 4 needs a license key for Release builds. Without SIXLABORS_LICENSE_KEY this builds Debug,
# which the license allows for development and testing; set the key (Render env var) for a real Release build.
ARG SIXLABORS_LICENSE_KEY=""
RUN if [ -n "$SIXLABORS_LICENSE_KEY" ]; then CONFIG=Release; else CONFIG=Debug; fi \
    && dotnet publish src/UrbanFix.Api/UrbanFix.Api.csproj -c $CONFIG -o /app "-p:SixLaborsLicenseKey=$SIXLABORS_LICENSE_KEY"

FROM mcr.microsoft.com/dotnet/aspnet:9.0
# QuestPDF (SkiaSharp) needs fontconfig on Linux.
RUN apt-get update && apt-get install -y --no-install-recommends libfontconfig1 fonts-dejavu-core \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app .
# Render injects PORT; Kestrel must listen on it.
ENV ASPNETCORE_URLS=http://0.0.0.0:10000
CMD ["sh", "-c", "ASPNETCORE_URLS=http://0.0.0.0:${PORT:-10000} exec dotnet UrbanFix.Api.dll"]
