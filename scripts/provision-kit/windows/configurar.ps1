<#
.SYNOPSIS
    IOLSIMULATOR - Configurador de tablets (kit para clinicas, Windows).

.DESCRIPTION
    Port fiel para Windows PowerShell 5.1 de scripts/provision-tablet.sh (el
    procedimiento de referencia, con todos los gotchas documentados, vive en
    docs/builds-deploy.md "Provision de tablets (Device Owner)"). Pensado para
    que un MEDICO sin conocimientos tecnicos, en su propia PC con Windows,
    deje una tablet lista como Device Owner (modo kiosco) con un solo doble
    clic en "Configurar tablet.bat" -- sin Git Bash, sin adb instalado de
    antemano y sin tener que tocar ninguna opcion.

    Que hace (mismo orden que provision-tablet.sh):
      1. Resuelve 'adb': PATH, rutas conocidas del SDK, o descarga
         platform-tools OFICIAL de Google a la carpeta 'herramientas' junto a
         este script (no redistribuimos el binario en el kit).
      2. Descarga y verifica (SHA256) el APK de la tablet publicado en el
         backend (mismo manifest que usa el OTA, ver docs/updates.md).
      3. Busca la tablet conectada por USB (con checklist en pantalla si
         tarda, y aviso si hay mas de una conectada a la vez).
      4. Verifica que no tenga cuentas configuradas y quita el bloqueo de
         pantalla de fabrica (si no es seguro).
      5. Instala el APK.
      6. Configura el modo kiosco (dpm set-device-owner) -- si la tablet ya
         estaba configurada, lo informa y sigue sin error.
      7. Lanza la app como HOME explicito y confirma que el kiosco quedo
         activo.
      8. Reinicia la tablet y confirma en vivo que arranca directo en el
         simulador, en foco y con el kiosco (lock task) activo -- exactamente
         lo que le va a pasar al paciente/clinico al prenderla.

    Cada paso que falla corta la ejecucion con un mensaje en espanol, sin
    jerga tecnica, explicando que hacer. Se guarda un registro completo
    (con la salida cruda de adb) en 'registro-AAAAMMDD-HHMMSS.txt' junto a
    este script, para mandar a soporte si hace falta.

.NOTES
    Requiere Windows PowerShell 5.1 (el que ya viene instalado en Windows 10
    y 11) -- NO requiere PowerShell 7. Por eso este script evita a proposito
    sintaxis que 5.1 no entiende (operador ternario "?:", "??", encadenado
    "&&"/"||" de comandos, "ForEach-Object -Parallel", etc.).
#>

