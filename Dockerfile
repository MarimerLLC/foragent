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

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

RUN groupadd -r foragent && useradd -r -g foragent --create-home foragent \
    && mkdir -p /data && chown foragent:foragent /data

COPY --from=build /app/publish .
RUN chown -R foragent:foragent /app

USER foragent
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "Foragent.Agent.dll"]
