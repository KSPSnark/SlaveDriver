using Expansions.Serenity;
using System;

namespace SlaveDriver
{
    /// <summary>
    /// PartModule that allows "slaving" a robotic part to another part higher up the
    /// parent hierarchy, so that it moves when the parent does.
    /// </summary>
    public class ModuleRoboticSlave : PartModule
    {
        // Non-localized placeholder string that we set things to when they're not supposed to be visible.
        private const string DUMMY_UNUSED = "N/A";

        // Slaved servos are only updated if their current setting is at least this much off
        // the calculated one, in order to prevent jitter spam.
        private const float TARGET_ANGLE_EPSILON = 0.01f;

        // Master change trackers
        ChangeTracker<float> masterTargetAngleTracker = new ChangeTracker<float>(OnMasterTargetAngleModified);
        ChangeTracker<float> masterMinLimitTracker = new ChangeTracker<float>(OnMasterMinMaxLimitsModified);
        ChangeTracker<float> masterMaxLimitTracker = new ChangeTracker<float>(OnMasterMinMaxLimitsModified);
        ChangeTracker<float> masterTraverseVelocityTracker = new ChangeTracker<float>(OnMasterTraverseVelocityModified);
        ChangeTracker<float> masterDampingTracker = new ChangeTracker<float>(OnMasterDampingModified);
        ChangeTracker<bool> masterLockedTracker = new ChangeTracker<bool>(OnMasterLockedModified);

        // Servo change trackers
        ChangeTracker<float> servorMinLimitTracker = new ChangeTracker<float>(OnServoMinMaxLimitsModified);
        ChangeTracker<float> servoMaxLimitTracker = new ChangeTracker<float>(OnServoMinMaxLimitsModified);
        ChangeTracker<bool> servoIsMotorizedTracker = new ChangeTracker<bool>(OnServoMotorizationChanged);
        ChangeTracker<bool> servoMotorEngagedTracker = new ChangeTracker<bool>(OnServoMotorizationChanged);


        // This is initialized once, on first access, and then left alone. It points at the servo
        // on *this* part, which this module will control when slaved.  Ideally, it should never
        // be null. If it's null, it means somebody goofed their part config and erroneously
        // put a ModuleRoboticSlave on a part that doesn't have a ModuleRoboticServoHinge on it.
        private ModuleRoboticServoHinge _servo = null;
        private bool initializedServo = false;
        private ModuleRoboticServoHinge Servo => InitializeServo();

        // This will be null when no master-usable part is present in the part's parent hierarchy.
        // Otherwise, it will be set to the closest ancestor that's master-usable. Note that
        // it's present even when slaveSelected is false, since it represents what *will* be
        // the master if slaveSelected is ever toggled on.
        private ModuleRoboticServoHinge _master = null;
        private bool initializedMaster = false;
        private ModuleRoboticServoHinge Master
        {
            get { return InitializeMaster(); }
            set { SetMaster(value); }
        }

        // These are used for handling the stock servo fields that we hide when a servo is slaved.
        // Each one handles a pair of fields: the stock field (shown when not slaved), and a text-only
        // display field (shown when slaved). Only one or the other is displayed at any given time.
        private SlavedFieldPair targetAngleFieldPair = null;
        private SlavedFieldPair traverseVelocityFieldPair = null;
        private SlavedFieldPair dampingFieldPair = null;
        private SlavedFieldPair lockedFieldPair = null;

        /// <summary>
        /// When supplied, will be used as the part title when displaying the name of this
        /// part as other parts' masters. If absent, the full part name will be used, which
        /// may be awkwardly long.
        /// </summary>
        [KSPField]
        public string partAbbreviation = string.Empty;

        /// <summary>
        /// Remembers the user's preference for this part being slaved or not. This is persisted,
        /// and is never programmatically changed because it's where we remember the user
        /// preference. Note that whether the part actually *acts* like a slave depends not
        /// only on this setting, but whether a master is actually available in the part's
        /// parent hierarchy.
        /// 
        /// IMPORTANT:  This flag merely stores the user's *choice*, and does not solely determine
        /// whether slave mode is actually enabled. For that to happen, the part must also be
        /// situated somewhere that has a master located in its parent hierarchy.
        /// </summary>
        [KSPField(isPersistant = true)]
        public bool slaveSelected = false;

