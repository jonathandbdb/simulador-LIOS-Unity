using System;
using UnityEngine;

namespace Simulador.Data
{
    /// <summary>
    /// Gate compartido que oculta la escena de fondo (cockpit, halos, disability glare, todo
    /// lo que renderiza <c>Camera.main</c>) mientras una pantalla modal a pantalla completa del
    /// visor este visible -- <see cref="Simulador.Update.UpdatePromptVR"/> y
    /// <see cref="Simulador.License.LicenseBlockScreenVR"/> son los dos consumidores actuales, y
    /// AMBOS pueden estar activos al mismo tiempo (los updates siguen funcionando con la
    /// licencia bloqueada, ver docs/licenciamiento.md) -- de ahi el refcount: la escena solo se
    /// restaura cuando el ULTIMO consumidor la libera, nunca antes.
    ///
    /// Reemplaza el mecanismo anterior ("canvas opaco pegado a la camara a 0.15-0.2 m", F5/F3)
    /// que causaba diplopia real en el Quest (reportado en dispositivo, ver docs/updates.md
    /// Gotchas): un plano a esa distancia exige una convergencia binocular imposible/incomoda
    /// para el ojo humano y el cerebro no llega a fusionar las dos vistas -- se veia doble, un
    /// cartel por ojo. Este gate resuelve "no se debe ver la simulacion detras" a nivel de
    /// CAMARA en vez de a nivel de geometria: mientras esta activo, restringe
    /// <c>cullingMask</c> a la capa <c>UI</c> (builtin de Unity, layer 5 -- ninguna otra capa la
    /// usaba en el proyecto al momento de este cambio, ver docs/updates.md) y fuerza
    /// <c>clearFlags = SolidColor</c> con un color oscuro solido -- asi NINGUNA geometria de la
    /// escena llega a rasterizarse, solo lo que este en esa capa (los canvases de estas dos
    /// pantallas, ver <see cref="ApplyOverlayLayer"/>). El canvas en si puede volver a vivir a
    /// distancia estereo comoda (1.5-2 m) porque ya no necesita tapar nada con su propio tamaño
    /// -- la camara se encarga de eso.
    ///
    /// LIMITE DEL SNAPSHOT (regresion 0.8.1): la foto que toma <see cref="Acquire"/> NO es la
    /// fuente de verdad del aspecto de camara. <c>clearFlags</c>/<c>backgroundColor</c> los posee
    /// <c>Simulador.Vision.ScenarioManager</c> (dia = Skybox, noche = SolidColor casi negro) y
    /// puede cambiarlos en cualquier momento; ademas el bootstrap de la pantalla de calce corre
    /// en AfterSceneLoad, ANTES del <c>Start()</c> del ScenarioManager, asi que la foto sale del
    /// estado AUTORADO de la escena (Skybox) y no del escenario vigente. Restaurarla a ciegas
    /// reponia el skybox diurno detras de ruta_noche. Por eso <see cref="Release"/> avisa por
    /// <see cref="SceneRestored"/>: el dueño del aspecto se reimpone despues del restore.
    /// </summary>
    public static class CameraSceneOcclusionGate
    {
        private const string OverlayLayerName = "UI";
        private static readonly Color OccludedBackgroundColor = new Color(0.03f, 0.03f, 0.04f, 1f);

        private static int _refCount;
        private static bool _applied;
        private static Camera _camera;
        private static int _savedCullingMask;
        private static CameraClearFlags _savedClearFlags;
        private static Color _savedBackgroundColor;

        /// <summary>
        /// True mientras la oclusion este EFECTIVAMENTE aplicada sobre la camara. Lo consulta el
        /// dueño del aspecto de camara (<c>ScenarioManager</c>) para NO escribir
        /// <c>clearFlags</c>/<c>backgroundColor</c> mientras la escena debe estar oculta: sin
        /// eso, un cambio de escenario con el gate tomado repone el Skybox del consultorio y el
        /// panorama se ve detras del cartel modal.
        /// </summary>
        public static bool IsOccluding => _applied;

        /// <summary>
        /// Se dispara cuando el ULTIMO consumidor libera el gate y la camara YA fue restaurada
        /// (fin de <see cref="Release"/>, refcount en 0). Existe porque el snapshot del gate no
        /// es la fuente de verdad del aspecto de camara (ver el docstring de la clase): quien lo
        /// posee se suscribe y lo reimpone. El orden es critico -- si se disparara ANTES del
        /// restore, el gate pisaria lo que el suscriptor acaba de escribir.
        /// </summary>
        public static event Action SceneRestored;

