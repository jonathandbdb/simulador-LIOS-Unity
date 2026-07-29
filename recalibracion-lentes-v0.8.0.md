# Propuesta de recalibración del catálogo — modelo óptico v0.8.0

**Fecha:** 2026-07-27 · **Catálogo vivo comparado:** `0.6.0-clinical.a50` (`GET https://vr.conecta.sh/api/lenses`)
**Apps publicadas:** visor y tablet `0.5.0` (versionCode 500)

Documento de decisión, no doc viva. Una vez aplicado (o descartado) se puede borrar.

---

## Por qué hay que recalibrar

El modelo óptico cambió en v0.8.0 y dos parámetros dejaron de significar lo que significaban
cuando se calibraron los valores que hoy están en producción:

- **`desenfoque_max`** era un tope `0..1` sobre un desenfoque que **saturaba a 1.5 D**. Ahora es un
  **multiplicador del radio físico** del círculo de desenfoque, sin saturación. Un `0.79` que antes
  quería decir "79 % de la fuerza máxima del efecto" hoy quiere decir "mostrar el 79 % del
  desenfoque que la física predice".
- **`profundidad_foco_m`** siempre se restó antes del blur, pero con el modelo viejo su efecto
  quedaba enmascarado porque todo saturaba igual. Ahora se ve.

Además se agregó **`cataract_scatter`**, que no existe en el catálogo de producción.

---

## ⚠️ La DB y el dispositivo NO coinciden hoy

`CatalogParser.MergeMissingParams` completa los params ausentes usando los defaults que viajan
**dentro del APK**, indexando **por `id`**. Consecuencia:

| lente | `cataract_scatter` en la DB | lo que aplica el visor 0.5.0 |
|---|---|---|
| `catarata` | ausente | **0.6** (inyectado desde el APK) |
| `monofocal`, `vivity`, `panoptix`, `paciente_joven` | ausente | 0.0 (inyectado) |
| `generic_a209ba91` ("monofocal plus") | ausente | **0.0** — *no está en los defaults, nunca recibe merge* |

O sea: **la catarata ya se comporta distinto de lo que dice la base de datos.** Cualquier lectura
de la DB sin tener esto en cuenta induce a error.

---

## Cambios propuestos

Solo se listan los parámetros que cambian. Todo lo no listado se mantiene tal cual.

| lente | parámetro | hoy en la DB | propuesto |
|---|---|---|---|
| **paciente_joven** | `desenfoque_max` | `0.029871298` | **`0.0`** |
| | `cataract_scatter` | *(ausente)* | **`0.0`** |
| **monofocal** | `foco_lejos_m` | `6.021838` | **`6.0`** |
| | `profundidad_foco_m` | `0.0` | **`1.0`** |
| | `desenfoque_max` | `0.78903085` | **`1.0`** |
| | `astig_magnitude` | `0.008730474` | **`0.0`** |
| | `cataract_scatter` | *(ausente)* | **`0.0`** |
| **monofocal plus** (`generic_a209ba91`) | `foco_lejos_m` | `5.966443` | **`6.0`** |
| | `profundidad_foco_m` | `0.24067381` | **`1.4`** |
| | `desenfoque_max` | `0.8079977` | **`1.0`** |
| | `cataract_scatter` | *(ausente)* | **`0.0`** |
| **vivity** (EDOF) | `foco_intermedio_m` | `0.990826` | **`1.0`** |
| | `profundidad_foco_m` | `1.50683963` | **`1.0`** |
| | `desenfoque_max` | `0.8919068` | **`1.0`** |
| | `cataract_scatter` | *(ausente)* | **`0.0`** |
| **panoptix** (trifocal) | `foco_intermedio_m` | `1.00269628` | **`1.0`** |
| | `foco_cerca_m` | `0.29962188` | **`0.42`** |
| | `profundidad_foco_m` | `2.01077127` | **`0.70`** |
| | `desenfoque_max` | `0.8562958` | **`1.0`** |
| | `cataract_scatter` | *(ausente)* | **`0.0`** |
| **catarata** | `desenfoque_max` | `2.0` | **`1.0`** |
| | `cataract_scatter` | *(ausente, el visor usa 0.6)* | **`0.9`** |

---

## Por qué cada cambio

### 1. `panoptix`: `profundidad_foco_m` 2.01 → 0.70 — *el más importante*

Con 2.01 la tolerancia es de **±1.0 D alrededor de cada foco**. Como los tres focos del panoptix
están a 0.17 / 1.00 / 3.34 D, las tres zonas **se fusionan en una sola franja nítida continua**: el
trifocal queda **perfectamente nítido de 30 cm a 4 m**, indistinguible de `paciente_joven`.

Eso borra el punto pedagógico central del simulador. Un trifocal real tiene una curva de desenfoque
con **picos y valles**: se ve bien en las tres distancias de diseño y peor en el medio — y ese valle
alrededor de 55–65 cm es exactamente la queja clínica típica de esos pacientes.

Con 0.70 (±0.35 D) reaparecen los valles. **No es una regresión introducida por v0.8.0**: con el
modelo viejo también estaba nítido en todos lados, solo que se notaba menos porque todo saturaba.