        /// <summary>
        /// Right-click menu item for switching between "slave" mode and "free" mode.
        /// </summary>
        [KSPEvent(active = true, guiActive = false, guiActiveEditor = false, guiName = DUMMY_UNUSED)]
        public void DoToggleSlaveEvent()
        {
            if (Servo == null)
            {
                Logging.Warn("Invalid part configuration on " + ToString(part) + ", doing nothing");
                return;
            }
            SetUserSlaveMode(!slaveSelected, true);
        }
        private BaseEvent ToggleSlaveEvent { get { return Events["DoToggleSlaveEvent"]; } }

        /// <summary>
        /// Action group item for toggling slave mode.
        /// </summary>
        /// <param name="actionParam"></param>
        [KSPAction("#SlaveDriver_toggleSlaveAction")]
        public void DoToggleSlaveAction(KSPActionParam actionParam)
        {
            if (Servo == null)
            {
                Logging.Warn("Invalid part configuration on " + ToString(part) + ", doing nothing");
                return;
            }
            SetUserSlaveMode(actionParam.type == KSPActionType.Activate, true);
        }
        private BaseAction ToggleSlaveAction { get { return Actions["DoToggleSlaveAction"]; } }

        /// <summary>
        /// Used for displaying the target angle when in slave mode.
        /// </summary>
        [KSPField(guiName = DUMMY_UNUSED, guiFormat = "F1", guiActive = false, guiActiveEditor = false)]
        public float slavedTargetAngle = float.NaN;
        private const string SLAVED_TARGET_ANGLE_FIELD = "slavedTargetAngle";
        private const string UNSLAVED_TARGET_ANGLE_FIELD = "targetAngle";

        /// <summary>
        /// Used for displaying the traverse velocity when in slave mode.
        /// </summary>
        [KSPField(guiName = DUMMY_UNUSED, guiFormat = "F1", guiActive = false, guiActiveEditor = false)]
        public float slavedTraverseVelocity = float.NaN;
        private const string SLAVED_TRAVERSE_VELOCITY_FIELD = "slavedTraverseVelocity";
        private const string UNSLAVED_TRAVERSE_VELOCITY_FIELD = "traverseVelocity";

        /// <summary>
        /// Used for displaying the damping when in slave mode.
        /// </summary>
        [KSPField(guiName = DUMMY_UNUSED, guiFormat = "F1", guiActive = false, guiActiveEditor = false)]
        public float slavedDamping = float.NaN;
        private const string SLAVED_DAMPING_FIELD = "slavedDamping";
        private const string UNSLAVED_DAMPING_FIELD = "hingeDamping";

        /// <summary>
        /// Used for displaying the locked status when in slave mode.
        /// </summary>
        [KSPField(guiName = DUMMY_UNUSED, guiActive = false, guiActiveEditor = false)]
        public bool slavedLocked = false;
        private const string SLAVED_LOCKED_FIELD = "slavedLocked";
        private const string UNSLAVED_LOCKED_FIELD = "servoIsLocked";


        /// <summary>
        /// Gets whether slave mode is actually enabled or not.  For slave mode to be enabled,
        /// two conditions must both apply.  First, the user must have explicitly chosen to set
        /// the part into slave mode. Second, the part must have a master (i.e. there's some
        /// master-capable part located in its parent hierarchy).
        /// </summary>
        private bool IsSlaveEnabled => slaveSelected && (Master != null);

        /// <summary>
        /// Here when the part is starting up.
        /// </summary>
        /// <param name="state"></param>
        public override void OnStart(StartState state)
        {
            base.OnStart(state);
        }

