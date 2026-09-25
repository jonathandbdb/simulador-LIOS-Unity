#!/usr/bin/env python3
"""
Pipeline del video tutorial de uso. Lee `storyboard.es.json` y produce, por etapas:

    python docs/comercial/video/build-video.py plan     -> build/video/plan.tsv
    python docs/comercial/video/build-video.py subs     -> build/video/subs.es.ass
    python docs/comercial/video/build-video.py compose  -> build/video/IOLSIMULATOR-tutorial-es.mp4

`plan` aplana el guion a un estado POR FRAME en un TSV plano. Esa forma es deliberada: el
arnes de captura (Assets/Scripts/Editor/TutorialCapture.cs) lo lee con File.ReadAllLines y
Split('\t'), sin parsear JSON, asi no hay que agregarle Newtonsoft al asmdef Simulador.Editor
ni meter logica de interpolacion en C#. Toda la matematica de curvas vive aca.

`compose` arma el split screen y se lo pasa a ffmpeg por stdin como rawvideo: no escribe los
~6.500 frames compuestos a disco (serian varios GB de PNG que nadie vuelve a mirar).

ffmpeg NO esta en el PATH de esta maquina; se resuelve por ruta absoluta (ver FFMPEG).
"""

import json
import os
import shutil
import subprocess
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[2]
STORYBOARD = HERE / "storyboard.es.json"
OUT = ROOT / "build" / "video"          # gitignorado (.gitignore:17), igual que los APK
VISOR_DIR = OUT / "visor"               # lo escribe el arnes de Unity
TABLET_DIR = OUT / "tablet"             # capturas de la app tablet (Editor clon)
PLAN = OUT / "plan.tsv"
ASS = OUT / "subs.es.ass"
MP4 = OUT / "IOLSIMULATOR-tutorial-es.mp4"

# ffmpeg 8.0.1 full build (winget). No esta en PATH; se invoca por ruta absoluta.
FFMPEG = Path(os.environ.get("FFMPEG", "")) if os.environ.get("FFMPEG") else None
if not FFMPEG:
    for cand in (
        Path.home() / "AppData/Local/Microsoft/WinGet/Links/ffmpeg.exe",
        Path("C:/Program Files/Shotcut/ffmpeg.exe"),
    ):
        if cand.is_file():
            FFMPEG = cand
            break
    else:
        FFMPEG = Path(shutil.which("ffmpeg") or "ffmpeg")

# ---------------------------------------------------------------- guion -> frames


def smoothstep(a: float, b: float, t: float) -> float:
    """Interpolacion con arranque y frenado suaves.

    Lineal se nota mal justo en lo que este video quiere mostrar: un giro de cabeza que
    arranca y frena de golpe parece un corte de camara, no una persona mirando.
    """
    t = min(1.0, max(0.0, t))
    return a + (b - a) * t * t * (3.0 - 2.0 * t)


def sample(keys, t, field):
    """Valor interpolado de una curva de keyframes [{t, <field>}, ...] en el instante t.

    Devuelve None si la curva no existe o si NINGUN keyframe declara ese campo: asi un beat
    puede definir `yaw` sin definir `pitch` (o al reves) sin tener que repetir ceros.
    """
    if not keys or not any(field in k for k in keys):
        return None
    keys = [k for k in keys if field in k]
    if t <= keys[0]["t"]:
        return float(keys[0][field])
    for k0, k1 in zip(keys, keys[1:]):
        if t <= k1["t"]:
            span = k1["t"] - k0["t"]
            u = 1.0 if span <= 0 else (t - k0["t"]) / span
            return smoothstep(float(k0[field]), float(k1[field]), u)
    return float(keys[-1][field])


def load():
    sb = json.loads(STORYBOARD.read_text(encoding="utf-8"))
    fps = sb["fps"]
    t0 = 0.0
    for b in sb["beats"]:
        b["_start"] = t0
        b["_frame0"] = round(t0 * fps)
        t0 += b["dur"]
        b["_frame1"] = round(t0 * fps)  # exclusivo
    sb["_total_s"] = t0
    sb["_frames"] = round(t0 * fps)
    return sb


