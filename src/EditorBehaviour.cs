using KSP.UI.Screens;
using UnityEngine;

namespace SlaveDriver
{
    /// <summary>
    /// Handles the care and feeding of ModuleRoboticSlave in the vehicle editor.
    /// </summary>
    [KSPAddon(KSPAddon.Startup.EditorAny, false)]
    class EditorBehaviour : MonoBehaviour
    {
        public void Awake()
        {
            Logging.Log("Registering events");
            GameEvents.onEditorPartEvent.Add(OnEditorPartEvent);
            GameEvents.onEditorLoad.Add(OnEditorLoad);
            GameEvents.onEditorStarted.Add(OnEditorStarted);
            GameEvents.onPartActionUIShown.Add(OnPartActionUIShown);
        }

        public void OnDestroy()
        {
            Logging.Log("Unregistering events");
            GameEvents.onEditorPartEvent.Remove(OnEditorPartEvent);
            GameEvents.onEditorLoad.Remove(OnEditorLoad);
            GameEvents.onEditorStarted.Remove(OnEditorStarted);
            GameEvents.onPartActionUIShown.Remove(OnPartActionUIShown);
        }


        /// <summary>
        /// Here when an event happens to the part in the editor.
        /// </summary>
        /// <param name="eventType"></param>
        /// <param name="part"></param>
        private void OnEditorPartEvent(ConstructionEventType eventType, Part part)
        {
            switch (eventType)
            {
                case ConstructionEventType.PartAttached:
                    ModuleRoboticSlave.OnEditorPartAttached(part);
                    break;
                case ConstructionEventType.PartDetached:
                    ModuleRoboticSlave.OnEditorPartDetached(part);
                    break;
                default:
                    // don't care about anything else
                    break;
            }
        }


        /// <summary>
        /// Here when a ship loads in the editor.
        /// </summary>
        /// <param name="ship"></param>
        /// <param name="loadType"></param>
        private void OnEditorLoad(ShipConstruct ship, CraftBrowserDialog.LoadType loadType)
        {
            // Order of operations:
            // 1. This method (OnEditorLoad) gets called.
            // 2. PartModules' OnStart method gets called (e.g. for ModuleRoboticSlave)
            Logging.Log("Loaded vessel in editor: " + ship.shipName);
            ModuleRoboticSlave.RefreshFromRoot(ship.parts[0]);
        }


        /// <summary>
        /// Here when the editor starts up. We need this because if it starts with a ship
        /// already loaded (e.g. upon "revert to VAB"), then we don't get an OnEditorLoad
        /// notification, but we *do* still need to do the necessary initialization.
        /// </summary>
        private void OnEditorStarted()
        {
            ShipConstruct ship = EditorLogic.fetch.ship;
            if ((ship == null) || (ship.parts.Count == 0)) return;

            Logging.Log("Entered editor with ship: " + ship.shipName);
            ModuleRoboticSlave.RefreshFromRoot(ship.parts[0]);
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
