---
name: git-lios
description: Fuente única de la convención git del proyecto - remote lios exclusivo (origin prohibido), staging selectivo con lista negra, .meta atómicos, formato de commit con scopes. Cargar antes de cualquier operación git.
---

# Git del proyecto — convención (fuente única)

## Remotes

| Remote | URL | Estado |
|--------|-----|--------|
| `lios` | `https://github.com/jonathandbdb/simulador-LIOS-Unity.git` | ✅ **Único destino permitido** de push |
| `origin` | `https://github.com/jonathandbdb/My-project.git` | ❌ **PROHIBIDO** (repo legacy; existe pero no se usa) |

- Antes de todo push: `git remote -v` y confirmar destino `lios`.
- Nunca `git remote remove/rename/set-url` sobre `lios` (el hook `protect.sh` bloquea
  `push origin` y alteraciones de `lios` como red de seguridad — pero la regla es no llegar ahí).

## Flujo

- **`main` directo** (desarrollador único). Ramas `feature/<nombre>` solo si el usuario las pide.
- Commit/push **SOLO a pedido explícito del usuario** — jamás por iniciativa del enjambre.
- Pull/merge con conflicto → frenar y preguntar; no resolver por cuenta propia.

## Formato de commit

`<scope>: <descripción en español, imperativo/presente>` — igual que el historial del repo
(ej.: `RutaNoche: extrae materiales del interior y baja el brillo`, `vision: encandilamiento
clinico`).

Scopes canónicos: `vision:` · `net:` · `tablet:` · `data:` · `backend:` · `build:` · `scenes:`
· `docs:` · `enjambre:` (cambios en `.claude/`, `CLAUDE.md`, `AGENTS.md`). Nombres de escenario
(`RutaNoche:`, `consultorio:`) también valen cuando el cambio es de un escenario concreto.

## Staging — selectivo SIEMPRE

- Por path explícito. **Nunca** `git add -A`, `git add .`, ni `git commit -a`.
- **Lista negra** (jamás stagear): `Library/`, `Temp/`, `Logs/`, `obj/`, `UserSettings/`,
  `build/`, `builds/`, `Builds/`, `*.apk`, `.env`, `*.csproj`, `*.slnx`,
  `.claude/settings.local.json`.
- **`.meta` atómicos**: asset nuevo/movido/borrado y su `.meta` van en el MISMO commit. Un
  `.meta` sin asset (o viceversa) en `git status` es un problema a resolver ANTES de commitear.
- Secretos: nada de tokens/passwords/IPs con credenciales en ningún commit.

## Prohibiciones permanentes

- `--force` / `--force-with-lease` (sin excepción salvo pedido explícito y consciente del
  usuario).
- `--amend` sobre commits ya pusheados.
- `--no-verify` (saltear hooks).
- Push a `origin` bajo cualquier circunstancia.