def resolve_lens(beat, carry):
    """Lente por ojo del beat, arrastrando la del beat anterior si no la redefine."""
    lens = beat.get("lens")
    if not lens:
        return carry
    if "both" in lens:
        return (lens["both"], lens["both"])
    return (lens.get("left", carry[0]), lens.get("right", carry[1]))


BOOK_PARK_M = 0.72   # brazo extendido: presente en la escena, sin tapar nada
# Inclinacion por defecto del libro. Sin esto queda casi horizontal, como apoyado en una
# mesa: muy escorzado y las paginas no se leen. Signo y valor calibrados sobre frames
# reales (-40 tapaba el cuadro, +40 vuelca el libro al reves y se ve el canto).
BOOK_TILT_DEG = -35.0


def cmd_plan():
    sb = load()
    fps = sb["fps"]
    OUT.mkdir(parents=True, exist_ok=True)

    rows = []
    lens = ("paciente_joven", "paciente_joven")
    scenario = "consultorio"
    for b in sb["beats"]:
        lens = resolve_lens(b, lens)
        scenario = b.get("scenario", scenario)
        render = 1 if b.get("render", True) else 0
        focus = 1 if b.get("focusCheck", False) else 0
        head = b.get("head") or []
        book = b.get("book") or []
        for f in range(b["_frame0"], b["_frame1"]):
            t = (f - b["_frame0"]) / fps
            yaw = sample(head, t, "yaw") or 0.0
            pitch = sample(head, t, "pitch") or 0.0
            dist = sample(book, t, "dist")
            tilt = sample(book, t, "tilt")
            # En consultorio el libro SIEMPRE se posiciona. Sin esto queda donde este el
            # Right Controller -- que en el Editor, sin mando, es el origen del rig: el
            # libro pegado a la cara, tapando la escena que el video quiere mostrar.
            if dist is None and scenario == "consultorio":
                dist = BOOK_PARK_M
            rows.append(
                (f, render, scenario, lens[0], lens[1], focus,
                 f"{yaw:.3f}", f"{pitch:.3f}",
                 "-" if dist is None else f"{dist:.4f}",
                 f"{(BOOK_TILT_DEG if tilt is None else tilt):.2f}", b["id"])
            )

    with PLAN.open("w", encoding="utf-8", newline="\n") as fh:
        fh.write(f"#fps={fps}\n#seed={sb['seed']}\n")
        fh.write(f"#width={sb['visor']['width']}\n#height={sb['visor']['height']}\n")
        fh.write(f"#frames={len(rows)}\n")
        fh.write("frame\trender\tscenario\tlens_l\tlens_r\tfocus\t"
                 "yaw\tpitch\tbook\tbooktilt\tbeat\n")
        for r in rows:
            fh.write("\t".join(str(x) for x in r) + "\n")

    render_n = sum(1 for r in rows if r[1] == 1)
    print(f"{PLAN.relative_to(ROOT)}")
    print(f"  {len(rows)} frames  ({sb['_total_s']:.1f} s = {int(sb['_total_s'])//60}:"
          f"{int(sb['_total_s'])%60:02d})  ·  {render_n} a renderizar en Unity")
    for b in sb["beats"]:
        print(f"  {b['_frame0']:>5}-{b['_frame1']-1:<5} {b['id']:<18} {b['dur']:>5.1f}s"
              f"  {'card' if not b.get('render', True) else b.get('scenario', '')}")
    return 0


# ---------------------------------------------------------------- subtitulos (.ass)

ASS_HEAD = """[Script Info]
ScriptType: v4.00+
PlayResX: 1920
PlayResY: 1080
WrapStyle: 0
ScaledBorderAndShadow: yes

[V4+ Styles]
Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding
Style: Sub,Segoe UI Semibold,46,&H00FFFFFF,&H00FFFFFF,&H00201812,&H96000000,0,0,0,0,100,100,0,0,1,0,0,2,160,160,52,1

[Events]
Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text
"""


def ass_time(s: float) -> str:
    cs = int(round(s * 100))
    h, cs = divmod(cs, 360000)
    m, cs = divmod(cs, 6000)
    sec, cs = divmod(cs, 100)
    return f"{h}:{m:02d}:{sec:02d}.{cs:02d}"


