# Implementation Plan: Доступ без своего домена — ZeroTier, подписанный указатель hub и ключи флота

**Branch**: `2028-domain-independent-access` | **Date**: 2026-11-24 | **Spec**: [spec.md](spec.md)
**Input**: Feature specification from `specs/002-domain-independent-access/spec.md`

## Summary

Три слоя, поставляемые по очереди:
1. **Ключи флота** — один набор секретов (ключ подписи, токены, SSH-ключ, токен ZT API) шифруется
   паролем флота и хранится переменной Railway; `build-dist` забирает его вместо генерации своего.
2. **Подписанный указатель** — сервис `szdiag-signpost` в своём Railway оператора хранит
   подписанные ECDSA-конверты «где hub» и «какой релиз» + пакет агента. Общий резолвер
   `HubLocator` для апдейтера и агента; аренда против двух hub-ов. На GitHub — ничего.
3. **ZeroTier-доступ** — агент входит в сервисную сеть через libzt (гейт — спайк), публикует
   sshd и управляющий канал по ZT; hub чистит членов сети через API контроллера.

## Technical Context

**Language/Version**: C# / .NET 8 (`net8.0`), PowerShell 5.1 (скрипты — UTF-8 с BOM)
**Primary Dependencies**: ASP.NET Core / SignalR (есть), `ZeroTier.Sockets` 1.8.4 (новая, только агент),
`System.Security.Cryptography` (ECDSA P-256, AES-GCM, PBKDF2 — BCL); CLI `railway` 4.x и `gh` у оператора
**Storage**: Railway volume `/data` (релиз + 3 пакета), аренда в памяти сервиса; `secrets\` на hub-машинах;
`signpost-seen.json` рядом с exe клиента
**Testing**: xUnit (`dotnet test`), фейковые источники резолвера; live-проверки — `quickstart.md`
**Target Platform**: hub/agent/updater — Windows x64; signpost — Linux-контейнер на Railway
**Project Type**: распределённая система (hub-сервис + клиентские агенты + облачный указатель)
**Performance Goals**: смена активного hub видна клиентам ≤ 1 мин; ZT-доступ ≤ 60 с от `Open`
**Constraints**: на клиенте ни драйверов, ни служб, откат без следов; рабочий токен/приватные
ключи на клиента не попадают; запуски GitHub Actions не нужны (workflow в репо нет)
**Scale/Scope**: 2–3 hub-машины, единицы одновременных СЗ

## Constitution Check

`.specify/memory/constitution.md` — незаполненный шаблон, формальных гейтов нет. Сверка с
инвариантами CLAUDE.md:

| Инвариант | Как соблюдён |
|---|---|
| Каждый шаг `Open` — парный шаг `Revert` под флагом `RevertState` | ZT-шаг: `JoinedZt`/`ZtStoragePath`, откат первым вместе с туннелем |
| Рабочий токен на клиента не заезжает | токен ZT API и приватный ключ подписи — только на hub; клиенту — публичный ключ и токен агента (как сейчас) |
| Имена протокола — в `HubRoutes`/Contracts | новые маршруты и `AccessMode.Zt` — в Contracts |
| Пути от `AppContext.BaseDirectory` | `signpost-seen.json`, `libzt.dll` — рядом с exe |
| `.ps1` в UTF-8 BOM | `tools\fleet.ps1`, правки `build-dist.ps1` |
| Секреты не в git | `secrets\` в `.gitignore`; бандл только шифротекстом и только в Railway |
| Минуты Actions / публичный репо | на GitHub ничего не публикуется (зеркало отклонено) |

Повторная сверка после дизайна: нарушений нет, Complexity Tracking — один пункт (ниже).

## Project Structure

### Documentation (this feature)

```text
specs/002-domain-independent-access/
├── plan.md · research.md · data-model.md · quickstart.md
├── contracts/signpost-api.md
├── checklists/requirements.md
└── tasks.md            # /speckit-tasks
```

### Source Code

```text
src/
├── SzDiag.Contracts/
│   ├── Signing/SignedEnvelope.cs, EnvelopeSigner.cs, EnvelopeVerifier.cs   # ECDSA, kid
│   ├── Signpost/HubRecord.cs, ReleaseRecord.cs, SignpostRoutes.cs
│   ├── HubLocator.cs, SeenSeqStore.cs                                       # резолвер + replay
│   └── AccessMode.cs                                                        # + Zt
├── SzDiag.Signpost/            # НОВЫЙ: minimal API, Dockerfile, LeaseTable, PackageStore
├── SzDiag.ZtController/        # НОВЫЙ: zerotier-one (контроллер) + Bearer-прокси к его API, Dockerfile
├── SzDiag.Hub/
│   ├── SignpostPublisher.cs    # hosted: публикация/продление, takeover, статус
│   ├── ZtControllerClient.cs   # прокси своего контроллера: member delete, ipAssignments
│   ├── ZtMemberSweeper.cs      # hosted: члены без СЗ > 10 мин
│   ├── LocalAddressFilter.cs   # DenySubnets + management не на ZT-подсети
│   └── HubOptions.cs           # Signpost / ZeroTier / DenySubnets
├── SzDiag.Agent/
│   ├── ZtNode.cs, ZtStream.cs, ZtSshForwarder.cs     # libzt, Stream, проброс :22
│   ├── ZtHubTransport.cs       # ConnectCallback/WebSocketFactory для SignalR/HttpClient
│   ├── WindowsSystemAccessManager.cs  # шаг 11 Open + парный Revert
│   └── RevertState.cs          # JoinedZt, ZtStoragePath, ZtNodeId, ZtAddress
├── SzDiag.Updater/Program.cs   # LocateReleaseAsync вместо HubDiscovery напрямую
└── SzDiag.Cli/                 # `fleet sign-release`, target для zt
tools/
├── fleet.ps1                   # init / pull
└── build-dist.ps1              # pull секретов, -Publish, build-info.json, libzt.dll рядом с агентом
spikes/zt-spike/                # вне решения, удаляется после фазы 3
tests/  SzDiag.Contracts.Tests · SzDiag.Hub.Tests · SzDiag.Agent.Tests · SzDiag.Signpost.Tests (новый)
```

**Structure Decision**: существующая раскладка `src/` + зеркальные `tests/`; добавляется один
проект `SzDiag.Signpost` (и его тесты) — это отдельный процесс на другой платформе.

## Фазы поставки

| # | Фаза | Закрывает | Зависит от |
|---|---|---|---|
| 1 | Ключи флота: `fleet.ps1`, бандл, `build-dist` без самогенерации | US2, FR-010…015 | — |
| 2 | Конверты + signpost-сервис + публикатор + `HubLocator` + аренда | US1, US4, FR-001…009, 025 | 1 (ключ подписи) |
| 3 | Спайк libzt (research R8, 6 пунктов) | гейт | — |
| 4 | ZT в агенте, sweeper, фильтр адресов, `target` | US3, FR-016…024 | 2, 3 |

Фазы 1–2 тестируются на существующих адресах (LAN / CF-туннель); US1 «без домена» целиком —
только после фазы 4. Провал спайка → фаза 4 уходит в отдельную спеку, 1–2 поставляются.

## Risks

- **libzt под .NET 8** не проверен (R8) — гейт фазы 3.
- **Лицензия ZeroTier (BSL 1.1)** — вопрос владельца, не блокирует код.
- **Публичный ID сети** — посторонние входят в сеть; закрыто flow rules, сторожем и фильтром management.
- **Railway `*.up.railway.app`** — чужое имя; указатель недоступен → LAN и прежний конфиг.

## Complexity Tracking

| Violation | Why Needed | Simpler Alternative Rejected Because |
|---|---|---|
| Новый проект `SzDiag.Signpost` (13-й) | облачный процесс на Linux, отдельный деплой | встроить в hub нельзя — hub за NAT и меняет машину; встроить в `crm-api` — чужая кодовая база |