        /// <summary>
        /// Called on each frame.
        /// </summary>
        public void LateUpdate()
        {
            if ((Master != null) && IsSlaveEnabled)
            {
                masterTargetAngleTracker.Update(Master.targetAngle, this);
                masterMinLimitTracker.Update(Master.softMinMaxAngles.x, this);
                masterMinLimitTracker.Update(Master.softMinMaxAngles.y, this);
                masterTraverseVelocityTracker.Update(Master.traverseVelocity, this);
                masterDampingTracker.Update(Master.hingeDamping, this);
                masterLockedTracker.Update(Master.servoIsLocked, this);
            }
        }

        /// <summary>
        /// Reset all trackers. Needed when switching from one master to another.
        /// </summary>
        private void ResetTrackers()
        {
            masterTargetAngleTracker.Reset();
            masterMinLimitTracker.Reset();
            masterMinLimitTracker.Reset();
            masterTraverseVelocityTracker.Reset();
            masterDampingTracker.Reset();
            masterLockedTracker.Reset();
        }

        /// <summary>
        /// Sets up the Servo property on demand.
        /// </summary>
        private ModuleRoboticServoHinge InitializeServo()
        {
            if (!initializedServo)
            {
                initializedServo = true;
                Logging.Log("Initializing servo for " + ToString(part));

                // Locate the servo for this part.
                _servo = TryGetServoModule(part);

                if (_servo == null)
                {
                    // Somebody done goofed, and added config to put this PartModule where
                    // it doesn't belong, i.e. on something that's not a robotic part.
                    Logging.Error(
                        "No appropriate servo module found for ModuleRoboticSlave on "
                        + ToString(part) + "; mod won't function for this part.");
                    Master = null;
                    slaveSelected = false;
                    return null;
                }

                InitializeFieldPairs();
            }

            return _servo;
        }

        private void InitializeFieldPairs()
        {
            if (targetAngleFieldPair == null)
            {
                targetAngleFieldPair = CreateSlavedFieldPair(UNSLAVED_TARGET_ANGLE_FIELD, SLAVED_TARGET_ANGLE_FIELD);
                traverseVelocityFieldPair = CreateSlavedFieldPair(UNSLAVED_TRAVERSE_VELOCITY_FIELD, SLAVED_TRAVERSE_VELOCITY_FIELD);
                dampingFieldPair = CreateSlavedFieldPair(UNSLAVED_DAMPING_FIELD, SLAVED_DAMPING_FIELD);
                lockedFieldPair = CreateSlavedFieldPair(UNSLAVED_LOCKED_FIELD, SLAVED_LOCKED_FIELD);
            }
        }


        /// <summary>
        /// Here when our servo has changed its min/max limits.
        /// </summary>
        /// <param name="host"></param>
        private static void OnServoMinMaxLimitsModified(ModuleRoboticSlave host)
        {
            if (!host.IsSlaveEnabled) return; // nothing to do

            // If we're slaved, then changing our servo limits requires recalculating
            // the same slaved parameters as if our master's servo limits changed.
            OnMasterMinMaxLimitsModified(host);
        }

        /// <summary>
        /// Here when the is-motorized or is-motor-engaged status of our servo changes.
        /// </summary>
        /// <param name="obj"></param>
        private static void OnServoMotorizationChanged(ModuleRoboticSlave host)
        {
            Logging.Log("Motorization changed for " + ToString(host.part));
            PropagateMaster(host.part, host.Master);
        }

        /// <summary>
        /// Here when our master's target angle gets modified.
        /// </summary>
        /// <param name="host"></param>
        private static void OnMasterTargetAngleModified(ModuleRoboticSlave host)
        {
            host.AdjustSlavedTargetAngle();
        }

        /// <summary>
        /// Here when our master's min/max limits get modified.
        /// </summary>
        /// <param name="host"></param>
        private static void OnMasterMinMaxLimitsModified(ModuleRoboticSlave host)
        {
            host.AdjustSlavedTargetAngle();
            host.AdjustSlavedTraverseVelocity();
        }


        /// <summary>
        /// Here when our master's traverse velocity gets modified.
        /// </summary>
        /// <param name="host"></param>
        private static void OnMasterTraverseVelocityModified(ModuleRoboticSlave host)
        {
            host.AdjustSlavedTraverseVelocity();
        }