def cmd_subs():
    sb = load()
    OUT.mkdir(parents=True, exist_ok=True)
    lines = [ASS_HEAD]
    n = 0
    for b in sb["beats"]:
        for s in b["subs"]:
            a = b["_start"] + s["t"]
            z = a + s["d"]
            text = s["text"].replace("\n", "\\N")
            lines.append(f"Dialogue: 0,{ass_time(a)},{ass_time(z)},Sub,,0,0,0,,{text}\n")
            n += 1
    ASS.write_text("".join(lines), encoding="utf-8")
    print(f"{ASS.relative_to(ROOT)} -> {n} subtitulos")
    return 0


# ---------------------------------------------------------------- narracion (TTS)

# Voz neuronal uruguaya: el guion esta escrito en voseo rioplatense ("elegi", "tocas",
# "pedile"), asi que una voz es-ES lo leeria con la prosodia equivocada. La alternativa
# local (Microsoft Helena, SAPI) es es-ES y suena claramente sintetica.
VOICE = "es-UY-MateoNeural"
VOICE_DIR = OUT / "voice"
WAV = OUT / "narracion.wav"
SR = 48000                     # igual que el audio del MP4, para no resamplear
GAP_S = 0.35                   # respiro minimo entre dos lineas habladas


def line_id(beat, i):
    return f"{beat['id']}_{i:02d}"


def cmd_voice():
    """Un .mp3 por subtitulo, con el MISMO texto que se lee en pantalla."""
    sb = load()
    VOICE_DIR.mkdir(parents=True, exist_ok=True)
    made = skipped = 0
    for b in sb["beats"]:
        for i, s in enumerate(b["subs"]):
            out = VOICE_DIR / f"{line_id(b, i)}.mp3"
            txt = VOICE_DIR / f"{line_id(b, i)}.txt"
            # Regenerar solo si cambio el texto: el TTS es una llamada de red por linea.
            if out.is_file() and txt.is_file() and txt.read_text(encoding="utf-8") == s["text"]:
                skipped += 1
                continue
            subprocess.run(
                [sys.executable, "-m", "edge_tts", "--voice", VOICE,
                 "--text", s["text"], "--write-media", str(out)],
                check=True, capture_output=True)
            txt.write_text(s["text"], encoding="utf-8")
            made += 1
            print(f"  {line_id(b, i)}: {s['text'][:60]}")
    print(f"{VOICE_DIR.relative_to(ROOT)} -> {made} generados, {skipped} ya estaban")
    return 0


def clip_duration(path: Path) -> float:
    out = subprocess.run(
        [str(FFMPEG).replace("ffmpeg.exe", "ffprobe.exe"), "-v", "error",
         "-show_entries", "format=duration", "-of", "csv=p=0", str(path)],
        check=True, capture_output=True, text=True).stdout.strip()
    return float(out)


def cmd_retime():
    """Reacomoda los tiempos del guion para que cada linea hablada entre completa.

    El video se monto primero con subtitulos leidos, que se recorren mas rapido que lo
    hablado. En vez de acelerar la voz (suena mal), se empujan los subtitulos dentro de su
    beat y, si no alcanza, se alarga el beat. Los visuales siguen el beat, asi que alargarlo
    es gratis salvo por volver a capturar.
    """
    sb = load()
    raw = json.loads(STORYBOARD.read_text(encoding="utf-8"))
    by_id = {b["id"]: b for b in raw["beats"]}
    total_before = sb["_total_s"]
    grown = []
    for b in sb["beats"]:
        if not b["subs"]:
            continue
        durs = [clip_duration(VOICE_DIR / f"{line_id(b, i)}.mp3") for i in range(len(b["subs"]))]
        t = b["subs"][0]["t"]
        out = []
        for s, d in zip(b["subs"], durs):
            t = max(t, s["t"])
            # El subtitulo dura lo que dura la voz, con un minimo para que se pueda leer.
            out.append({"t": round(t, 2), "d": round(max(d, 1.6), 2), "text": s["text"]})
            t += d + GAP_S
        need = round(t - GAP_S + 0.6, 1)          # cola despues de la ultima linea
        tgt = by_id[b["id"]]
        tgt["subs"] = out
        if need > b["dur"]:
            grown.append((b["id"], b["dur"], need))
            tgt["dur"] = need
    STORYBOARD.write_text(json.dumps(raw, ensure_ascii=False, indent=2), encoding="utf-8")
    after = sum(b["dur"] for b in raw["beats"])
    for bid, old, new in grown:
        print(f"  {bid:<18} {old:>5.1f}s -> {new:>5.1f}s")
    print(f"duracion {total_before:.1f}s -> {after:.1f}s "
          f"({int(after)//60}:{int(after)%60:02d}), {len(grown)} beats alargados")
    return 0


