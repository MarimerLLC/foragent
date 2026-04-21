# syntax=docker/dockerfile:1
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Clear Windows-specific NuGet fallback folders so Linux builds don't trip on them
RUN printf '<configuration><fallbackPackageFolders><clear /></fallbackPackageFolders></configuration>' \
    > /src/NuGet.config

COPY Foragent.slnx Directory.Build.props Directory.Packages.props ./
COPY src/ src/

RUN --mount=type=cache,target=/root/.nuget/packages \
    dotnet publish src/Foragent.Agent/Foragent.Agent.csproj \
    -c Release -o /app/publish

# Runtime: aspnet:10.0 provides .NET 10 (the Playwright-branded v1.50.0-noble
# base ships only .NET 8 and can't run this app). We install Chromium + its
# system deps at image build time by invoking Microsoft.Playwright's install
# entrypoint directly with the dotnet CLI — same effect as calling
# `playwright.ps1 install --with-deps chromium` but without needing pwsh.
# Browser files land in /ms-playwright so every user of the image sees them.
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .

ENV PLAYWRIGHT_BROWSERS_PATH=/ms-playwright
# Microsoft.Playwright.dll has no runtimeconfig of its own, so we hand it the
# app's runtime/deps config via `dotnet exec`. Equivalent to the pwsh
# `playwright.ps1 install` entry point but doesn't need PowerShell in the image.
RUN dotnet exec \
      --runtimeconfig Foragent.Agent.runtimeconfig.json \
      --depsfile Foragent.Agent.deps.json \
      Microsoft.Playwright.dll install chromium --with-deps

RUN useradd --create-home --shell /bin/bash foragent \
    && mkdir -p /data && chown -R foragent:foragent /data /app

USER foragent
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "Foragent.Agent.dll"]