        /// <summary>
        /// Here when our master's traverse velocity gets modified.
        /// </summary>
        /// <param name="host"></param>
        private static void OnMasterDampingModified(ModuleRoboticSlave host)
        {
            host.AdjustSlavedDamping();
        }


        /// <summary>
        /// Here when our master's locked status gets toggled.
        /// </summary>
        /// <param name="host"></param>
        private static void OnMasterLockedModified(ModuleRoboticSlave host)
        {
            host.lockedFieldPair.SetValue(host.Master.servoIsLocked);
        }


        /// <summary>
        /// Adjust the slaved target angle in response to a change to the master.
        /// </summary>
        private void AdjustSlavedTargetAngle()
        {
            float fraction = (Master.targetAngle - Master.softMinMaxAngles.x) / (Master.softMinMaxAngles.y - Master.softMinMaxAngles.x);
            float angle = Servo.softMinMaxAngles.x + fraction * (Servo.softMinMaxAngles.y - Servo.softMinMaxAngles.x);

            // prevent jitter spam
            if (Math.Abs(angle - Servo.targetAngle) < TARGET_ANGLE_EPSILON) return;

            targetAngleFieldPair.SetValue(angle);
            slavedTargetAngle = angle;
        }


        /// <summary>
        /// Adjust the slaved traverse velocity in response to a change to the master.
        /// </summary>
        private void AdjustSlavedTraverseVelocity()
        {
            // Calculate the ideal traverse velocity, based on angle limits and master's traverse velocity
            float calculatedTraverseVelocity;
            if (Master.traverseVelocity < float.Epsilon)
            {
                calculatedTraverseVelocity = 0; // should never happen, but just in case
            }
            else
            {
                float traverseTime = (Master.softMinMaxAngles.y - Master.softMinMaxAngles.x) / Master.traverseVelocity;
                calculatedTraverseVelocity = (Servo.softMinMaxAngles.y - Servo.softMinMaxAngles.x) / traverseTime;
            }

            // It might be out of bounds, in which case we need to clamp it to within bounds.
            // TODO: might be nice to have some visual indication in the PAW that "this is clamped, you're out of bounds"
            if (calculatedTraverseVelocity < traverseVelocityFieldPair.unslavedMinValue)
            {
                calculatedTraverseVelocity = traverseVelocityFieldPair.unslavedMinValue;
            }
            else if (calculatedTraverseVelocity > traverseVelocityFieldPair.unslavedMaxValue)
            {
                calculatedTraverseVelocity = traverseVelocityFieldPair.unslavedMaxValue;
            }

            traverseVelocityFieldPair.SetValue(calculatedTraverseVelocity);

            AdjustSlavedDamping();
        }


        /// <summary>
        /// Adjust the slaved damping in response to a change to the master.
        /// </summary>
        private void AdjustSlavedDamping()
        {
            // Calculate the ideal damping, based on traverse velocities and master's damping.
            float calculatedDamping = Master.hingeDamping * Master.traverseVelocity / Servo.traverseVelocity;

            // It might be out of bounds, in which case we need to clamp it to within bounds.
            // TODO: might be nice to have some visual indication in the PAW that "this is clamped, you're out of bounds"
            if (calculatedDamping < dampingFieldPair.unslavedMinValue)
            {
                calculatedDamping = dampingFieldPair.unslavedMinValue;
            }
            else if (calculatedDamping > dampingFieldPair.unslavedMaxValue)
            {
                calculatedDamping = dampingFieldPair.unslavedMaxValue;
            }

            dampingFieldPair.SetValue(calculatedDamping);
        }


        /// <summary>
        /// Sets up the Master property on demand. This is used typically at initialization time
        /// when we're populating master references from scratch (e.g. at vessel load time).
        /// </summary>
        /// <returns></returns>
        private ModuleRoboticServoHinge InitializeMaster()
        {
            if (!initializedMaster)
            {
                initializedMaster = true;
                InitializeFieldPairs();
                SwitchMaster(FindMaster(part));
            }
            return _master;
        }