def cmd_audio():
    """Arma una sola pista WAV con cada linea en su instante exacto.

    Se ensambla en Python en vez de con un filtergraph de ffmpeg: son ~50 clips, y un
    `amix` de 50 entradas es fragil y ilegible comparado con escribir bytes en su offset.
    """
    sb = load()
    total = int(sb["_total_s"] * SR) + SR
    buf = bytearray(total * 2)                      # s16le mono
    placed = 0
    for b in sb["beats"]:
        for i, s in enumerate(b["subs"]):
            mp3 = VOICE_DIR / f"{line_id(b, i)}.mp3"
            if not mp3.is_file():
                print(f"  falta {mp3.name}", file=sys.stderr)
                continue
            pcm = subprocess.run(
                [str(FFMPEG), "-v", "error", "-i", str(mp3), "-f", "s16le",
                 "-acodec", "pcm_s16le", "-ac", "1", "-ar", str(SR), "-"],
                check=True, capture_output=True).stdout
            off = int((b["_start"] + s["t"]) * SR) * 2
            end = min(off + len(pcm), len(buf))
            buf[off:end] = pcm[:end - off]
            placed += 1

    import wave
    with wave.open(str(WAV), "wb") as w:
        w.setnchannels(1)
        w.setsampwidth(2)
        w.setframerate(SR)
        w.writeframes(bytes(buf))
    print(f"{WAV.relative_to(ROOT)} -> {placed} lineas, {len(buf) / 2 / SR:.1f}s")
    return 0


# ---------------------------------------------------------------- composicion

from PIL import Image, ImageChops, ImageDraw, ImageFont  # noqa: E402  (tras las etapas que no lo necesitan)

W, H = 1920, 1080

# Identidad del deck comercial (docs/comercial/deck.template.html, :root). El tutorial y el
# deck son piezas distintas en contenido, pero tienen que verse de la misma familia.
BG = (14, 18, 24)
PANEL = (22, 28, 37)
LINE = (42, 51, 66)
TEXT = (233, 238, 245)
MUTED = (141, 154, 171)
ACCENT = (46, 163, 242)

FONTS = Path("C:/Windows/Fonts")
F_REG, F_SEMI, F_BOLD, F_LIGHT = "segoeui.ttf", "seguisb.ttf", "segoeuib.ttf", "segoeuil.ttf"

# Geometria del split screen. El visor es mas grande a proposito: es el protagonista, la
# tablet esta para explicar de donde viene el cambio.
VIS = (60, 140, 1200, 675)      # x, y, w, h  (16:9)
TAB = (1320, 140, 540, 338)     # x, y, w, h  (16:10, igual que el canvas de la app)
INFO = (1320, 546, 540, 269)    # ficha de estado, debajo de la tablet
SUB_TOP = 900                   # por debajo de esto solo van subtitulos (ver MarginV del .ass)

# Nombres para mostrar. En el video no van los ids internos del catalogo: quien mira es un
# medico, no quien lo configuro.
LENS_NAME = {
    "monofocal": "Monofocal",
    "panoptix": "Multifocal / Trifocal",
    "vivity": "EDOF",
    "paciente_joven": "Paciente joven, sin catarata",
    "catarata": "Catarata",
}
SCEN_NAME = {"consultorio": "Consultorio, de día", "ruta_noche": "Ruta, de noche"}

