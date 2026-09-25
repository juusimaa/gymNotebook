# Built with the repository root as the context (docker-compose.yml and
# .github/workflows/build-and-push.yml both pass `context: .` with this file), so the
# privacy notice artifacts in docs/privacy/notices/ can be embedded into the API
# assembly (specs/001 T031). What the context actually sends is an allow-list in
# backend/Dockerfile.dockerignore, not the whole repository.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /app

COPY backend/GymNotebook.Api/GymNotebook.Api.csproj backend/GymNotebook.Api/GymNotebook.Api.csproj

RUN dotnet restore backend/GymNotebook.Api/GymNotebook.Api.csproj

COPY backend/GymNotebook.Api/ backend/GymNotebook.Api/
# Same relative position as in the repository, so the csproj's
# ../../docs/privacy/notices path resolves identically here and in a local build.
COPY docs/privacy/notices/ docs/privacy/notices/

RUN dotnet publish backend/GymNotebook.Api/GymNotebook.Api.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .

EXPOSE 8080

COPY backend/entrypoint.sh /entrypoint.sh
RUN chmod +x /entrypoint.sh

ENTRYPOINT ["/entrypoint.sh"]