        /// <summary>
        /// Sets the master to the specified value. This is typically used when we need to actively
        /// switch from one master to another due to dynamically changed state such as a user
        /// toggling slave mode on and off.
        /// </summary>
        /// <param name="newMaster"></param>
        private void SetMaster(ModuleRoboticServoHinge newMaster)
        {
            if (!initializedMaster) initializedMaster = true;
            SwitchMaster(newMaster);
        }

        /// <summary>
        /// Shared logic for whenever the part's master changes.
        /// </summary>
        /// <param name="newMaster"></param>
        private void SwitchMaster(ModuleRoboticServoHinge newMaster)
        {
            _master = newMaster;
            ConfigureGui();
            ResetTrackers();
        }

        /// <summary>
        /// Given a servo module, get its target angle field.
        /// </summary>
        /// <param name="servo"></param>
        /// <param name="fieldName"></param>
        /// <returns></returns>
        private static BaseField FieldOf(ModuleRoboticServoHinge servo, string fieldName)
        {
            if (servo == null) return null;
            return servo.Fields[fieldName];
        }


        /// <summary>
        /// Useful when needing to refresh the master mappings for an entire vessel.
        /// </summary>
        /// <param name="rootPart"></param>
        internal static void RefreshFromRoot(Part rootPart)
        {
            Logging.Log("Refreshing master-slave chains for entire vessel from root " + ToString(rootPart));
            PropagateMaster(rootPart, null);
        }


        /// <summary>
        /// Here when any part is attached in the editor.
        /// </summary>
        /// <param name="part"></param>
        internal static void OnEditorPartAttached(Part part)
        {
            PropagateMaster(part, FindMaster(part));

            if (part.symmetryCounterparts != null)
            {
                for (int i = 0; i < part.symmetryCounterparts.Count; ++i)
                {
                    Part counterpart = part.symmetryCounterparts[i];
                    PropagateMaster(counterpart, FindMaster(counterpart));
                }
            }
        }


        /// <summary>
        /// Here when any part is detached in the editor.
        /// </summary>
        /// <param name="part"></param>
        internal static void OnEditorPartDetached(Part part)
        {
            // Not really anything vitally necessary to do, but recurse through
            // the part and its children to null out any masters, so we're not
            // left with dangling references.
            ModuleRoboticSlave slave = TryGetSlaveModule(part);
            if (slave != null)
            {
                slave.Master = null;
            }
            if (part.children != null)
            {
                for (int i = 0; i < part.children.Count; ++i)
                {
                    OnEditorPartDetached(part.children[i]);
                }
            }
        }


        /// <summary>
        /// Here when the user has manually chosen to toggle slave mode (either by action group or
        /// by PAW button). Significant because user choice is the one place where we set the
        /// persistent slaveSelected flag.
        /// </summary>
        /// <param name="isActivating">True if slave mode is being turned on, false if turned off.</param>
        /// <param name="includeSymmetryCounterparts"></param>
        private void SetUserSlaveMode(bool isActivating, bool includeSymmetryCounterparts)
        {
            if (isActivating == slaveSelected) return; // no change

            slaveSelected = isActivating;
            Logging.Log("User setting slave mode to " + isActivating + " for " + ToString(part));
            PropagateMaster(part, Master);

            AdjustSlavedTargetAngle();
            AdjustSlavedTraverseVelocity();
            AdjustSlavedDamping();
            OnMasterLockedModified(this);

            if (includeSymmetryCounterparts && (part.symmetryCounterparts != null))
            {
                for (int i = 0; i < part.symmetryCounterparts.Count; ++i)
                {
                    ModuleRoboticSlave slave = TryGetSlaveModule(part.symmetryCounterparts[i]);
                    if (slave == null) continue; // should never happen, though
                    slave.SetUserSlaveMode(isActivating, false);
                }
            }
        }


        /// <summary>
        /// Here when any PAW is shown, either in the editor or in flight.
        /// </summary>
        /// <param name="paw"></param>
        /// <param name="part"></param>
        internal static void OnPartActionUIShown(UIPartActionWindow paw, Part part)
        {
            ModuleRoboticSlave slave = TryGetSlaveModule(part);
            if (slave != null) slave.ConfigureGui();
        }

