using UnityEngine;

namespace SlaveDriver
{
    /// <summary>
    /// Handles the care and feeding of ModuleRoboticSlave in the flight scene.
    /// </summary>
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    class FlightBehaviour : MonoBehaviour
    {
        public void Awake()
        {
            Logging.Log("Registering events");
            GameEvents.onVesselLoaded.Add(OnVesselLoaded);
            GameEvents.onVesselCreate.Add(OnVesselCreate);
            GameEvents.onPartActionUIShown.Add(OnPartActionUIShown);
        }

        public void OnDestroy()
        {
            Logging.Log("Unregistering events");
            GameEvents.onVesselLoaded.Remove(OnVesselLoaded);
            GameEvents.onVesselCreate.Remove(OnVesselCreate);
            GameEvents.onPartActionUIShown.Remove(OnPartActionUIShown);
        }

        /// <summary>
        /// This is called in a variety of circumstances: vessel is launched, or created by undocking,
        /// decoupling, planting a flag, or EVA; also triggered by new asteroid creation and rescue
        /// Kerbal contracts.
        /// </summary>
        /// <param name="vessel"></param>
        private void OnVesselCreate(Vessel vessel)
        {
            // There are a lot of cases we don't care about, and only a couple of cases where we
            // do.  We care about the case of launching a new vessel, and we also care about
            // when one vessel turns into two vessels due to undocking, decoupling, etc.
            // All the other cases we don't care about.
            //
            // Fortunately, it's easy to eliminate the don't-care-about cases, because those will
            // spawn a vessel that has zero parts in it (since they're not loaded), so we can
            // ignore those.
            if (vessel.parts.Count == 0) return;

            Logging.Log("OnVesselCreate (" + vessel.parts.Count + " parts): " + vessel.vesselName);

            // Order of operations when launching a ship appears to be:
            // 1. OnVesselCreate gets called
            // 2. The PartModules' OnStart gets called (e.g. for ModuleRoboticSlave)
            // 3. OnVesselRollout gets called
            ModuleRoboticSlave.RefreshFromRoot(vessel.parts[0]);
        }

        /// <summary>
        /// Here when a vessel in flight is loaded.
        /// </summary>
        /// <param name="data"></param>
        private void OnVesselLoaded(Vessel vessel)
        {
            Logging.Log("Loaded vessel (" + vessel.parts.Count + " parts): " + vessel.vesselName);
            ModuleRoboticSlave.RefreshFromRoot(vessel.parts[0]);
        }

        /// <summary>
        /// Here when a PAW is popped up in the editor.
        /// </summary>
        /// <param name="paw"></param>
        /// <param name="part"></param>
        private void OnPartActionUIShown(UIPartActionWindow paw, Part part)
        {
            ModuleRoboticSlave.OnPartActionUIShown(paw, part);
        }
    }
}
