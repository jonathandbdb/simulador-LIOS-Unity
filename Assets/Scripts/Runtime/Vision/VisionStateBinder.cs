using System.Collections;
using Simulador.Data;
using UnityEngine;

namespace Simulador.Vision
{
    /// <summary>
    /// Base comun de los suscriptores de DataManager.VisionStateChanged (P6.1). Centraliza el
    /// ciclo de vida que triplicaban VisionParamsBinder, GlareController y DisabilityGlareController:
    /// espera al singleton DataManager (coroutine), se suscribe al evento, despacha el estado inicial
    /// por ojo y se desuscribe en OnDisable. Cada subclase solo implementa ApplyEyeState con SU
    /// logica especifica (uniforms del material, globals del glare, straylight del velo).
    ///
    /// Las variaciones sutiles entre binders NO se homogeneizan: se preservan con hooks virtuales.
    /// - OnAfterInitialDispatch(): trabajo extra tras el despacho inicial, dentro de la MISMA
    ///   coroutine (p.ej. el blend demo opt-in de VisionParamsBinder, que espera al catalogo).
    /// - OnBinderDisable(): limpieza extra en OnDisable (p.ej. el reset de _GlareVeilL/R y
    ///   VisionActivity.Veil* de DisabilityGlareController).
    /// GlareController conserva su propio OnEnable (publica los umbrales de facing antes del primer
    /// render): es independiente de este ciclo de vida, por eso no necesita hook.
    /// </summary>
    public abstract class VisionStateBinder : MonoBehaviour
    {
        protected DataManager _dm;

        protected IEnumerator Start()
        {
            while (DataManager.Instance == null) yield return null;
            _dm = DataManager.Instance;
            _dm.VisionStateChanged += OnVisionChanged;

            // Empujar el estado actual (si ya habia lentes aplicadas).
            ApplyEyeState("left", _dm.Left);
            ApplyEyeState("right", _dm.Right);

            yield return OnAfterInitialDispatch();
        }

        protected virtual void OnDisable()
        {
            if (_dm != null) _dm.VisionStateChanged -= OnVisionChanged;
            OnBinderDisable();
        }

        private void OnVisionChanged(string eye, EyeState state) => ApplyEyeState(eye, state);

        /// <summary>Aplica el estado clinico de un ojo ("left"/"right") a su destino especifico.</summary>
        protected abstract void ApplyEyeState(string eye, EyeState state);

        /// <summary>Hook opcional tras el despacho inicial, dentro de la coroutine Start. Vacio por defecto.</summary>
        protected virtual IEnumerator OnAfterInitialDispatch() { yield break; }

        /// <summary>Hook opcional de limpieza en OnDisable. Vacio por defecto.</summary>
        protected virtual void OnBinderDisable() { }
    }
}