### 2. `panoptix`: `foco_cerca_m` 0.30 → 0.42

El PanOptix real se diseña para una adición cercana de +3.25 D en el plano del LIO, que en el plano
de anteojos da un foco de lectura en **~40–42 cm**, no 30. Con el valor actual la lente queda *más*
borrosa a 42 cm que a 33 cm, que está al revés de la realidad.

### 3. `monofocal`: `profundidad_foco_m` 0.0 → 1.0

Un 0 significa **tolerancia cero**: el desenfoque arranca apenas te salís de los 6.02 m. Un
pseudofáquico monofocal real conserva ~0.5–1.0 D de profundidad de foco (por diámetro pupilar,
aberraciones y tolerancia neural) y ve bien de 2 m a infinito. Con 1.0 (±0.5 D) queda nítido hasta
~1.5 m y claramente borroso en lectura, que es el comportamiento de manual.

### 4. `monofocal plus`: `profundidad_foco_m` 0.24 → 1.4

Una monofocal mejorada (tipo Eyhance) se vende exactamente por esto: **más profundidad de foco que
una monofocal estándar, sin llegar a tener foco intermedio propio**. Con 0.24 es prácticamente
idéntica a la monofocal común (mirá la tabla de abajo: 1.19 vs 1.36 px al metro) y no se justifica
como lente distinta. Con 1.4 (±0.7 D) gana el intermedio y la diferencia se ve.

### 5. `vivity`: `profundidad_foco_m` 1.51 → 1.0

Con dos focos (lejos 0.17 D + intermedio 1.0 D) y ±0.5 D de tolerancia, la cobertura queda continua
de infinito a ~67 cm y después cae. Eso es la historia correcta de una EDOF: **estira el intermedio
pero no da lectura fina**. Con 1.51 llegaba demasiado cerca y se pisaba con el trifocal.

### 6. `desenfoque_max` → 1.0 en las cuatro LIOs

Ahora que multiplica óptica real, dejarlo en 0.79–0.89 significa **mostrar el 79–89 % del desenfoque
físico**. En una pantalla de ~24 píxeles por grado contra los ~60 del ojo humano, subestimar va en
la dirección equivocada: si algo hay que hacer es exagerar, no atenuar.

> Si preferís exagerar para que se note más en el visor, subilo **parejo en las cuatro**. Si lo
> movés lente por lente volvés a perder la comparación entre ellas, que es de lo que se trata.

### 7. `catarata`: `cataract_scatter` 0.6 → 0.9 y `desenfoque_max` 2.0 → 1.0

**El síntoma:** el cartel del pronóstico del SmartTV se sigue leyendo con catarata.

**La causa:** con `foco_lejos_m = 9.0`, el error dióptrico máximo a distancia infinita es
`1/9 = 0.111 D`, así que **nada más allá de ~2 m puede desenfocarse por foco**. A 4.86 m el scatter
aporta el **96 %** del desenfoque y el foco apenas el 4 %. La degradación de lejos depende
enteramente de `cataract_scatter`.

**El argumento clínico:** la lente tiene `cataract_yellow = 1.0` (brunescente **avanzada**) pero
scatter 0.6 (nuclear **moderada**). Está tuneada como avanzada en un eje y moderada en el otro.
Subirla a 0.9 la vuelve coherente consigo misma.

| a 4.86 m | radio | letra mínima legible |
|---|---|---|
| hoy en el visor (scatter 0.6) | 1.94 px | ~2.7 cm |
| propuesto (scatter 0.9) | 4.28 px | ~6.1 cm |

**El intercambio a mirar:** bajar `desenfoque_max` de 2.0 a 1.0 hace la catarata **menos** borrosa
de cerca (42 cm: 9.55 → 6.34 px). Los dos valores son igualmente ilegibles en la práctica, pero es
un cambio real — conviene mirarla de cerca antes de dar la tabla por buena. Si no convence, la
alternativa es dejar `desenfoque_max` en 2.0 y subir solo el scatter.

### 8. Dos que parecen accidentes de slider

- **`monofocal.astig_magnitude = 0.008730474`** — sin significado clínico, pero distinto de cero, así
  que el camino del astigmatismo se ejecuta al pedo en cada frame.
- **`paciente_joven.desenfoque_max = 0.029871298`** — con 0 exacto el gate de `VisionActivity` apaga
  el post-proceso entero y ahorra dos blits por ojo. Con 0.0299 nunca apaga y la lente de referencia
  gasta GPU sin producir ningún efecto visible.

También se redondean `foco_lejos_m` 6.021838 → 6.0, 5.966443 → 6.0, `foco_intermedio_m`
0.990826 / 1.00269628 → 1.0. Son artefactos de arrastre de slider; el cambio es cosmético.

---

## Antes y después

Radio del círculo de desenfoque en píxeles, escenario de día (pupila 3.0 mm), ppd 24 (el punto de
operación real del visor). Referencia: **0 = nítido · ~1 = leve · ~2 = molesto · >3 = ilegible**.

### Hoy (efectivo en el visor 0.5.0, con el merge del APK aplicado)

