#!/bin/bash
# Libreria compartida de los hooks del enjambre Simulador LIOs.
# Se hace `source` desde los demas scripts. Sin dependencias de jq/python:
# extraccion del JSON de stdin con grep/sed puro (Git Bash en Windows).

# Extrae un campo string plano del JSON ("clave": "valor"). No soporta valores
# con comillas escapadas — suficiente para file_path y tool_name.
sim_json_field() {
    # $1 = JSON completo, $2 = nombre del campo
    printf '%s' "$1" \
        | grep -o "\"$2\"[[:space:]]*:[[:space:]]*\"[^\"]*\"" \
        | head -n1 \
        | sed -e "s/^\"$2\"[[:space:]]*:[[:space:]]*\"//" -e 's/"$//'
}

# Normaliza un path de Windows a forward slashes y minusculas de drive
# (C:\x\y -> C:/x/y) para poder matchear con regex uniformes.
sim_norm_path() {
    printf '%s' "$1" | sed -e 's#\\\\#/#g' -e 's#\\#/#g'
}

# Escapa un string para incrustarlo en un valor JSON (backslash, comillas,
# newlines). Uso: sim_json_escape "$mensaje"
sim_json_escape() {
    printf '%s' "$1" \
        | sed -e 's/\\/\\\\/g' -e 's/"/\\"/g' \
        | awk 'BEGIN{ORS="\\n"} {print}' \
        | sed -e 's/\\n$//'
}

# Mapea un path del repo a su doc viva. Imprime el path de la doc o vacio.
sim_doc_for_path() {
    local p
    p=$(sim_norm_path "$1")
    case "$p" in
        *Assets/Scripts/Runtime/Vision/*|*Assets/Shaders/*) echo "docs/vision-optica.md" ;;
        *Assets/Scripts/Runtime/Net/TabletController*)      echo "docs/tablet.md" ;;
        *Assets/Scripts/Runtime/Net/*)                      echo "docs/networking.md" ;;
        *Assets/Scripts/Runtime/Tablet/*)                   echo "docs/tablet.md" ;;
        *Assets/Scripts/Runtime/Data/*|*StreamingAssets/lentes.json) echo "docs/catalogo-lentes.md" ;;
        *Assets/Scripts/Editor/TabletBuild*)                echo "docs/builds-deploy.md" ;;
        *backend/*|*defaults/lentes.json)                   echo "docs/backend.md" ;;
        *) echo "" ;;
    esac
}
