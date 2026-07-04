using UnityEngine;

namespace Simulador.Vision
{
    /// <summary>
    /// Ancla las fuentes de glare del sol a una DIRECCION del cielo fija en el mundo,
    /// reposicionandolas cada frame a camPos + sunDirection * distance. Asi el glare del
    /// sol queda "al infinito": su direccion de pantalla es constante al TRASLADAR la
    /// cabeza (cero paralaje), solidario con el paisaje del portal (WindowPortal.shader),
    /// que tambien se muestrea por direccion de vista. Antes las fuentes vivian en una
    /// posicion de mundo fija a ~4.9 m => paralaje de objeto cercano contra el paisaje a
    /// infinito ("el sol esta en la sala").
    ///
    /// El DISCO solar lo pinta el propio WindowPortal.shader (_SunDirWS); este script solo
    /// mueve el halo/starburst clinico (billboards GlareBillboardInstance, hijos) y NO
    /// toca energias ni curvas de glare (v_fade satura con src_energy*8/distance >= 1).
    /// De paso, como DisabilityGlareController lee transform.position de cada fuente, el
    /// velo CIE pasa a calcular theta contra una direccion fija = comportamiento correcto
    /// de fuente al infinito (mejor que el punto de mundo fijo anterior).
    ///
    /// 'distance' se mantiene por DEBAJO de la distancia al vidrio-portal para que el glare
    /// siga DELANTE del portal opaco (si no, el ZTest del vidrio lo ocluiria).
    /// 'sunDirection' DEBE coincidir con _SunDirWS de WindowView.mat (misma direccion de sol).
    /// </summary>
    public class SunSkyAnchor : MonoBehaviour
    {
        [Tooltip("Direccion del sol en el mundo (unit). Debe coincidir con _SunDirWS de WindowView.mat.")]
        public Vector3 sunDirection = new Vector3(-0.4149f, 0.1908f, 0.8897f);

        [Tooltip("Distancia (m) a la que se coloca el glare desde la camara. Debe quedar DELANTE del vidrio-portal.")]
        public float distance = 4.9f;

        [Tooltip("Camara del jugador (si null, usa Camera.main).")]
        public Camera cam;

        private void LateUpdate()
        {
            if (cam == null) cam = Camera.main;
            if (cam == null) return;
            Vector3 d = sunDirection.sqrMagnitude > 1e-6f ? sunDirection.normalized : Vector3.forward;
            transform.position = cam.transform.position + d * distance;
        }
    }
}