param(
    # Backend del que se descarga el manifest/APK de la tablet. Por defecto,
    # el de produccion (mismo default que scripts/provision-tablet.sh). Un
    # tecnico de soporte puede pasar otro con:
    #   powershell -NoProfile -ExecutionPolicy Bypass -File configurar.ps1 -BackendUrl "https://otro"
    [string]$BackendUrl = 'https://vr.conecta.sh'
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'   # Invoke-WebRequest es muchisimo mas lento con la barra de progreso

try {
    [Console]::OutputEncoding = [System.Text.Encoding]::UTF8
} catch {
    # SIM: atajo deliberado -- si esto falla (consola no interactiva/redirigida)
    # seguimos igual: solo afecta como se ven los acentos, no la logica.
}

# Forzar TLS 1.2: el .NET Framework que trae Windows PowerShell 5.1 por
# defecto en muchas instalaciones de Windows 10/11 no habilita TLS 1.2 solo,
# y tanto el backend como dl.google.com lo exigen.
[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12

# -----------------------------------------------------------------------
# Constantes (mismos valores que scripts/provision-tablet.sh)
# -----------------------------------------------------------------------
$TotalSteps = 8
$Package = 'com.simulador.tablet'
$Receiver = 'com.simulador.kiosk.SimuladorDeviceAdminReceiver'
$AdminComponent = "$Package/$Receiver"
$UnityActivity = 'com.unity3d.player.UnityPlayerGameActivity'
$AppActivity = "$Package/$UnityActivity"
$AppLabel = 'IOLSIMULATOR Tablet'
$PlatformToolsUrl = 'https://dl.google.com/android/repository/platform-tools-latest-windows.zip'

$ScriptDir = $PSScriptRoot
$ToolsDir = Join-Path $ScriptDir 'herramientas'
$DownloadDir = Join-Path $env:TEMP 'iolsimulator-tablet'
if (-not (Test-Path -LiteralPath $DownloadDir)) {
    New-Item -ItemType Directory -Path $DownloadDir -Force | Out-Null
}
$LogPath = Join-Path $ScriptDir ('registro-{0}.txt' -f (Get-Date -Format 'yyyyMMdd-HHmmss'))

$Serial = $null
$AdbPath = $null

# -----------------------------------------------------------------------
# Utilidades de salida: consola (con colores) + registro en disco (texto
# plano, con la salida cruda de adb) para poder mandarlo a soporte.
# -----------------------------------------------------------------------
function Write-Log {
    param([string]$Message)
    $timestamp = Get-Date -Format 'yyyy-MM-dd HH:mm:ss'
    "$timestamp  $Message" | Out-File -LiteralPath $LogPath -Append -Encoding utf8
}

function Write-Step {
    param([int]$Number, [int]$Total, [string]$Text)
    Write-Host ''
    Write-Host ('--- Paso {0} de {1}: {2} ---' -f $Number, $Total, $Text) -ForegroundColor Cyan
    Write-Log ('PASO {0}/{1}: {2}' -f $Number, $Total, $Text)
}

function Write-Info {
    param([string]$Text)
    Write-Host "  $Text"
    Write-Log "  $Text"
}

function Write-Success {
    param([string]$Text)
    Write-Host "  $Text" -ForegroundColor Green
    Write-Log "  $Text"
}

function Exit-Fatal {
    param([string]$Message)
    Write-Log "FALLO: $Message"
    Write-Host ''
    Write-Host '============================================' -ForegroundColor Red
    Write-Host '  NO SE PUDO TERMINAR' -ForegroundColor Red
    Write-Host '============================================' -ForegroundColor Red
    Write-Host $Message -ForegroundColor Red
    Write-Host ''
    Write-Host 'Se guardo un registro detallado de todo lo que paso en:' -ForegroundColor Yellow
    Write-Host "  $LogPath" -ForegroundColor Yellow
    Write-Host 'Si necesitas ayuda, mandale ese archivo a soporte.' -ForegroundColor Yellow
    exit 1
}

function Show-Welcome {
    Write-Host ''
    Write-Host '================================================' -ForegroundColor Cyan
    Write-Host '   IOLSIMULATOR - Configurador de tablets' -ForegroundColor Cyan
    Write-Host '================================================' -ForegroundColor Cyan
    Write-Host ''
    Write-Host 'Este programa deja la tablet lista para usar con el simulador,'
    Write-Host 'en modo kiosco (arranca directo en la app, sin poder salir).'
    Write-Host ''
    Write-Host 'Antes de continuar:'
    Write-Host '  - Conecta la tablet a esta PC con el cable USB (de datos).'
    Write-Host '  - Esta PC necesita conexion a Internet.'
    Write-Host '  - Si es la primera vez, segui la guia impresa paso a paso.'
    Write-Host ''
    Read-Host 'Presiona Enter para comenzar'
    Write-Log '=== Inicio de la ejecucion ==='
}

# -----------------------------------------------------------------------
# adb: ejecuta un comando con el -s <serial> resuelto (si ya lo tenemos) y
# guarda la orden + la salida cruda (stdout+stderr) en el registro, igual
# que 'set_owner_output="$(... 2>&1 | tr -d "\r")"' del script bash.
# -----------------------------------------------------------------------
function Invoke-Adb {
    param(
        [Parameter(Mandatory = $true)][string[]]$Arguments,
        [switch]$NoSerial
    )

    $fullArgs = @()
    if (-not $NoSerial -and $Serial) {
        $fullArgs += @('-s', $Serial)
    }
    $fullArgs += $Arguments

    Write-Log ('adb ' + ($fullArgs -join ' '))

    $rawLines = & $AdbPath @fullArgs 2>&1
    $exitCode = $LASTEXITCODE

    $textLines = @()
    foreach ($line in $rawLines) {
        if ($null -ne $line) { $textLines += $line.ToString() }
    }
    $text = $textLines -join "`r`n"

    if ($text) { Write-Log $text }
    Write-Log "(exit code: $exitCode)"

    return [PSCustomObject]@{
        ExitCode = $exitCode
        Output   = $text
    }
}

function Get-AdbDevices {
    $result = Invoke-Adb -Arguments @('devices') -NoSerial
    $devices = @()
    $lines = $result.Output -split "`r`n"
    foreach ($line in $lines) {
        if (-not $line) { continue }
        if ($line -match '^List of devices attached') { continue }
        $parts = $line -split '\s+', 2
        if ($parts.Count -eq 2) {
            $devices += [PSCustomObject]@{ Serial = $parts[0].Trim(); State = $parts[1].Trim() }
        }
    }
    return $devices
}

# -----------------------------------------------------------------------
# Paso 1: resolver adb -- PATH, rutas conocidas del SDK, o descargar
# platform-tools oficial de Google a 'herramientas\' junto a este script
# (no redistribuimos el binario dentro del kit).
# -----------------------------------------------------------------------
function Resolve-AdbBinary {
    Write-Step 1 $TotalSteps 'Preparando las herramientas de conexion (adb)...'

    $candidates = @()

    $inPath = Get-Command 'adb.exe' -ErrorAction SilentlyContinue
    if ($inPath) { $candidates += $inPath.Source }

    if ($env:LOCALAPPDATA) {
        $candidates += (Join-Path $env:LOCALAPPDATA 'Android\Sdk\platform-tools\adb.exe')
    }
    $candidates += 'C:\Android\platform-tools\adb.exe'
    if ($env:USERPROFILE) {
        $candidates += (Join-Path $env:USERPROFILE 'Android\Sdk\platform-tools\adb.exe')
    }
    # Descarga previa de este mismo kit (para no re-descargar en cada corrida).
    $candidates += (Join-Path $ToolsDir 'platform-tools\adb.exe')

    foreach ($candidate in $candidates) {
        if ($candidate -and (Test-Path -LiteralPath $candidate -PathType Leaf)) {
            Write-Info "Usando adb en: $candidate"
            return $candidate
        }
    }

    Write-Info 'No se encontraron las herramientas de Android en esta PC. Descargandolas desde Google (puede tardar un par de minutos)...'
    if (-not (Test-Path -LiteralPath $ToolsDir)) {
        New-Item -ItemType Directory -Path $ToolsDir -Force | Out-Null
    }
    $zipPath = Join-Path $ToolsDir 'platform-tools.zip'

    try {
        Invoke-WebRequest -Uri $PlatformToolsUrl -OutFile $zipPath -UseBasicParsing -TimeoutSec 180
    } catch {
        Exit-Fatal "No se pudieron descargar las herramientas de Android desde Internet ($PlatformToolsUrl). Revisa la conexion a Internet de esta PC e intenta de nuevo.`nDetalle tecnico: $($_.Exception.Message)"
    }

    try {
        Expand-Archive -LiteralPath $zipPath -DestinationPath $ToolsDir -Force
    } catch {
        Exit-Fatal "Se descargaron las herramientas de Android pero no se pudieron descomprimir. Detalle tecnico: $($_.Exception.Message)"
    }
    Remove-Item -LiteralPath $zipPath -Force -ErrorAction SilentlyContinue

    $downloadedAdb = Join-Path $ToolsDir 'platform-tools\adb.exe'
    if (-not (Test-Path -LiteralPath $downloadedAdb -PathType Leaf)) {
        Exit-Fatal "Se descomprimieron las herramientas de Android pero no aparecio 'adb.exe' en la ruta esperada ($downloadedAdb). Avisa a soporte."
    }

    Write-Info 'Herramientas de Android descargadas correctamente.'
    return $downloadedAdb
}

# -----------------------------------------------------------------------
# Paso 2: descargar y verificar (SHA256) el APK de la tablet publicado en
# el backend -- mismo contrato de manifest que el OTA (docs/updates.md).
# -----------------------------------------------------------------------
function Get-TabletApk {
    param([string]$Backend)

    Write-Step 2 $TotalSteps 'Descargando la aplicacion del simulador...'

    $manifestUrl = '{0}/api/manifest.json?app=tablet' -f $Backend.TrimEnd('/')
    Write-Info 'Consultando la version disponible...'

    try {
        $response = Invoke-WebRequest -Uri $manifestUrl -UseBasicParsing -TimeoutSec 30
    } catch {
        $statusCode = $null
        if ($_.Exception.Response) {
            try { $statusCode = [int]$_.Exception.Response.StatusCode } catch { $statusCode = $null }
        }
        if ($statusCode -eq 503) {
            Exit-Fatal 'El sistema todavia no tiene ninguna version de la app lista para descargar. Avisa a soporte.'
        }
        Exit-Fatal "No se pudo conectar a Internet para descargar la app ($manifestUrl). Revisa que esta PC tenga conexion a Internet e intenta de nuevo.`nDetalle tecnico: $($_.Exception.Message)"
    }

    try {
        $manifest = $response.Content | ConvertFrom-Json
    } catch {
        Exit-Fatal 'La respuesta del sistema no se pudo interpretar. Avisa a soporte con el registro de esta ejecucion.'
    }

    if (-not $manifest.apk_version -or -not $manifest.apk_url -or -not $manifest.apk_sha256) {
        Exit-Fatal 'La respuesta del sistema no trae los datos esperados de la app. Avisa a soporte con el registro de esta ejecucion.'
    }

    Write-Info "Version disponible: $($manifest.apk_version)"

    $apkPath = Join-Path $DownloadDir ('simulador-tablet-{0}.apk' -f $manifest.apk_version)
    Write-Info 'Descargando (puede tardar unos minutos segun tu conexion)...'
    try {
        Invoke-WebRequest -Uri $manifest.apk_url -OutFile $apkPath -UseBasicParsing -TimeoutSec 600
    } catch {
        Exit-Fatal "La descarga de la app fallo. Revisa la conexion a Internet e intenta de nuevo.`nDetalle tecnico: $($_.Exception.Message)"
    }

    Write-Info 'Verificando que la descarga este completa y sin errores...'
    $actualHash = (Get-FileHash -LiteralPath $apkPath -Algorithm SHA256).Hash
    if ($actualHash.ToLowerInvariant() -ne $manifest.apk_sha256.ToLowerInvariant()) {
        Remove-Item -LiteralPath $apkPath -Force -ErrorAction SilentlyContinue
        Exit-Fatal 'La aplicacion descargada no paso la verificacion de integridad (puede haberse cortado la descarga a mitad de camino). Volve a intentar; si el problema persiste, avisa a soporte.'
    }

    Write-Info 'Descarga verificada correctamente.'
    return [PSCustomObject]@{ Path = $apkPath; Version = $manifest.apk_version }
}

# -----------------------------------------------------------------------
# Paso 3: buscar la tablet conectada -- checklist en pantalla si tarda,
# aviso si hay mas de una conectada a la vez (a diferencia del script bash,
# ese medico no tiene forma de pasar --serial: le pedimos que deje UNA
# sola tablet conectada).
# -----------------------------------------------------------------------
function Wait-ForDevice {
    Write-Step 3 $TotalSteps 'Buscando la tablet conectada...'

    $elapsed = 0
    $interval = 2
    $maxWait = 300
    $checklistShown = $false
    $unauthorizedWarned = $false

    while ($true) {
        $devices = Get-AdbDevices
        $authorized = @($devices | Where-Object { $_.State -eq 'device' })
        $unauthorized = @($devices | Where-Object { $_.State -eq 'unauthorized' })

        if ($authorized.Count -gt 1) {
            Exit-Fatal "Hay $($authorized.Count) tablets conectadas a esta PC al mismo tiempo. Desconecta todas menos UNA (la que vas a configurar) y volve a ejecutar este programa."
        }

        if ($authorized.Count -eq 1) {
            $script:Serial = $authorized[0].Serial
            Write-Info "Tablet encontrada (serie $Serial)."
            return
        }

        if ($unauthorized.Count -gt 0 -and -not $unauthorizedWarned) {
            $unauthorizedWarned = $true
            Write-Info 'En la tablet aparecio un cartel: "Permitir depuracion USB?". Marca "Permitir siempre desde esta computadora" y toca Permitir.'
        }

        if (-not $checklistShown -and $elapsed -ge 15) {
            $checklistShown = $true
            Write-Info 'No se detecta ninguna tablet todavia. Revisa lo siguiente:'
            Write-Info '  1. Si la tablet no esta de fabrica, restablecela de fabrica primero.'
            Write-Info '  2. En el asistente inicial de Android: SALTEA el inicio de sesion, sin agregar ninguna cuenta.'
            Write-Info '  3. Ajustes > Acerca de la tablet > toca 7 veces "Numero de compilacion" (activa Opciones de desarrollador).'
            Write-Info '  4. Ajustes > Sistema > Opciones de desarrollador > activa "Depuracion USB".'
            Write-Info '  5. Conecta el cable USB de DATOS (no solo de carga) a esta PC y acepta el cartel en la tablet.'
            Write-Info 'Reintentando cada pocos segundos (hasta 5 minutos en total)...'
        }

        if ($elapsed -ge $maxWait) {
            Exit-Fatal 'No se detecto ninguna tablet en 5 minutos. Revisa el cable (que sea de datos, no solo de carga), que la tablet este encendida y desbloqueada, y los pasos de la guia impresa.'
        }

        Start-Sleep -Seconds $interval
        $elapsed += $interval
    }
}

# -----------------------------------------------------------------------
# Paso 4: cuentas + bloqueo de pantalla (mismos gotchas que
# provision-tablet.sh -- ver docs/builds-deploy.md).
# -----------------------------------------------------------------------
function Confirm-DeviceReady {
    Write-Step 4 $TotalSteps 'Verificando que la tablet este lista...'

    Write-Info 'Revisando que no tenga ninguna cuenta configurada...'
    $accountsResult = Invoke-Adb -Arguments @('shell', 'dumpsys', 'account')
    $accountCount = ([regex]::Matches($accountsResult.Output, 'Account \{')).Count
    if ($accountCount -gt 0) {
        Exit-Fatal "La tablet tiene una cuenta configurada (por ejemplo, una cuenta de Google). Para continuar hay que restablecerla de fabrica y NO iniciar sesion con ninguna cuenta durante la configuracion inicial (ver la guia impresa, seccion 'Preparar la tablet'). Despues, volve a ejecutar este programa."
    }

    Write-Info 'Quitando el bloqueo de pantalla de fabrica (si lo tiene)...'
    Invoke-Adb -Arguments @('shell', 'locksettings', 'set-disabled', 'true') | Out-Null
    $disabledResult = Invoke-Adb -Arguments @('shell', 'locksettings', 'get-disabled')
    $isDisabled = $disabledResult.Output.Trim()
    if ($isDisabled -notmatch 'true') {
        Exit-Fatal 'La tablet tiene un bloqueo de pantalla CON SEGURIDAD (PIN, patron o contrasena) y no se puede quitar automaticamente. Quitalo a mano: Ajustes > Seguridad > Bloqueo de pantalla > Ninguno, y volve a ejecutar este programa.'
    }

    Write-Info 'Manteniendo la pantalla despierta durante el resto del proceso...'
    Invoke-Adb -Arguments @('shell', 'svc', 'power', 'stayon', 'true') | Out-Null
    Invoke-Adb -Arguments @('shell', 'input', 'keyevent', 'KEYCODE_WAKEUP') | Out-Null
    Invoke-Adb -Arguments @('shell', 'wm', 'dismiss-keyguard') | Out-Null
}

# -----------------------------------------------------------------------
# Paso 5: instalar el APK.
# -----------------------------------------------------------------------
function Install-TabletApk {
    param([string]$ApkPath)

    Write-Step 5 $TotalSteps 'Instalando la aplicacion en la tablet...'
    $result = Invoke-Adb -Arguments @('install', '-r', $ApkPath)
    if ($result.ExitCode -ne 0 -or $result.Output -match 'Failure') {
        Exit-Fatal "No se pudo instalar la aplicacion en la tablet.`nDetalle tecnico: $($result.Output)"
    }
    Write-Info 'Aplicacion instalada correctamente.'
}

# -----------------------------------------------------------------------
# Paso 6: modo kiosco (Device Owner). Si la tablet YA esta configurada (por
# ejemplo, se volvio a correr el programa) lo informamos y seguimos sin
# tratarlo como error. Gotcha "already provisioned" (ver
# docs/builds-deploy.md): se aplica el mismo truco sin root que usa
# provision-tablet.sh --fix-setup, automaticamente, sin que el medico tenga
# que saber que existe.
# -----------------------------------------------------------------------
function Set-KioskDeviceOwner {
    Write-Step 6 $TotalSteps 'Configurando el modo kiosco...'

    $policyResult = Invoke-Adb -Arguments @('shell', 'dumpsys', 'device_policy')
    if ($policyResult.Output -match [regex]::Escape($AdminComponent)) {
        Write-Info 'Esta tablet ya estaba configurada. Seguimos para confirmar que todo este en orden...'
        return
    }

    $result = Invoke-Adb -Arguments @('shell', 'dpm', 'set-device-owner', $AdminComponent)
    if ($result.ExitCode -eq 0) {
        Write-Info 'Modo kiosco configurado correctamente.'
        return
    }

    if ($result.Output -match '(?i)already provisioned') {
        Write-Info 'Aplicando un ajuste adicional que necesita esta tablet...'
        Invoke-Adb -Arguments @('shell', 'settings', 'put', 'global', 'device_provisioned', '0') | Out-Null
        Invoke-Adb -Arguments @('shell', 'content', 'insert', '--uri', 'content://settings/secure', '--bind', 'name:s:user_setup_complete', '--bind', 'value:s:0') | Out-Null

        $retryResult = Invoke-Adb -Arguments @('shell', 'dpm', 'set-device-owner', $AdminComponent)
        if ($retryResult.ExitCode -eq 0) {
            Write-Info 'Modo kiosco configurado correctamente.'
            return
        }
        Exit-Fatal "No se pudo configurar el modo kiosco en esta tablet. Puede hacer falta restablecerla de fabrica (ver la guia impresa, seccion 'Problemas frecuentes').`nDetalle tecnico: $($retryResult.Output)"
    }

    if ($result.Output -match '(?i)(already.*device owner|device owner.*already)') {
        Exit-Fatal "Esta tablet ya tiene OTRA aplicacion configurada como administrador del dispositivo. Para usarla con el simulador hay que restablecerla de fabrica primero (ver la guia impresa, seccion 'Preparar la tablet') y despues volver a ejecutar este programa."
    }

    Exit-Fatal "No se pudo configurar el modo kiosco en esta tablet.`nDetalle tecnico: $($result.Output)"
}

# -----------------------------------------------------------------------
# Paso 7: appops + dialogo de modo inmersivo + lanzar como HOME explicito
# (gotcha de la carrera tarea-standard, ver docs/builds-deploy.md) +
# verificar HOME persistente y tarea type=home.
# -----------------------------------------------------------------------
function Start-KioskAppAndVerify {
    Write-Step 7 $TotalSteps 'Iniciando la aplicacion y confirmando el modo kiosco...'

    Invoke-Adb -Arguments @('shell', 'appops', 'set', $Package, 'REQUEST_INSTALL_PACKAGES', 'allow') | Out-Null
    Invoke-Adb -Arguments @('shell', 'settings', 'put', 'secure', 'immersive_mode_confirmations', 'confirmed') | Out-Null

    Invoke-Adb -Arguments @('shell', 'am', 'start', '-a', 'android.intent.action.MAIN', '-c', 'android.intent.category.HOME', '-n', $AppActivity) | Out-Null

    Write-Info 'Esperando a que la aplicacion quede como pantalla de inicio (hasta 30 segundos)...'
    $waited = 0
    $resolved = ''
    while ($waited -lt 30) {
        $resolveResult = Invoke-Adb -Arguments @('shell', 'cmd', 'package', 'resolve-activity', '-a', 'android.intent.action.MAIN', '-c', 'android.intent.category.HOME')
        $resolved = $resolveResult.Output
        if ($resolved -match [regex]::Escape($Package)) { break }
        Start-Sleep -Seconds 1
        $waited++
    }
    if ($resolved -notmatch [regex]::Escape($Package)) {
        Exit-Fatal 'La aplicacion no quedo configurada como pantalla de inicio tras 30 segundos. Volve a ejecutar este programa; si el problema persiste, avisa a soporte con el registro de esta ejecucion.'
    }

    $taskResult = Invoke-Adb -Arguments @('shell', 'dumpsys', 'activity', 'activities')
    $taskLines = $taskResult.Output -split "`r`n"
    $taskLine = $taskLines | Where-Object { $_ -match ('Task\{.*' + [regex]::Escape($Package)) } | Select-Object -First 1
    if (-not $taskLine -or $taskLine -notmatch 'type=home') {
        Exit-Fatal 'No se pudo confirmar que la aplicacion arranco correctamente como pantalla de inicio. Avisa a soporte con el registro de esta ejecucion.'
    }

    Write-Info 'Modo kiosco confirmado.'
}

# -----------------------------------------------------------------------
# Paso 8: reiniciar y confirmar en vivo que arranca directo en la app, en
# foco y con el kiosco (lock task) activo -- exactamente lo que le va a
# pasar al paciente/clinico al prender la tablet.
# -----------------------------------------------------------------------
function Invoke-RebootVerification {
    Write-Step 8 $TotalSteps 'Reiniciando la tablet para confirmar que quedo bien configurada...'
    Write-Info 'No desconectes el cable ni apagues la tablet durante este paso.'

    Invoke-Adb -Arguments @('reboot') | Out-Null

    Write-Info 'Esperando a que la tablet vuelva a encender (puede tardar unos minutos)...'
    & $AdbPath -s $Serial wait-for-device
    Write-Log "adb -s $Serial wait-for-device (exit code: $LASTEXITCODE)"

    Write-Info 'Esperando a que termine de arrancar...'
    $waited = 0
    $bootCompleted = ''
    while ($waited -lt 120) {
        $bootResult = Invoke-Adb -Arguments @('shell', 'getprop', 'sys.boot_completed')
        $bootCompleted = $bootResult.Output.Trim()
        if ($bootCompleted -eq '1') { break }
        Start-Sleep -Seconds 2
        $waited += 2
    }
    if ($bootCompleted -ne '1') {
        Exit-Fatal 'La tablet no termino de arrancar en 2 minutos. Puede seguir arrancando igual: esperá un minuto mas y fijate si la app abrio sola. Si no, avisa a soporte con el registro de esta ejecucion.'
    }

    Write-Info 'Arranco. Esperando unos segundos mas a que la aplicacion tome el control...'
    Start-Sleep -Seconds 15

    Write-Info 'Verificando que la aplicacion quedo en primer plano...'
    $focusResult = Invoke-Adb -Arguments @('shell', 'dumpsys', 'window')
    $focusLine = ($focusResult.Output -split "`r`n") | Where-Object { $_ -match 'mCurrentFocus' } | Select-Object -First 1
    if (-not $focusLine -or $focusLine -notmatch [regex]::Escape($Package)) {
        Exit-Fatal "Tras reiniciar, la aplicacion del simulador no quedo en primer plano. Puede que la tablet haya quedado a mitad de una configuracion anterior -- volve a ejecutar este programa; si el problema persiste, avisa a soporte con el registro de esta ejecucion."
    }

    Write-Info 'Verificando que el modo kiosco quedo activo...'
    $lockResult = Invoke-Adb -Arguments @('shell', 'dumpsys', 'activity', 'activities')
    $lockLine = ($lockResult.Output -split "`r`n") | Where-Object { $_ -match 'mLockTaskModeState' } | Select-Object -First 1
    if (-not $lockLine -or $lockLine -notmatch 'LOCKED') {
        Exit-Fatal 'Tras reiniciar, el modo kiosco no quedo activo. Avisa a soporte con el registro de esta ejecucion.'
    }

    Write-Success 'Confirmado: la tablet arranca directo en el simulador, en modo kiosco.'
}

function Write-FinalSummary {
    param([string]$ApkVersion)

    $model = (Invoke-Adb -Arguments @('shell', 'getprop', 'ro.product.model')).Output.Trim()
    $androidRelease = (Invoke-Adb -Arguments @('shell', 'getprop', 'ro.build.version.release')).Output.Trim()
    $pkgInfo = (Invoke-Adb -Arguments @('shell', 'dumpsys', 'package', $Package)).Output
    $versionName = $ApkVersion
    $vnMatch = [regex]::Match($pkgInfo, 'versionName=(\S+)')
    if ($vnMatch.Success) { $versionName = $vnMatch.Groups[1].Value }

    Write-Host ''
    Write-Host '============================================' -ForegroundColor Green
    Write-Host '  LISTO: la tablet quedo configurada' -ForegroundColor Green
    Write-Host '============================================' -ForegroundColor Green
    Write-Host "  Modelo: $model (Android $androidRelease)"
    Write-Host "  Serie: $Serial"
    Write-Host "  Aplicacion: $AppLabel $versionName"
    Write-Host '  Modo kiosco: activo'
    Write-Host ''
    Write-Host 'La tablet ya esta lista para entregar. No hace falta hacer nada mas.' -ForegroundColor Green
    Write-Log "RESUMEN FINAL: modelo=$model android=$androidRelease serial=$Serial version=$versionName"
}

# -----------------------------------------------------------------------
# Main
# -----------------------------------------------------------------------
try {
    Show-Welcome

    $AdbPath = Resolve-AdbBinary
    $apk = Get-TabletApk -Backend $BackendUrl
    Wait-ForDevice
    Confirm-DeviceReady
    Install-TabletApk -ApkPath $apk.Path
    Set-KioskDeviceOwner
    Start-KioskAppAndVerify
    Invoke-RebootVerification
    Write-FinalSummary -ApkVersion $apk.Version

    exit 0
} catch {
    Exit-Fatal "Ocurrio un problema inesperado. Detalle tecnico: $($_.Exception.Message)"
}
