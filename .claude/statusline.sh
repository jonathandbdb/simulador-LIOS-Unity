#!/usr/bin/env bash
# Status line para Claude Code (enjambre LIOS-Unity): modelo · carpeta · git · contexto · effort · 5h · costo
# Portado del enjambre NEXIT. El command de settings.json lo auto-ubica leyendo
# workspace.project_dir del JSON de entrada (no depende de $CLAUDE_PROJECT_DIR).
# Requiere python3 en la máquina (parseo de JSON sin jq).
input=$(cat)

# Extraer campos con python3 (no depende de jq); un campo por línea
# (evita que `read` colapse separadores cuando algún campo viene vacío)
{ read -r MODEL; read -r DIR; read -r PCT; read -r COST; read -r EFFORT; read -r RL5; } < <(printf '%s' "$input" | python3 -c '
import sys, json
try:
    d = json.load(sys.stdin)
except Exception:
    d = {}
model  = d.get("model", {}).get("display_name") or "?"
cur    = d.get("workspace", {}).get("current_dir") or d.get("cwd", "")
pct    = (d.get("context_window", {}) or {}).get("used_percentage", 0) or 0
cost   = (d.get("cost", {}) or {}).get("total_cost_usd", 0) or 0
effort = (d.get("effort", {}) or {}).get("level") or ""
rl5v   = (d.get("rate_limits", {}) or {}).get("five_hour", {}) or {}
rl5    = rl5v.get("used_percentage")
print(model)
print(cur)
print(int(round(pct)))
print(f"{cost:.2f}")
print(effort)
print("" if rl5 is None else int(round(rl5)))
')

# Defaults defensivos por si algún campo llegó vacío
[ -z "$PCT" ] && PCT=0
[ -z "$COST" ] && COST="0.00"

# Basename de la carpeta
DIRNAME=${DIR##*/}

# Rama git (vacío si no es repo)
BRANCH=$(git -C "$DIR" rev-parse --abbrev-ref HEAD 2>/dev/null)

# Barra de contexto: 10 celdas
FILLED=$(( (PCT + 5) / 10 ))
(( FILLED > 10 )) && FILLED=10
(( FILLED < 0  )) && FILLED=0
BAR=""
for ((i=0; i<10; i++)); do
  if (( i < FILLED )); then BAR+="█"; else BAR+="░"; fi
done

# Color de la barra según uso
if   (( PCT >= 80 )); then CTX_COLOR=$'\033[31m'   # rojo
elif (( PCT >= 50 )); then CTX_COLOR=$'\033[33m'   # amarillo
else                       CTX_COLOR=$'\033[32m'   # verde
fi
DIM=$'\033[2m'; CYAN=$'\033[36m'; MAG=$'\033[35m'; RST=$'\033[0m'

# Color del rate limit de 5h según consumo (si vino)
if [ -n "$RL5" ]; then
  if   (( RL5 >= 90 )); then RL_COLOR=$'\033[31m'   # rojo
  elif (( RL5 >= 70 )); then RL_COLOR=$'\033[33m'   # amarillo
  else                       RL_COLOR=$'\033[32m'   # verde
  fi
fi
BLUE=$'\033[34m'

# Construir la línea
LINE="${MAG}[${MODEL}]${RST} ${CYAN}📁 ${DIRNAME}${RST}"
[ -n "$BRANCH" ] && LINE+=" ${DIM}·${RST} ⎇ ${BRANCH}"
LINE+=" ${DIM}·${RST} ${CTX_COLOR}[${BAR}] ${PCT}%${RST}"
[ -n "$EFFORT" ] && LINE+=" ${DIM}·${RST} ${BLUE}⚡${EFFORT}${RST}"
[ -n "$RL5" ]    && LINE+=" ${DIM}·${RST} ${RL_COLOR}5h ${RL5}%${RST}"
LINE+=" ${DIM}· \$${COST}${RST}"

printf '%s' "$LINE"