        // Reset defensivo: RuntimeInitializeOnLoadMethod corre en CADA sesion de Play (Editor o
        // build), con o sin domain reload -- sin esto, un refcount residual de una sesion de
        // Play anterior con Domain Reload deshabilitado (Editor) dejaria este gate en un estado
        // inconsistente en la sesion siguiente.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStaticState()
        {
            _refCount = 0;
            _applied = false;
            _camera = null;
            // El evento es estatico: sin este reset, con Domain Reload deshabilitado (Editor)
            // quedarian suscriptores de la sesion de Play anterior (MonoBehaviours ya
            // destruidos) y la invocacion tiraria MissingReferenceException.
            SceneRestored = null;
        }

        /// <summary>
        /// Pone <paramref name="root"/> y TODA su jerarquia en la capa <c>UI</c> -- necesario
        /// para que sobreviva al <c>cullingMask</c> restringido mientras el gate este activo
        /// (el cullingMask de la camara filtra por capa de GameObject, no se hereda del padre:
        /// cada Image/Text hijo necesita estar en la capa el mismo, no solo el root del canvas).
        /// </summary>
        public static void ApplyOverlayLayer(GameObject root)
        {
            int layer = LayerMask.NameToLayer(OverlayLayerName);
            if (layer < 0)
            {
                // SIM: atajo deliberado -- "UI" es una capa builtin de Unity, esto no deberia
                // pasar nunca; si un proyecto la borrara del TagManager, el cartel seguiria
                // funcionando pero sin el gate de oclusion (se veria la escena detras).
                Debug.LogWarning("[Occlusion] La capa builtin 'UI' no existe en este proyecto; el gate de oclusion no va a poder aplicarse.");
                return;
            }
            SetLayerRecursively(root, layer);
        }

        private static void SetLayerRecursively(GameObject go, int layer)
        {
            go.layer = layer;
            foreach (Transform child in go.transform) SetLayerRecursively(child.gameObject, layer);
        }

        /// <summary>
        /// Suma un consumidor. Si es el primero, guarda el estado actual de <c>Camera.main</c>
        /// y aplica la oclusion; si ya habia otro consumidor activo, solo incrementa el
        /// refcount (idempotente para el estado de la camara -- nunca se vuelve a guardar/pisar
        /// un estado ya guardado, eso corromperia el restore).
        /// </summary>
        public static void Acquire()
        {
            _refCount++;
            if (_applied) return; // ya aplicado por otro consumidor -- no volver a guardar/pisar

            var cam = Camera.main;
            if (cam == null)
            {
                // SIM: atajo deliberado -- sin camara no hay nada que ocultar; en Main.unity
                // Camera.main siempre existe (XR Origin). El resto de la pantalla (texto,
                // input) sigue funcionando igual, solo falta el ocultamiento de fondo.
                Debug.LogWarning("[Occlusion] No se encontro Camera.main; no se puede ocultar la escena de fondo.");
                return;
            }

            _camera = cam;
            _savedCullingMask = cam.cullingMask;
            _savedClearFlags = cam.clearFlags;
            _savedBackgroundColor = cam.backgroundColor;

            int layer = LayerMask.NameToLayer(OverlayLayerName);
            cam.cullingMask = layer >= 0 ? (1 << layer) : 0;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = OccludedBackgroundColor;
            _applied = true;
        }

        /// <summary>
        /// Libera un consumidor. Restaura el estado original de la camara SOLO cuando el
        /// refcount llega a 0 (el ultimo consumidor en cerrarse) -- si otro consumidor sigue
        /// activo, la escena debe seguir oculta.
        /// </summary>
        public static void Release()
        {
            if (_refCount <= 0) return; // guard defensivo -- no deberia poder llamarse de mas
            _refCount--;
            if (_refCount > 0) return; // otro consumidor sigue activo, no restaurar todavia

            if (_applied && _camera != null)
            {
                _camera.cullingMask = _savedCullingMask;
                _camera.clearFlags = _savedClearFlags;
                _camera.backgroundColor = _savedBackgroundColor;
            }
            _applied = false;
            _camera = null;

            // DESPUES del restore (nunca antes): el dueño del aspecto de camara reimpone el
            // estado del escenario vigente sobre la foto generica que acabamos de reponer.
            SceneRestored?.Invoke();
        }
    }
}
