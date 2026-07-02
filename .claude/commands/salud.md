---
description: Chequeo de salud del entorno - Editor MCP, compilación, docker, adb, remote git y docs vivas
---

Chequeá la salud del entorno de trabajo SIN modificar nada. Corré (paralelizá lo independiente):

1. **Editor MCP**: `unity_editor_ping` → si responde, `unity_get_compilation_errors`.
2. **Backend**: `docker compose ps` en `backend/` (¿servicios arriba? — que docker no esté
   corriendo NO es error si no se va a trabajar backend: marcalo como ⚠️ informativo).
3. **Dispositivos**: `adb devices`.
4. **Git**: `git remote -v` (debe existir `lios`; recordá que `origin` está prohibido como
   destino) + `git status --short` (¿hay trabajo sin commitear?).
5. **Docs vivas**: existencia de las 7 (`docs/README.md`, `vision-optica`, `networking`,
   `tablet`, `catalogo-lentes`, `builds-deploy`, `backend`).

Presentá una tabla:

| Chequeo | Estado | Acción sugerida |
|---------|--------|-----------------|
| Editor Unity (MCP) | ✅/❌ | ... |
| Compilación | ✅/❌ | ... |
| Backend (docker) | ✅/⚠️/❌ | ... |
| adb | ✅/⚠️ | ... |
| Remote lios | ✅/❌ | ... |
| Docs vivas | ✅/❌ (faltan: ...) | ... |

Cada ❌ con su acción concreta (ej.: "abrí el Editor", "docker compose up -d", "conectá la
tablet"). Priorizá ❌ sobre ⚠️.
