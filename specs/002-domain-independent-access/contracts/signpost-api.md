# Contract: сервис указателя `szdiag-signpost`

База: `https://<сервис>.up.railway.app` — **свой** Railway оператора; адрес задаётся при сборке
(`build-dist -SignpostUrl …`) и в репозиторий не пишется. Env сервиса: `SZDIAG_SIGN_PUBKEY`
(SPKI PEM), `SZDIAG_FLEET_BUNDLE` (шифротекст, сервисом **не читается**), `DATA_DIR=/data`
(volume), `RAILWAY_DOCKERFILE_PATH=src/SzDiag.Signpost/Dockerfile`. Авторизации на запись нет —
её заменяет подпись.

| Метод | Путь | Ответ |
|---|---|---|
| `GET` | `/signpost` | `200 { hub?: SignedEnvelope, release?: SignedEnvelope, lease?: { holderHubId, hubName, expiresAt } }` (null-поля опускаются) |
| `PUT` | `/signpost/hub` | тело `SignedEnvelope(HubRecord)` → `200 lease` / `400` подпись или `kind` / `409 lease держателя` / `412` `seq` не растёт |
| `PUT` | `/signpost/release` | тело `SignedEnvelope(ReleaseRecord)` → `200` / `400` (в т.ч. пакет с этим sha256 не загружен) / `412` |
| `PUT` | `/package/{sha256}` | тело — `package.zip` (≤ 100 МБ) + заголовок `X-SzDiag-Release: base64(конверт релиза на этот sha256)` → `201` / `401` нет или чужой релиз / `400` sha256 тела ≠ путь; хранятся 3 последних, пакет текущего релиза — всегда |
| `GET` | `/package/{sha256}` | `200 application/zip` / `404` |
| `GET` | `/healthz` | `200 ok` |

Без `SZDIAG_SIGN_PUBKEY` все `PUT` → `503`.

Правила:
- Подпись проверяется до любых изменений; неизвестный `kid` → `400`.
- Ответ `GET /signpost` клиент **перепроверяет сам** — сервису не доверяет.
- В указатель попадают только адреса, которые hub находит сам (LAN, позже ZeroTier). Домены — никогда.

Зеркала нет (решение владельца 2026-11-24: на GitHub ничего не уходит).

# Contract: резолвер (`SzDiag.Contracts.HubLocator`)

```
Task<HubLocation?> LocateAsync(ct)
  источники по порядку: config HubUrl (если /healthz отвечает) → LAN discovery → signpost
  HubLocation { Url, Mode (config|lan-broadcast|lan|tunnel|zt), Source, Rejected }
Task<ReleaseLocation?> LocateReleaseAsync(ct)   // апдейтер, когда hub не найден/не отвечает
IReadOnlyList<SourceRejection> LastRejected     // причины отказа — и при неудаче тоже
```
Каждый отвергнутый источник — в `LastRejected` с причиной (не отвечает / таймаут / подпись /
чужой kid / seq) и в лог клиента.

# Contract: CLI / скрипты

| Команда | Что делает | Коды |
|---|---|---|
| `tools\fleet.ps1 init` | генерирует ключи флота, шифрует паролем, кладёт в Railway-переменную; отказ, если уже есть (без `-Force`) | 0 / 2 есть / 3 нет railway-логина |
| `tools\fleet.ps1 pull` | расшифровывает бандл в `secrets\` | 0 / 3 / 4 неверный пароль / 5 бандл испорчен |
| `build-dist.ps1 [-SignpostUrl …]` | без `secrets\fleet.json` зовёт `fleet.ps1 pull`; адрес указателя переносит из прежнего конфига | 6 нет секретов флота / 7 приватный ключ в клиентском dist |
| `build-dist.ps1 -Publish` | `szcli fleet sign-release` → пакет и релиз в свой указатель | 8 не опубликовано |
| `start-hub.cmd --takeover` | перехватить живую аренду | |
| `szcli fleet keygen\|encrypt\|decrypt\|sign-release` | см. `szcli fleet` | 0 / 1 / 2 / 4 / 5 |
