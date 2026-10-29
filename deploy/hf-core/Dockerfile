FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
ARG BRANCH=wave-3-to-5-complete
RUN git clone --depth 1 --branch ${BRANCH} https://github.com/shafiqblouch/UrbanFixAI.git /repo
WORKDIR /repo/core
# Debug build: SixLabors.ImageSharp 4 needs a license key for Release builds (allowed for testing without one).
RUN dotnet publish src/UrbanFix.Api/UrbanFix.Api.csproj -c Debug -o /app -p:SixLaborsLicenseKey=

FROM mcr.microsoft.com/dotnet/aspnet:9.0
RUN apt-get update && apt-get install -y --no-install-recommends libfontconfig1 fonts-dejavu-core \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://0.0.0.0:7860
EXPOSE 7860
CMD ["dotnet", "UrbanFix.Api.dll"]
