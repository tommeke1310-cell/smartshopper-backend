# ── Stage 1: build ──────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

COPY *.csproj ./
RUN dotnet restore

COPY . .
RUN dotnet publish -c Release -o /app/publish --no-restore

# ── Stage 2: runtime met Playwright ──────────────────────────────────
FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS runtime
WORKDIR /app

# Node.js nodig voor Playwright browser install script
RUN apt-get update && apt-get install -y --no-install-recommends \
    curl ca-certificates \
    && curl -fsSL https://deb.nodesource.com/setup_20.x | bash - \
    && apt-get install -y nodejs \
    && rm -rf /var/lib/apt/lists/*

COPY --from=build /app/publish .

# Installeer Playwright + Chromium dependencies via npx
RUN npx --yes playwright@1.44.0 install --with-deps chromium

ENV PLAYWRIGHT_BROWSERS_PATH=/root/.cache/ms-playwright
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

ENTRYPOINT ["dotnet", "SmartShopper.API.dll"]