        /// <summary>
        /// Here when we need to set up the PAW's GUI due to slave mode changing around.
        /// </summary>
        /// <param name="prospectiveMaster"></param>
        private void ConfigureGui()
        {
            // We only show the "toggle slave mode on and off" control when a usable master
            // exists.
            bool hasMaster = Master != null;
            ToggleSlaveEvent.guiActive = hasMaster;
            ToggleSlaveEvent.guiActiveEditor = hasMaster;
            if (hasMaster)
            {
                ToggleSlaveEvent.guiName = LocalizeUtil.Format(
                    slaveSelected ? "#SlaveDriver_deactivateSlave" : "#SlaveDriver_activateSlave",
                    GetBriefDescription(Master));
            }
            else
            {
                ToggleSlaveEvent.guiName = DUMMY_UNUSED;
            }

            // Show/hide stuff based on whether we're actually in slave mode.
            bool isSlaveEnabled = IsSlaveEnabled;

            targetAngleFieldPair.ConfigureGui(isSlaveEnabled);
            if (float.IsNaN(slavedTargetAngle) && (Servo != null)) slavedTargetAngle = Servo.targetAngle;

            traverseVelocityFieldPair.ConfigureGui(isSlaveEnabled);
            if (float.IsNaN(slavedTraverseVelocity) && (Servo != null)) slavedTraverseVelocity = Servo.traverseVelocity;

            dampingFieldPair.ConfigureGui(isSlaveEnabled);
            if (float.IsNaN(slavedDamping) && (Servo != null)) slavedDamping = Servo.hingeDamping;

            lockedFieldPair.ConfigureGui(isSlaveEnabled);
            if (Servo != null) slavedLocked = Servo.servoIsLocked;
        }

        /// <summary>
        /// Sometimes a part's situation may change in a way that affects master-slave chains. Examples
        /// of this include attaching a part (with its tree of children); detaching a part (with its
        /// tree of children); user manually toggling slave mode on/off; altering ship in flight
        /// (e.g. docking, undocking, part destruction).  Whenever that happens, something that
        /// affects *this* part's choice of master can affect its entire child tree, too, so we need
        /// to propagate the change.
        ///
        /// This function affects the part passed in, as well as all of its children. It will be called
        /// on all parts, not just robotic parts are those equipped with ModuleRoboticSlave, since
        /// the master-slave connection propagates through "neutral" parts such as structural elements.
        /// </summary>
        /// <param name="part">The part to which to propagate a master</param>
        /// <param name="prospectiveMaster">The relevant actual or potential master. Null if no master candidate is available.</param>
        private static void PropagateMaster(Part part, ModuleRoboticServoHinge prospectiveMaster)
        {
            // Figure out how we propagate
            ModuleRoboticServoHinge localServo;
            ModuleRoboticSlave slave;
            PropagationType propagation = PropagationTypeOf(part, out localServo, out slave);
            ModuleRoboticServoHinge toPropagate = null;
            switch (propagation)
            {
                case PropagationType.Transparent:
                    toPropagate = prospectiveMaster;
                    break;
                case PropagationType.Assertive:
                    toPropagate = localServo;
                    break;
            }

            // If this part is a slaveable one, set the master.
            if (slave != null) slave.Master = prospectiveMaster;

            // Propagate to children.
            if (part.children != null)
            {
                for (int i = 0; i < part.children.Count; ++i)
                {
                    PropagateMaster(part.children[i], toPropagate);
                }
            }
        }


        /// <summary>
        /// Get a brief description of the part, if possible. Otherwise, get the full part title.
        /// </summary>
        /// <param name="part"></param>
        /// <returns></returns>
        private static string GetBriefDescription(ModuleRoboticServoHinge servo)
        {
            ModuleRoboticSlave slave = TryGetSlaveModule(servo.part);
            if ((slave == null) || (string.IsNullOrEmpty(slave.partAbbreviation)))
            {
                return servo.part.partInfo.title;
            }
            else
            {
                return slave.partAbbreviation;
            }
        }


