FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /app

COPY GymNotebook.Api/GymNotebook.Api.csproj GymNotebook.Api/GymNotebook.Api.csproj

RUN dotnet restore GymNotebook.Api/GymNotebook.Api.csproj

COPY GymNotebook.Api/ GymNotebook.Api/

RUN dotnet publish GymNotebook.Api/GymNotebook.Api.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .

EXPOSE 8080

ENTRYPOINT ["dotnet", "GymNotebook.Api.dll"]