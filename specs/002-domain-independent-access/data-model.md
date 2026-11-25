# Data Model: Доступ без своего домена

## SignedEnvelope (Contracts)

| Поле | Тип | Правила |
|---|---|---|
| `payload` | base64 | сырые UTF-8 байты JSON нагрузки; проверяется подпись именно их |
| `sig` | base64 | ECDSA P-256/SHA-256 (IEEE P1363) над байтами `payload` |
| `kid` | string | первые 16 hex SHA-256 от SPKI публичного ключа; неизвестный `kid` → отказ |

## HubRecord (нагрузка `kind = "hub"`)

| Поле | Тип | Правила |
|---|---|---|
| `kind` | `"hub"` | |
| `hubId` | string | стабильный id машины-hub (`Environment.MachineName` + соль из `secrets\hub-id`) |
| `hubName` | string | для людей: «ПК», «ноут» |
| `endpoints` | `[{mode, url}]` | `mode`: `zt` / `lan` / `tunnel`; порядок = предпочтение |
| `ztNetwork` | string | 16 hex, id сервисной сети |
| `seq` | long | unix-мс издателя, строго растёт |
| `issuedAt` | DateTimeOffset | справочно; аренду считает сервис по своим часам |
| `takeover` | bool | разрешить перехват живой аренды |

## ReleaseRecord (нагрузка `kind = "release"`)

| Поле | Тип | Правила |
|---|---|---|
| `kind` | `"release"` | |
| `version` | string | как `version.txt` (git sha / `-dirty`) |
| `buildTime` | DateTimeOffset | порядок релизов; клиент не ставит старее установленного |
| `packageSha256` | hex | sha256 `package.zip` |
| `packageSize` | long | |
| `packageUrls` | string[] | относительные (`/package/{sha}`) — от своего указателя; hub в LAN не нужен в списке |
| `seq` | long | как у HubRecord |

## Lease (сервис указателя, в памяти)

`holderHubId`, `expiresAt = receivedAt + 90 с`, `lastEnvelope`. Переходы:
- пусто / истекла → любой валидный `PUT hub` занимает;
- живая, тот же `hubId` → продление (`seq` больше прежнего);
- живая, другой `hubId`, `takeover=false` → `409`; `takeover=true` → смена держателя.

## FleetSecrets (бандл, шифруется паролем флота)

| Поле | Содержимое |
|---|---|
| `formatVersion` | 1 |
| `signPrivateKey` | PKCS#8 PEM ECDSA P-256 |
| `signPublicKey` | SPKI PEM (он же зашивается в апдейтер/агент и в env сервиса) |
| `agentToken` | 32 байта base64url |
| `managementToken` | 32 байта base64url |
| `svcDiagKey` / `svcDiagKeyPub` | OpenSSH ed25519 |
| `ztControllerUrl` | HTTPS-адрес прокси своего контроллера (Railway) |
| `ztControllerToken` | Bearer прокси контроллера (он же `ZT_PROXY_TOKEN` сервиса) |
| `ztIdentitySecret` | бэкап `identity.secret` контроллера (= префикс ID сети) |
| `ztNetwork` | id сервисной сети |
| `createdAt` | |

Обёртка в переменной `SZDIAG_FLEET_BUNDLE`: `{ v:1, kdf:"pbkdf2-sha256", iter:600000, salt, nonce, ct, tag }` (base64).
Раскладывается в `secrets\` как: `fleet-sign.key`, `fleet-sign.pub`, `svc_diag_key(.pub)`, `fleet.json`
(токены, `ztApiToken`, `ztNetwork`). `secrets\` уже в `.gitignore`.

## RevertState (агент) — новые поля

| Поле | Назначение |
|---|---|
| `JoinedZt` (bool) | шаг входа в сеть применён |
| `ZtStoragePath` | `C:\ProgramData\szdiag\zt\<СЗ>` — стирается в Revert |
| `ZtNodeId` | для отчёта hub и удаления члена |
| `ZtAddress` | назначенный IP, справочно |

Парный шаг `Revert`: «zerotier» — **первым**, вместе с quick tunnel (снять наружную дверь до sshd):
остановить ноду, стереть `ZtStoragePath`. Удаление члена на контроллере делает hub по `ReportAccess`/`close`.

## AccessMode — новое значение

`zt` (в `SzDiag.Contracts.AccessMode`); `AccessHost` = ZT-IP агента; `szcli target` печатает
`ssh -p 22 svc-diag@<zt-ip>` с пиннингом host-ключа, как для туннеля.

## HubOptions — новые секции

- `Hub.Signpost`: `Enabled`, `Url`, `PublishInterval=30s`, `HubName`, `Takeover=false`, `SignKeyPath`, `ExtraEndpoints` (руками; build-dist пустой).
- `Hub.ZeroTier`: `Enabled`, `Network`, `ApiTokenPath` (из `secrets\fleet.json`), `SweepAfter=10m`.
- `Hub.DenySubnets`: `["10.199.89.0/24"]` — запросы на эти локальные адреса отвергаются.
- `Hub.ManagementLoopbackOrLanOnly`: management API не отвечает на адреса сервисной ZT-подсети.