# Rectangulo del stream DENTRO de la captura de la tablet, normalizado. Medido por codigo
# sobre el RectTransform real (no a ojo): el AspectRatioFitter recorta en vez de encajar, asi
# que el stream desborda verticalmente la pantalla (de -0.10 a 1.10).
STREAM_RECT = (0.0, -0.10, 1.0, 1.10)

# Centro de cada toque, normalizado sobre la captura de la tablet. Salen de volcar los
# RectTransform de los TabletButton reales, no de estimarlos sobre la imagen.
TAP_TARGETS = {
    "ocultar_calce":   (0.7443, 0.0500),
    "escenario_noche": (0.1641, 0.0500),
    "lente_catarata":  (0.5000, 0.3383),
    "lente_monofocal": (0.5000, 0.4233),
    "lente_panoptix":  (0.5000, 0.6783),
    "ojo_od":          (0.5000, 0.7363),
}

_font_cache = {}


def font(name, size):
    key = (name, size)
    if key not in _font_cache:
        _font_cache[key] = ImageFont.truetype(str(FONTS / name), size)
    return _font_cache[key]


def wordmark(d, x, y, size=26):
    """IOL semibold + SIMULATOR regular, sin espacio — regla de marca (docs/tablet.md:200-202)."""
    f1, f2 = font(F_SEMI, size), font(F_LIGHT, size)
    d.text((x, y), "IOL", font=f1, fill=TEXT)
    x += d.textlength("IOL", font=f1)
    d.text((x, y), "SIMULATOR", font=f2, fill=MUTED)


def chrome(beat_label, scenario, lens):
    """Marco estatico del split screen. Se compone una vez por beat, no por frame."""
    img = Image.new("RGB", (W, H), BG)
    d = ImageDraw.Draw(img)
    wordmark(d, 60, 46)
    if beat_label:
        f = font(F_SEMI, 22)
        tw = d.textlength(beat_label, font=f)
        d.rounded_rectangle((W - 60 - tw - 32, 44, W - 60, 82), 8, fill=PANEL, outline=LINE)
        d.text((W - 60 - tw - 16, 50), beat_label, font=f, fill=ACCENT)
    d.line((60, 104, W - 60, 104), fill=LINE, width=1)

    for (x, y, w, h), label in ((VIS, "Lo que ve el paciente"), (TAB, "Lo que hacés vos")):
        d.text((x, y - 34), label, font=font(F_SEMI, 21), fill=MUTED)
        d.rectangle((x - 1, y - 1, x + w, y + h), outline=LINE)
    info_card(d, scenario, lens)
    return img


def info_card(d, scenario, lens):
    """Ficha de estado: escenario y lente por ojo. Sin esto, quien mira el video pierde de
    vista cual de las lentes esta puesta justo cuando aparece el efecto que las distingue."""
    x, y, w, h = INFO
    d.rounded_rectangle((x, y, x + w, y + h), 10, fill=PANEL, outline=LINE)
    d.text((x + 24, y + 20), "EN ESTE MOMENTO", font=font(F_SEMI, 17), fill=ACCENT)
    fk, fv = font(F_REG, 20), font(F_SEMI, 25)

    rows = [("Escenario", SCEN_NAME.get(scenario, scenario))]
    if lens[0] == lens[1]:
        rows.append(("Ambos ojos", LENS_NAME.get(lens[0], lens[0])))
    else:
        rows.append(("Ojo derecho", LENS_NAME.get(lens[1], lens[1])))
        rows.append(("Ojo izquierdo", LENS_NAME.get(lens[0], lens[0])))

    ry = y + 60
    for k, v in rows:
        d.text((x + 24, ry), k, font=fk, fill=MUTED)
        d.text((x + 24, ry + 26), v, font=fv, fill=TEXT)
        ry += 68


def card(title, sub):
    """Placa de titulo / cierre: sin split screen, solo texto centrado."""
    img = Image.new("RGB", (W, H), BG)
    d = ImageDraw.Draw(img)
    ft, fs = font(F_LIGHT, 82), font(F_REG, 34)
    d.text((W / 2, 400), title, font=ft, fill=TEXT, anchor="mm")
    d.line((W / 2 - 90, 478, W / 2 + 90, 478), fill=ACCENT, width=3)
    d.text((W / 2, 540), sub, font=fs, fill=MUTED, anchor="mm")
    wordmark(d, 60, 46)
    return img