        /// <summary>
        /// Walk up the parent hierarchy until a suitable master is found. Returns null if there
        /// isn't one.
        /// </summary>
        /// <param name="part"></param>
        /// <returns></returns>
        private static ModuleRoboticServoHinge FindMaster(Part part)
        {
            if (part == null) return null;
            ModuleRoboticServoHinge localServo;
            for (Part current = part.parent; current != null; current = current.parent)
            {
                switch (PropagationTypeOf(current, out localServo))
                {
                    case PropagationType.Assertive:
                        // found a part that wants to be a master, use that
                        return localServo;
                    case PropagationType.Dead:
                        // found a part that interrupts the chain
                        return null;
                }
            }

            // Walked all the way up to the root of the craft without finding
            // a suitable master.
            return null;
        }


        /// <summary>
        /// Find a servo module on the current part, or null if not found.
        /// </summary>
        /// <param name="part"></param>
        /// <returns></returns>
        private static ModuleRoboticServoHinge TryGetServoModule(Part part)
        {
            return TryGetModule<ModuleRoboticServoHinge>(part);
        }


        /// <summary>
        /// Find a slave module on the current part, or null if not found.
        /// </summary>
        /// <param name="part"></param>
        /// <returns></returns>
        private static ModuleRoboticSlave TryGetSlaveModule(Part part)
        {
            ModuleRoboticSlave slave = TryGetModule<ModuleRoboticSlave>(part);
            if (slave == null) return null; // nope, no slave module

            // We have our prospective slave, but let's make sure it's actually
            // functional before we return it. If someone borked a part by putting
            // a slave module where it doesn't belong, don't treat it as a slave.
            return (slave.Servo == null) ? null : slave;
        }


        /// <summary>
        /// Find a module of the specified type on the current part, or null if not found.
        /// </summary>
        /// <param name="part"></param>
        /// <returns></returns>
        private static T TryGetModule<T>(Part part) where T : PartModule
        {
            if (part == null) return null;
            for (int i = 0; i < part.Modules.Count; ++i)
            {
                T module = part.Modules[i] as T;
                if (module != null) return module;
            }
            return null;
        }

        /// <summary>
        /// Create a SlavedFieldPair based on the provided names.
        /// </summary>
        /// <param name="unslavedField"></param>
        /// <param name="slavedField"></param>
        /// <returns></returns>
        private SlavedFieldPair CreateSlavedFieldPair(string unslavedField, string slavedField)
        {
            return new SlavedFieldPair(Servo.Fields[unslavedField], Fields[slavedField]);
        }

        /// <summary>
        /// Useful for debug logging messages.
        /// </summary>
        /// <param name="part"></param>
        /// <returns></returns>
        private static string ToString(Part part)
        {
            return (part == null) ? "NULL" : (part.name + " " + part.persistentId);
        }

        /// <summary>
        /// Useful for debug logging messages.
        /// </summary>
        /// <param name="part"></param>
        /// <returns></returns>
        private static String ToString(ModuleRoboticServoHinge servo)
        {
            return (servo == null) ? "NULL" : ToString(servo.part);
        }


        /// <summary>
        /// Given a part, determine what effect it should have on propagation.
        /// </summary>
        /// <param name="part">The part to analyze.</param>
        /// <param name="localServo">The servo module on the part, if present; null otherwise.</param>
        /// <param name="slave">THe slave module on the part, if present; null otherwise.</param>
        /// <returns></returns>
        private static PropagationType PropagationTypeOf(
            Part part,
            out ModuleRoboticServoHinge localServo,
            out ModuleRoboticSlave slave)
        {
            localServo = null;
            slave = null;

            // Is it even a robotic part at all?
            BaseServo baseServo = TryGetModule<BaseServo>(part);
            if (baseServo == null) return PropagationType.Transparent;

            // Is it locked?
            if (baseServo.servoIsLocked) return PropagationType.Transparent;

            // Is it loose?
            if (!baseServo.servoIsMotorized || !baseServo.servoMotorIsEngaged) return PropagationType.Dead;

            // Is it a supported robotic type?
            localServo = baseServo as ModuleRoboticServoHinge;
            if (localServo == null) return PropagationType.Dead;

            // Is it slaveable?
            slave = TryGetSlaveModule(part);
            if (slave == null) return PropagationType.Assertive;

            // Is slave mode active?
            return slave.IsSlaveEnabled ? PropagationType.Transparent : PropagationType.Assertive;
        }


