# Optimization Summary
## 1. Current Progress \& Key Decisions
- Async fixes completed in Content.IntegrationTests...
- Test timeout persistent despite fixes...
- NuGet config: Added dotnet-eng source...
## 2. Key Context \& Constraints
- Project structure: Path: C:\\Users\\Orvar Od\\Monolith-DS...
- Constraints: AGPLv3 license...
## 3. Remaining Tasks
- Resolve test timeout...
- Verify async fixes...
- Update docs...
## 4. Critical Data
- Error logs: SSL/auth failures for nuget...
- Config files: Directory.Packages.props...
- Summary file: optimization_summary.md...
### NEXT STEPS
1. Diagnose timeout root cause...
2. Address NuGet SSL issues...
3. Document best practices...

## 5. Анализ лагов прод-сервера (2026-08-18, ~23:29 MSK)

Снято после деплоя `luam-20260818-research-persistence` (build `9b5e4b68...`).

### Железо и нагрузка

- VPS: 4 ядра AMD EPYC-Rome @ 2.25 ГГц, 17 ГБ RAM, 4 ГБ swap.
- Load average: 2.34 / 2.27 / 2.16; игровой процесс ~130% CPU (≈1.1–1.3 ядра), RSS 7.1 ГБ.
- `MainLoop: Cannot keep up` — 135 раз за день, при 0 онлайн всё равно повторяется раз в 1–2 минуты.

### Состояние мира

- `robust_entities_count` = 239 677 сущностей.
- `physics_active_mover_count` = 544.
- `npc_active_count` = 9, `npc_steering_active_count` = 0.

### Топ систем по суммарному времени апдейта (~25 минут аптайма)

- PhysicsSystem — 256.88s
- LuaMBehaviorSystem — 153.81s
- LuaMStationaryTurretBehaviorAdapterSystem — 63.22s
- AtmosphereSystem — 56.46s
- GameTicker — 42.29s
- LocalityLoaderSystem — 30.52s
- FlammableSystem — 14.57s
- PowerNetSystem — 14.19s
- EmitSoundSystem — 10.56s
- PathfindingSystem — 8.98s

### Пики лагов

- 23:28 — заход игрока: разворачивание станции SQI CIV-088 + отдача полного состояния.
- 23:29:35 — `PVS requested full state` у holodilnik66; при отставании тика клиенты просят полный ресинк — петля «отставание → full state → ещё большее отставание».

### Что НЕ причина

- Диск: wa 0%, своп: 38 МБ, GC pause ratio: 0, CPU steal: 0–1%.

### Распараллеливание (уже работает)

- `thread.parallel_count` = auto (4 ядра). Выше поднимать не нужно — только хуже.
- Физика: islands + broadphase параллельно.
- PVS: сериализация/отправка состояния по клиентам параллельно.
- NPC steering/pathfinding параллельно.
- Основной тик последовательный по дизайну (порядок систем), его ядрами не ускорить.

### Рекомендации (по приоритету)

1. Срезать последовательную нагрузку: AI-поведения + адаптеры турелей (в сумме ~217s, второе место после физики), атмос, сущности 240k.
2. Включить Server GC в systemd-юните: `Environment=DOTNET_gcServer=1` (сейчас workstation GC; требует рестарта, даёт страховку, а не лечение — pause ratio сейчас 0).
3. Не поднимать `thread.parallel_count` выше числа ядер.
4. Железо: нужна частота, а не ядра — 2.25 ГГц узкое место тика.
5. Не делать шардинг мира по процессам — SS14 не поддерживает.
6. Min threads пула задаётся через runtimeconfig (`System.Threading.ThreadPool.MinThreads`), env-переменной для него нет.

### NEXT STEPS (server perf)

1. Профилировать LuaMBehaviorSystem + турели и решить, что урезать.
2. Оценить чистку накопившихся сущностей сектора.
3. Добавить `DOTNET_gcServer=1` при следующем плановом рестарте.