def paste_fit(img, src, box):
    """Pega `src` dentro de `box` conservando proporcion (letterbox si hace falta)."""
    x, y, w, h = box
    sw, sh = src.size
    k = min(w / sw, h / sh)
    nw, nh = int(sw * k), int(sh * k)
    img.paste(src.resize((nw, nh), Image.LANCZOS), (x + (w - nw) // 2, y + (h - nh) // 2))


def stream_cover(frame, size):
    """Escala el frame del visor para CUBRIR el rectangulo del stream, recortando el sobrante.

    Recorta en vez de encajar porque es lo que hace el AspectRatioFitter de la app.
    """
    sw, sh = size
    x0, y0, x1, y1 = STREAM_RECT
    bw, bh = (x1 - x0) * sw, (y1 - y0) * sh
    k = max(bw / frame.width, bh / frame.height)
    scaled = frame.resize((max(1, round(frame.width * k)), max(1, round(frame.height * k))),
                          Image.LANCZOS)
    layer = Image.new("RGB", (sw, sh), (0, 0, 0))
    layer.paste(scaled, (round(x0 * sw + (bw - scaled.width) / 2),
                         round(y0 * sh + (bh - scaled.height) / 2)))
    return layer


def load_chrome(name, cache):
    """Separa la interfaz de la tablet del stream que tiene detras.

    La barra superior y el carrusel inferior de la app son SEMITRANSPARENTES: se ve el
    escenario a traves de ellos. Si uno pega el frame del visor solo en una banda "segura"
    para no taparlos, esas zonas quedan congeladas con el escenario del momento de la
    captura -- que es justo lo que se veia mal (los apliques de luz de arriba y el fondo
    de los botones de abajo, quietos mientras el resto se movia).

    Solucion: cada panel se captura DOS veces, con el stream forzado a negro y a blanco.
    Con eso el matte sale exacto, sin estimar nada:

        sobre negro:  N = a*C          (color premultiplicado por su alfa)
        sobre blanco: B = a*C + (1-a)  (el mismo, mas el fondo que se cuela)
        =>  (1-a) = B - N     y      composicion sobre cualquier fondo F:  N + (1-a)*F

    Asi la interfaz queda intacta y TODO el stream, de punta a punta, es el frame vivo.
    """
    if name in cache:
        return cache[name]
    black = TABLET_DIR / name.replace(".png", "_k.png")
    white = TABLET_DIR / name.replace(".png", "_w.png")
    if not (black.is_file() and white.is_file()):
        cache[name] = None          # sin el par, se cae al panel plano de siempre
        return None
    n = Image.open(black).convert("RGB")
    b = Image.open(white).convert("RGB")
    # inv = 1-alfa, por canal. Se clampea porque el JPG/PNG y el dithering de la UI
    # meten ruido de un par de niveles y (1-a) tiene que quedar en [0,1].
    inv = Image.eval(ImageChops.subtract(b, n), lambda v: v)
    cache[name] = (n, inv)
    return cache[name]


def paste_stream(name, frame, cache):
    """Panel de la tablet con el stream vivo debajo de su interfaz."""
    mat = load_chrome(name, cache)
    if mat is None:
        return None
    premult, inv = mat
    live = stream_cover(frame, premult.size)
    return ImageChops.add(premult, ImageChops.multiply(inv, live))


def draw_tap(img, box, nx, ny, age):
    """Toque: un punto y un anillo que se expande y se desvanece. `age` en segundos."""
    if age < 0 or age > 0.9:
        return
    x, y, w, h = box
    cx, cy = x + nx * w, y + ny * h
    ov = Image.new("RGBA", img.size, (0, 0, 0, 0))
    d = ImageDraw.Draw(ov)
    r = 14 + 52 * (age / 0.9)
    a = int(210 * (1 - age / 0.9))
    d.ellipse((cx - r, cy - r, cx + r, cy + r), outline=ACCENT + (a,), width=3)
    d.ellipse((cx - 11, cy - 11, cx + 11, cy + 11), fill=ACCENT + (min(235, a + 90),))
    img.paste(Image.alpha_composite(img.convert("RGBA"), ov).convert("RGB"), (0, 0))


def compose_frame(sb, f, cache):
    """Un frame compuesto, 1920x1080 RGB."""
    beat = cache["beat_of"][f]
    t = (f - beat["_frame0"]) / sb["fps"]

    if not beat.get("render", True):
        c = beat.get("card") or {}
        base = cache.get("card_" + beat["id"])
        if base is None:
            base = card(c.get("title", ""), c.get("sub", ""))
            cache["card_" + beat["id"]] = base
        return base.copy()

    base = cache.get("chrome_" + beat["id"])
    if base is None:
        base = chrome(cache["labels"].get(beat["id"], ""),
                      cache["state"][beat["id"]][0], cache["state"][beat["id"]][1])
        cache["chrome_" + beat["id"]] = base
    img = base.copy()

    vis = VISOR_DIR / f"{f:06d}.jpg"
    if vis.is_file():
        frame = Image.open(vis).convert("RGB")
    else:
        frame = Image.new("RGB", (16, 9), (30, 38, 50))   # falta el render: placeholder
    paste_fit(img, frame, VIS)

    name = beat.get("tablet")
    if name:
        shot = cache["tablet"].get(name)
        if shot is None:
            p = TABLET_DIR / name
            shot = Image.open(p).convert("RGB") if p.is_file() else Image.new(
                "RGB", (1280, 800), PANEL)
            cache["tablet"][name] = shot
        if beat.get("stream", True):
            live = paste_stream(name, frame, cache["chrome"])
            if live is not None:
                shot = live
        paste_fit(img, shot, TAB)

    tap = beat.get("tap")
    if tap and tap.get("target") in TAP_TARGETS:
        nx, ny = TAP_TARGETS[tap["target"]]
        draw_tap(img, TAB, nx, ny, t - tap["t"])
    return img


def build_cache(sb):
    beat_of = {}
    state = {}
    lens = ("paciente_joven", "paciente_joven")
    scenario = "consultorio"
    for b in sb["beats"]:
        lens = resolve_lens(b, lens)
        scenario = b.get("scenario", scenario)
        state[b["id"]] = (scenario, lens)
        for f in range(b["_frame0"], b["_frame1"]):
            beat_of[f] = b
    labels = {b["id"]: b.get("chip", "") for b in sb["beats"]}
    return {"beat_of": beat_of, "labels": labels, "state": state,
            "tablet": {}, "chrome": {}}


def cmd_preview():
    """Compone frames sueltos a PNG para revisarlos sin encodear 6.500."""
    sb = load()
    cache = build_cache(sb)
    OUT.mkdir(parents=True, exist_ok=True)
    frames = [int(x) for x in sys.argv[2:]] or [
        b["_frame0"] + (b["_frame1"] - b["_frame0"]) // 2 for b in sb["beats"]]
    for f in frames:
        if f not in cache["beat_of"]:
            print(f"frame {f} fuera de rango"); continue
        p = OUT / f"preview_{f:06d}_{cache['beat_of'][f]['id']}.png"
        compose_frame(sb, f, cache).save(p)
        print(p.relative_to(ROOT))
    return 0


def cmd_compose():
    sb = load()
    cache = build_cache(sb)
    OUT.mkdir(parents=True, exist_ok=True)
    cmd_subs()
    cmd_audio()

    cmd = [
        str(FFMPEG), "-y", "-loglevel", "warning",   # su progreso tapa el nuestro
        "-f", "rawvideo", "-pix_fmt", "rgb24", "-s", f"{W}x{H}",
        "-framerate", str(sb["fps"]), "-i", "-",
        "-i", str(WAV),
        "-vf", f"subtitles={ASS.name}",
        "-c:v", "libx264", "-preset", "slow", "-crf", "20", "-pix_fmt", "yuv420p",
        "-c:a", "aac", "-b:a", "128k", "-shortest",
        "-movflags", "+faststart", str(MP4),
    ]
    # cwd en OUT para que el filtro `subtitles` reciba un nombre relativo: en Windows la
    # ruta absoluta lleva `C:` y los dos puntos rompen el parser de filtros de ffmpeg.
    proc = subprocess.Popen(cmd, stdin=subprocess.PIPE, cwd=str(OUT))
    total = sb["_frames"]
    try:
        for f in range(total):
            proc.stdin.write(compose_frame(sb, f, cache).tobytes())
            if f % 300 == 0:
                print(f"  {f}/{total}  {cache['beat_of'][f]['id']}", flush=True)
    finally:
        proc.stdin.close()
        rc = proc.wait()
    if rc != 0:
        print("ffmpeg fallo", file=sys.stderr)
        return rc
    print(f"{MP4.relative_to(ROOT)} -> {MP4.stat().st_size / 1e6:.1f} MB")
    return 0


# ---------------------------------------------------------------- CLI

def cmd_smoke():
    """Plan reducido: un puñado de estados representativos, para validar el arnes en segundos.

    Correr los 5.940 frames para descubrir que el libro apunta al reves es carisimo. Esto
    escribe el MISMO plan.tsv que lee el arnes, con ~10 estados repetidos 8 frames cada uno
    (8 y no 1: al cambiar de lente o de escenario hace falta mas de un frame para que se
    asiente, y con un solo frame la captura muestra el estado anterior). Conserva los numeros
    de frame originales, asi los .jpg caen con el nombre que les corresponde en el plan real.

    Despues de revisar: volver a correr `plan` para restaurar el plan completo.
    """
    cmd_plan()
    lines = PLAN.read_text(encoding="utf-8").splitlines()
    head = [l for l in lines if l.startswith("#")]
    colhdr = next(l for l in lines if l.startswith("frame\t"))
    rows = [l.split("\t") for l in lines if not l.startswith(("#", "frame\t"))]
    by = {}
    for r in rows:
        by.setdefault(r[10], []).append(r)

    def pick(beat, key=None):
        rs = by.get(beat)
        if not rs:
            return None
        return rs[len(rs) // 2] if key is None else min(rs, key=key)

    yaw_min = lambda r: float(r[6])          # noqa: E731
    yaw_max = lambda r: -float(r[6])         # noqa: E731
    near = lambda r: float(r[8]) if r[8] != "-" else 9e9    # noqa: E731
    far = lambda r: -(float(r[8]) if r[8] != "-" else 0)    # noqa: E731

    sel = [pick(*a) for a in (
        ("paso1_calce",),                    # calce visible: valida el gate de oclusion
        ("calce_oculto",),                   # escena restaurada tras la oclusion
        ("mirar_costados", yaw_min),         # yaw extremo: valida que rotar XR Origin sirve
        ("mirar_costados", yaw_max),
        ("catarata",),
        ("libro_acerca", far),               # libro lejos
        ("libro_acerca", near),              # libro cerca: el desenfoque tiene que verse
        ("libro_panoptix", near),            # misma distancia, otra lente
        ("noche_panoptix", yaw_min),         # noche + giro: halos sobre los faros
        ("blend",),                          # una lente por ojo
    )]
    sel = [r for r in sel if r]

    out = head + [colhdr]
    for r in sel:
        out += ["\t".join(r)] * 8
    PLAN.write_text("\n".join(out) + "\n", encoding="utf-8")
    print(f"\nplan de smoke: {len(sel)} estados x8 = {len(sel) * 8} frames")
    for r in sel:
        print(f"  f{r[0]:>5} {r[10]:<16} {r[2]:<12} L={r[3]:<12} R={r[4]:<12} "
              f"calce={r[5]} yaw={r[6]:>7} pitch={r[7]:>6} libro={r[8]} tilt={r[9]}")
    return 0


COMMANDS = {"plan": cmd_plan, "smoke": cmd_smoke, "subs": cmd_subs,
            "voice": cmd_voice, "retime": cmd_retime, "audio": cmd_audio,
            "preview": cmd_preview, "compose": cmd_compose}


def main(argv) -> int:
    if len(argv) < 2 or argv[1] not in COMMANDS:
        print(__doc__)
        print("Comandos:", ", ".join(COMMANDS))
        return 2
    return COMMANDS[argv[1]]()


if __name__ == "__main__":
    raise SystemExit(main(sys.argv))
