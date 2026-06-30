"""Sistema simple de i18n para el panel admin (es/en).

No usamos gettext/Babel para mantener el stack delgado. Las traducciones
viven en este modulo y se exponen al template como funcion `t(key)`.
Si una clave no existe en el idioma elegido, cae al espanol; si tampoco
esta en espanol, devuelve la clave misma (para que sea facil detectarlo
visualmente en la UI durante el desarrollo).
"""
from typing import Literal

Lang = Literal["es", "en"]

DEFAULT_LANG: Lang = "es"
SUPPORTED_LANGS = ("es", "en")

# Diccionario de traducciones. Espanol primero (fuente de verdad).
TRANSLATIONS: dict[str, dict[Lang, str]] = {
    # Layout / navbar
    "app.title":           {"es": "Simulador VR — Admin",        "en": "Simulador VR — Admin"},
    "nav.dashboard":       {"es": "Inicio",                       "en": "Home"},
    "nav.devices":         {"es": "Dispositivos",                 "en": "Devices"},
    "nav.lenses":          {"es": "Lentes",                       "en": "Lenses"},
    "nav.versions":        {"es": "Versiones",                    "en": "Versions"},
    "nav.logs":            {"es": "Logs",                         "en": "Logs"},
    "nav.logout":          {"es": "Salir",                        "en": "Logout"},
    "nav.lang":            {"es": "Idioma",                       "en": "Language"},
    # Login
    "login.title":         {"es": "Acceso al panel",              "en": "Admin login"},
    "login.username":      {"es": "Usuario",                      "en": "Username"},
    "login.password":      {"es": "Contrasena",                   "en": "Password"},
    "login.submit":        {"es": "Entrar",                       "en": "Sign in"},
    "login.error":         {"es": "Usuario o contrasena invalidos.", "en": "Invalid username or password."},
    "theme.toggle":        {"es": "Cambiar tema claro/oscuro",    "en": "Toggle light/dark theme"},
    # Dashboard
    "dash.title":          {"es": "Panel de control",             "en": "Dashboard"},
    "dash.warn_title":     {"es": "Configuracion insegura detectada",
                            "en": "Insecure configuration detected"},
    "dash.warn_default_pass": {"es": "La contrasena del admin sigue siendo la default (admin123). Cambiar ADMIN_DEFAULT_PASS en el .env y recrear el contenedor.",
                               "en": "Admin password is still the default (admin123). Set ADMIN_DEFAULT_PASS in .env and recreate the container."},
    "dash.warn_default_jwt": {"es": "JWT_SECRET sigue siendo el valor de desarrollo. Definir un secreto fuerte en el .env antes de exponer el panel.",
                              "en": "JWT_SECRET is still the development value. Set a strong secret in .env before exposing the panel."},
    "dash.devices_total":  {"es": "Dispositivos registrados",     "en": "Registered devices"},
    "dash.devices_active": {"es": "Activos",                      "en": "Active"},
    "dash.active_version": {"es": "Version activa",               "en": "Active version"},
    "dash.active_catalog": {"es": "Catalogo activo",              "en": "Active catalog"},
    "dash.last_events":    {"es": "Ultimos eventos",              "en": "Latest events"},
    "dash.no_active":      {"es": "(ninguna)",                    "en": "(none)"},
    # Devices
    "dev.title":           {"es": "Dispositivos",                 "en": "Devices"},
    "dev.new":             {"es": "Registrar nuevo",              "en": "Register new"},
    "dev.device_id":       {"es": "Device ID",                    "en": "Device ID"},
    "dev.name":            {"es": "Nombre",                       "en": "Name"},
    "dev.status":          {"es": "Estado",                       "en": "Status"},
    "dev.last_seen":       {"es": "Ultima conexion",              "en": "Last seen"},
    "dev.last_ip":         {"es": "IP",                           "en": "IP"},
    "dev.expiry":          {"es": "Vence",                        "en": "Expires"},
    "dev.notes":           {"es": "Notas",                        "en": "Notes"},
    "dev.actions":         {"es": "Acciones",                     "en": "Actions"},
    "dev.save":            {"es": "Guardar",                      "en": "Save"},
    "dev.delete":          {"es": "Eliminar",                     "en": "Delete"},
    "dev.delete_confirm":  {"es": "Eliminar este dispositivo? Esta accion no se puede deshacer.",
                            "en": "Delete this device? This action cannot be undone."},
    "dev.status.active":    {"es": "Activo",                       "en": "Active"},
    "dev.status.suspended": {"es": "Suspendido",                   "en": "Suspended"},
    "dev.status.pending":   {"es": "Pendiente",                    "en": "Pending"},
    "dev.expiry_hint":      {"es": "Vacio = licencia permanente.", "en": "Empty = permanent license."},
    "dev.empty":            {"es": "No hay dispositivos registrados aun.",
                             "en": "No devices registered yet."},
    "dev.created":          {"es": "Dispositivo creado.",           "en": "Device created."},
    "dev.updated":          {"es": "Dispositivo actualizado.",      "en": "Device updated."},
    "dev.deleted":          {"es": "Dispositivo eliminado.",        "en": "Device deleted."},
    "dev.duplicate":        {"es": "Ya existe un dispositivo con ese Device ID.",
                             "en": "A device with that Device ID already exists."},
    # Lenses
    "lens.title":           {"es": "Catalogo de lentes",            "en": "Lens catalog"},
    "lens.new":             {"es": "Nuevo catalogo",                "en": "New catalog"},
    "lens.version":         {"es": "Version",                       "en": "Version"},
    "lens.active":          {"es": "Activo",                        "en": "Active"},
    "lens.created":         {"es": "Creado",                        "en": "Created"},
    "lens.lens_count":      {"es": "Lentes",                        "en": "Lenses"},
    "lens.activate":        {"es": "Activar",                       "en": "Activate"},
    "lens.edit":            {"es": "Editar",                        "en": "Edit"},
    "lens.json":            {"es": "JSON del catalogo",             "en": "Catalog JSON"},
    "lens.json_hint":       {"es": "Estructura: {\"version\":\"x.y.z\", \"catalogo\":[{\"id\":\"...\", \"nombre\":\"...\", \"descripcion\":\"...\", \"params\":{\"clave\":{\"default\":..,\"min\":..,\"max\":..}}}, ...]}",
                             "en": "Structure: {\"version\":\"x.y.z\", \"catalogo\":[{\"id\":\"...\", \"nombre\":\"...\", \"descripcion\":\"...\", \"params\":{\"key\":{\"default\":..,\"min\":..,\"max\":..}}}, ...]}"},
    "lens.invalid_json":    {"es": "JSON invalido: ",                "en": "Invalid JSON: "},
    "lens.no_catalogo":     {"es": "El JSON debe tener una clave 'catalogo' con un array.",
                             "en": "JSON must have a 'catalogo' key with an array."},
    "lens.save":            {"es": "Guardar y activar",              "en": "Save and activate"},
    "lens.saved":           {"es": "Catalogo guardado y activado.",  "en": "Catalog saved and activated."},
    "lens.activated":       {"es": "Catalogo activado.",             "en": "Catalog activated."},
    # Lenses — editor visual
    "lens.editor_visual":   {"es": "Editor visual",                   "en": "Visual editor"},
    "lens.editor_json":     {"es": "JSON crudo",                      "en": "Raw JSON"},
    "lens.add_lens":        {"es": "+ Agregar lente",                 "en": "+ Add lens"},
    "lens.lens_id":         {"es": "ID",                              "en": "ID"},
    "lens.lens_name":       {"es": "Nombre",                          "en": "Name"},
    "lens.lens_desc":       {"es": "Descripcion",                     "en": "Description"},
    "lens.param.halo":       {"es": "Intensidad de halos",            "en": "Halo intensity"},
    "lens.param.contrast":   {"es": "Perdida de contraste",           "en": "Contrast loss"},
    "lens.param.foco_lejos": {"es": "Foco lejano (m)",                "en": "Far focus (m)"},
    "lens.param.foco_inter": {"es": "Foco intermedio (m)",            "en": "Intermediate focus (m)"},
    "lens.param.foco_cerca": {"es": "Foco cercano (m)",               "en": "Near focus (m)"},
    "lens.param.prof_foco":  {"es": "Profundidad de foco (m)",        "en": "Depth of focus (m)"},
    "lens.param.desenfoque": {"es": "Desenfoque maximo",              "en": "Max blur"},
    "lens.param.halo_rings": {"es": "Dilatacion pupilar (noche)",     "en": "Pupil dilation (night)"},
    "lens.param.destello_int":   {"es": "Intensidad de starburst",    "en": "Starburst intensity"},
    "lens.param.destello_rayos": {"es": "Cantidad de rayos",          "en": "Ray count"},
    "lens.group.focos":          {"es": "Focos y desenfoque",         "en": "Foci and blur"},
    "lens.group.disfotopsias":   {"es": "Disfotopsias (halos, starburst, contraste)",
                                  "en": "Dysphotopsias (halos, starburst, contrast)"},
    "lens.desc_placeholder": {"es": "Descripcion clinica que se muestra en la tablet del doctor.",
                              "en": "Clinical description shown on the doctor's tablet."},
    "lens.range_hint":       {"es": "Los rangos min/max se editan en la pestana JSON crudo.",
                              "en": "Min/max ranges are edited in the raw JSON tab."},
    "lens.empty_table":     {"es": "Sin lentes. Agrega uno para empezar.",
                             "en": "No lenses yet. Add one to start."},
    "lens.delete_row":      {"es": "Eliminar",                        "en": "Delete"},
    "lens.delete_confirm":  {"es": "Eliminar esta lente del catalogo?",
                             "en": "Delete this lens from the catalog?"},
    "lens.id_required":     {"es": "El ID es obligatorio y debe ser unico.",
                             "en": "ID is required and must be unique."},
    "lens.name_required":   {"es": "Cada lente debe tener un nombre.",
                             "en": "Each lens must have a name."},
    # Versions
    "ver.title":            {"es": "Versiones publicadas",           "en": "Published versions"},
    "ver.new":              {"es": "Subir nueva version",            "en": "Upload new version"},
    "ver.apk_version":      {"es": "APK",                            "en": "APK"},
    "ver.asset_version":    {"es": "Assets",                         "en": "Assets"},
    "ver.min_apk":          {"es": "APK minima",                     "en": "Min APK"},
    "ver.changelog":        {"es": "Changelog",                      "en": "Changelog"},
    "ver.apk_file":         {"es": "Archivo APK",                    "en": "APK file"},
    "ver.pck_file":         {"es": "Archivo PCK",                    "en": "PCK file"},
    "ver.upload":           {"es": "Subir y publicar",               "en": "Upload and publish"},
    "ver.upload_hint":      {"es": "Los archivos se suben directo al bucket. El SHA256 del PCK se calcula automaticamente.",
                             "en": "Files are uploaded straight to the bucket. PCK SHA256 is computed automatically."},
    "ver.activate":         {"es": "Activar",                        "en": "Activate"},
    "ver.delete":           {"es": "Eliminar",                       "en": "Delete"},
    "ver.empty":            {"es": "No hay versiones publicadas.",   "en": "No versions published yet."},
    "ver.created":          {"es": "Version creada y activada.",     "en": "Version created and activated."},
    "ver.activated":        {"es": "Version activada.",              "en": "Version activated."},
    "ver.deleted":          {"es": "Version eliminada.",             "en": "Version deleted."},
    "ver.upload_error":     {"es": "Error al subir: ",               "en": "Upload error: "},
    # Logs
    "log.title":            {"es": "Logs de los visores",            "en": "Visor logs"},
    "log.device_filter":    {"es": "Filtrar por Device ID",          "en": "Filter by Device ID"},
    "log.event_filter":     {"es": "Filtrar por evento",             "en": "Filter by event"},
    "log.from":             {"es": "Desde",                          "en": "From"},
    "log.to":               {"es": "Hasta",                          "en": "To"},
    "log.apply":            {"es": "Aplicar",                        "en": "Apply"},
    "log.reset":            {"es": "Reset",                          "en": "Reset"},
    "log.export_csv":       {"es": "Exportar CSV",                   "en": "Export CSV"},
    "log.timestamp":        {"es": "Cuando",                         "en": "When"},
    "log.event":            {"es": "Evento",                         "en": "Event"},
    "log.detail":           {"es": "Detalle",                        "en": "Detail"},
    "log.empty":            {"es": "No hay logs con esos filtros.",  "en": "No logs match these filters."},
    # Generic
    "yes":                  {"es": "Si",                             "en": "Yes"},
    "no":                   {"es": "No",                             "en": "No"},
    "cancel":               {"es": "Cancelar",                       "en": "Cancel"},
    "never":                {"es": "nunca",                          "en": "never"},
}


def normalize_lang(value: str | None) -> Lang:
    if value in SUPPORTED_LANGS:
        return value  # type: ignore[return-value]
    return DEFAULT_LANG


def t(key: str, lang: Lang = DEFAULT_LANG) -> str:
    """Devuelve la traduccion de `key` en `lang`. Fallback es -> key."""
    entry = TRANSLATIONS.get(key)
    if entry is None:
        return key
    return entry.get(lang) or entry.get(DEFAULT_LANG) or key