| distancia | joven | monofocal | monof. plus | vivity | panoptix | catarata |
|---|---|---|---|---|---|---|
| cartel TV — 4.86 m | 0.00 | 0.06 | 0.00 | 0.00 | 0.00 | 1.94 |
| optotipo — 4 m | 0.00 | 0.14 | 0.00 | 0.00 | 0.00 | 1.99 |
| pared — 2 m | 0.00 | 0.54 | 0.35 | 0.00 | 0.00 | 2.49 |
| monitor — 1 m | 0.00 | 1.36 | 1.19 | 0.00 | 0.00 | 4.13 |
| tablero auto — 84 cm | 0.00 | 1.67 | 1.50 | 0.00 | 0.00 | 4.84 |
| intermedio — 65 cm | 0.00 | 2.23 | 2.08 | 0.00 | 0.00 | 6.19 |
| 55 cm | 0.00 | 2.69 | 2.55 | 0.10 | 0.00 | 7.04 |
| lectura — 42 cm | 0.01 | 3.60 | 3.49 | 1.14 | 0.00 | 9.55 |
| 33 cm | 0.00 | 4.66 | 4.57 | 2.33 | 0.00 | 12.04 |

### Propuesto

| distancia | joven | monofocal | monof. plus | vivity | panoptix | catarata |
|---|---|---|---|---|---|---|
| cartel TV — 4.86 m | 0.00 | 0.00 | 0.00 | 0.00 | 0.00 | 4.28 |
| optotipo — 4 m | 0.00 | 0.00 | 0.00 | 0.00 | 0.00 | 4.29 |
| pared — 2 m | 0.00 | 0.00 | 0.00 | 0.00 | 0.00 | 4.35 |
| monitor — 1 m | 0.00 | 0.69 | 0.28 | 0.00 | 0.00 | 4.65 |
| tablero auto — 84 cm | 0.00 | 1.08 | 0.67 | 0.00 | 0.00 | 4.82 |
| intermedio — 65 cm | 0.00 | 1.80 | 1.39 | 0.08 | **0.39** | 5.19 |
| 55 cm | 0.00 | 2.38 | 1.96 | 0.66 | **0.44** | 5.54 |
| lectura — 42 cm | 0.00 | 3.54 | 3.12 | 1.82 | 0.00 | 6.34 |
| 33 cm | 0.00 | 4.88 | 4.46 | 3.16 | 0.62 | 7.39 |

**Qué cuenta cada lente después del cambio:**

- **monofocal** — ve de lejos, necesita anteojos para leer.
- **monofocal plus** — igual de lejos, gana el intermedio (0.28 vs 0.69 al metro).
- **vivity (EDOF)** — perfecta hasta 65 cm, se cae en la lectura fina.
- **panoptix (trifocal)** — cubre las tres distancias, **con un valle en 55–65 cm**.
- **catarata** — mal en todo, y ahora también de lejos.
- **joven** — nítido en todo, y con GPU cero.

---

## Lo que deliberadamente NO se toca

`halo_intensity`, `halo_extra_rings`, `destello_intensity`, `destello_rayos`, `straylight`,
`contrast_loss`, `cataract_yellow`, `astig_axis_deg`.

Esos se calibraron a ojo contra lo que se ve en el visor, y no hay ni capturas ni ancla
bibliográfica para discutirlos desde la óptica. Inventar números ahí sería peor que dejarlos.

La única excepción es `cataract_scatter`, y solo porque hay un argumento concreto y verificable: la
incoherencia entre los dos ejes de severidad de la misma catarata (ver §7).

---

## Riesgos y cosas a verificar antes de fijar

1. **Confirmar que el visor está en 0.5.0.** Si sigue en 0.4.5 se estaría calibrando contra el
   modelo viejo y el trabajo no sirve.
2. **La catarata de cerca** se vuelve menos borrosa (§7). Mirarla antes de aceptar.
3. **`monofocal plus` no recibe merge**: es la única lente donde `cataract_scatter` hay que agregarlo
   sí o sí en la DB, porque no está en los defaults del APK.
4. **Overrides por dispositivo**: los visores que ya tengan ajustes guardados en
   `lens_overrides.json` mantienen sus valores por clave. Un cambio en la DB **no** los pisa.
5. Nada de esto requiere rebuild ni republicar: es **dato**.

---

## Cómo aplicarlo

**Para probar** — desde la tablet (`Ajuste fino`), en vivo y sin persistir en el catálogo. El slider
`Catarata (dispersión)` ya está disponible en la tablet 0.5.0.

**Para fijar** — migración sobre el catálogo vivo, con el mismo procedimiento del rollout de
`cataract_yellow` (`docs/backend.md`): `pg_dump` a `/root/backups/` primero, script one-shot que lee
la fila activa en el momento (no hardcodear `.a50`), aborta ante cualquier delta inesperado, y
calcula la versión nueva con `_next_admin_lens_version()` — **nunca** `0.8.0-clinical` limpio, que
está en `_KNOWN_SEED_VERSIONS` y el seed la pisaría. La fila vieja queda desactivada para rollback
desde `/admin/lenses`.
