# Контекст сборки — корень репозитория (нужен SzDiag.Contracts):
#   railway up --service szdiag-signpost
# Путь к этому файлу Railway берёт из переменной сервиса RAILWAY_DOCKERFILE_PATH
# (корневой railway.json с builder: DOCKERFILE он проигнорировал).
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY src/SzDiag.Contracts/SzDiag.Contracts.csproj src/SzDiag.Contracts/
COPY src/SzDiag.Signpost/SzDiag.Signpost.csproj src/SzDiag.Signpost/
RUN dotnet restore src/SzDiag.Signpost/SzDiag.Signpost.csproj
COPY src/SzDiag.Contracts/ src/SzDiag.Contracts/
COPY src/SzDiag.Signpost/ src/SzDiag.Signpost/
RUN dotnet publish src/SzDiag.Signpost/SzDiag.Signpost.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /app .
ENV DATA_DIR=/data
ENTRYPOINT ["dotnet", "SzDiag.Signpost.dll"]