        /// <summary>
        /// Given a part, determine what effect it should have on propagation.
        /// </summary>
        /// <param name="part">The part to analyze.</param>
        /// <param name="localServo">The servo module on the part, if present; null otherwise.</param>
        /// <returns></returns>
        private static PropagationType PropagationTypeOf(Part part, out ModuleRoboticServoHinge localServo)
        {
            ModuleRoboticSlave dontCareSlave;
            return PropagationTypeOf(part, out localServo, out dontCareSlave);
        }


        /// <summary>
        /// Indicates how a part interacts with the propagation of master-slave chains.
        /// </summary>
        private enum PropagationType
        {
            /// <summary>
            /// The part allows chains to propagate through it unaffected. Non-robotic
            /// parts fall into this category.
            /// </summary>
            Transparent,

            /// <summary>
            /// The part interrupts chains. For example, robotic parts of an unsupported
            /// type, or free-flopping servos (i.e. unmotorized).
            /// </summary>
            Dead,

            /// <summary>
            /// The part intrudes on chains by specifying *itself* as the master.
            /// </summary>
            Assertive
        }


        #region private class SlavedFieldPair
        /// <summary>
        /// Keeps track of a pair of PAW fields, such that exactly one of them is ever on
        /// display at one time, depending on whether we're in slave mode or not.
        /// </summary>
        private class SlavedFieldPair
        {
            public readonly BaseField unslavedField;
            public readonly BaseField slavedField;
            public readonly float unslavedMinValue; // NaN if not a float range
            public readonly float unslavedMaxValue; // NaN if not a float range

            /// <summary>
            /// Constructor.
            /// </summary>
            /// <param name="unslavedField"></param>
            /// <param name="slavedField"></param>
            public SlavedFieldPair(BaseField unslavedField, BaseField slavedField)
            {
                this.unslavedField = unslavedField;
                this.slavedField = slavedField;
                this.slavedField.guiName = this.unslavedField.guiName;

                UI_FloatRange floatRange = unslavedField.uiControlEditor as UI_FloatRange;
                if (floatRange == null)
                {
                    unslavedMinValue = unslavedMaxValue = float.NaN;
                }
                else
                {
                    unslavedMinValue = floatRange.minValue;
                    unslavedMaxValue = floatRange.maxValue;
                }
            }

            /// <summary>
            /// Set the specified value on both the slaved and the unslaved fields.
            /// </summary>
            /// <param name="newValue"></param>
            public void SetValue(object newValue)
            {
                unslavedField.SetValue(newValue, unslavedField.host);
                slavedField.SetValue(newValue, slavedField.host);
            }

            /// <summary>
            /// Set which mode it's in.
            /// </summary>
            /// <param name="isSlaveMode"></param>
            public void ConfigureGui(bool isSlaveMode)
            {
                unslavedField.guiActive = unslavedField.guiActiveEditor = !isSlaveMode;
                slavedField.guiActive = slavedField.guiActiveEditor = isSlaveMode;
            }
        }
        #endregion // private class SlavedFieldPair


        #region private class ChangeTracker
        private class ChangeTracker<T> where T : IComparable<T>
        {
            public delegate void ChangeHandler(ModuleRoboticSlave host);

            private bool isInitialized;
            private T lastValue;
            private ChangeHandler handler;

            public ChangeTracker(ChangeHandler handler)
            {
                isInitialized = false;
                this.handler = handler;
            }

            public void Reset()
            {
                isInitialized = false;
            }

            public void Update(T newValue, ModuleRoboticSlave host)
            {
                if (!isInitialized)
                {
                    isInitialized = true;
                    lastValue = newValue;
                    handler(host);
                }
                else
                {
                    if (newValue.CompareTo(lastValue) != 0)
                    {
                        lastValue = newValue;
                        handler(host);
                    }
                }
            }
        }
        #endregion // private class ChangeTracker
    } // class ModuleRoboticSlave
}
