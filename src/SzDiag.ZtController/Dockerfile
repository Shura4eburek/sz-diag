# Контроллер сервисной ZT-сети (T056). Контекст сборки — корень репозитория:
#   railway up --service szdiag-zt-controller
# Путь к этому файлу Railway берёт из переменной сервиса RAILWAY_DOCKERFILE_PATH.
# Переменные сервиса: ZT_PROXY_TOKEN (обязательно, ≥32 символов), ZT_IDENTITY_SECRET
# (восстановление identity при пустом volume). Volume — /var/lib/zerotier-one.
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY src/SzDiag.ZtController/SzDiag.ZtController.csproj src/SzDiag.ZtController/
RUN dotnet restore src/SzDiag.ZtController/SzDiag.ZtController.csproj -r linux-x64
COPY src/SzDiag.ZtController/ src/SzDiag.ZtController/
RUN dotnet publish src/SzDiag.ZtController/SzDiag.ZtController.csproj -c Release -r linux-x64 \
    --self-contained true -p:PublishSingleFile=true -o /app --no-restore

# Образ zerotier-one (Debian 12) — с контроллером внутри, проверено пробой 2026-11-24.
FROM zerotier/zerotier:1.14.2
COPY --from=build /app/SzDiag.ZtController /app/SzDiag.ZtController
COPY src/SzDiag.ZtController/entrypoint.sh /app/entrypoint.sh
RUN sed -i 's/\r$//' /app/entrypoint.sh && chmod +x /app/entrypoint.sh /app/SzDiag.ZtController
ENTRYPOINT ["/bin/sh", "/app/entrypoint.sh"]
