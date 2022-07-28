using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using DynamicExpresso;
using SimpleJSON;
using UnityEngine;
using UniRx;
using UniRx.Triggers;
using Microsoft.MixedReality.Toolkit.Utilities;
using Microsoft.MixedReality.Toolkit;
using Microsoft.MixedReality.Toolkit.Input;

namespace DxR.VisMorphs
{
    [Serializable]
    public class MorphSpecification
    {
        public TextAsset Json;
        public bool Enabled = true;
    }

    public class MorphManager : MonoBehaviour
    {
        public static MorphManager Instance { get; private set; }

        public bool DebugSignals;
        public List<MorphSpecification> MorphJsonSpecifications;

        public List<Morph> Morphs = new List<Morph>();
        public static Dictionary<string, Interpreter> Interpreters = new Dictionary<string, Interpreter>();

        private Dictionary<string, IObservable<dynamic>> GlobalSignalObservables = new Dictionary<string, IObservable<dynamic>>();
        private static CompositeDisposable disposables;

        private readonly string[] tagNames = new string[] { "DxRVis", "DxRMark", "DxRAxis", "DxRLegend", "Surface" };
        private static MouseObservablesHelper mouseObservablesHelper;
        private static MRTKObservablesHelper mrtkObservablesHelper;
        private static GameObjectObservablesHelper gameObjectObservablesHelper;

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(this);
            }
            else
            {
                Instance = this;
            }

            ReadMorphJsonSpecifications();
        }

        public void ReadMorphJsonSpecifications()
        {
            // If there are already morph specs defined (i.e., this is being called more than once), delete the previous ones
            if (Morphs.Count > 0)
            {
                ResetMorphSpecifications();
            }

            // Initialise variables
            if (disposables == null)
                disposables = new CompositeDisposable();

            foreach (MorphSpecification morphSpecification in MorphJsonSpecifications)
            {
                if (morphSpecification.Enabled)
                {
                    JSONNode morphSpec = JSON.Parse(morphSpecification.Json.text);
                    string morphName = morphSpec["name"] != null ? morphSpec["name"] : morphSpecification.Json.name;
                    ReadMorphSpecification(morphSpec, morphName);
                }
            }

            if (Morphs.Count > 0)
            {
                foreach (Morphable morphable in GameObject.FindObjectsOfType<Morphable>())
                {
                    morphable.CheckForMorphs();
                }
            }

            Debug.Log(string.Format("Vis Morphs: {0} morphs loaded: {1}", Morphs.Count, string.Join(", ", Morphs.Select(m => m.Name))));
        }

        private void ResetMorphSpecifications()
        {
            Morphs.Clear();
            GlobalSignalObservables.Clear();
            disposables.Clear();

            foreach (Morphable morphable in GameObject.FindObjectsOfType<Morphable>())
            {
                morphable.Reset(false);
            }

            gameObjectObservablesHelper.ClearGameObjectObservables();
        }

        private void ReadMorphSpecification(JSONNode morphSpec, string morphName)
        {
            // We need to initialise three separate things: states, signals, and transitions
            Morph newMorph = new Morph();
            newMorph.Name = morphName;

            ReadStatesSpecification(newMorph, morphSpec);
            ReadSignalsSpecification(newMorph, morphSpec);
            ReadTransitionsSpecification(newMorph, morphSpec);

            Morphs.Add(newMorph);
        }

        #region States

        private void ReadStatesSpecification(Morph morph, JSONNode morphSpec)
        {
            JSONNode statesSpec = morphSpec["states"];
            if (statesSpec != null)
            {
                foreach (JSONNode stateSpec in statesSpec.Children)
                {
                    morph.States.Add(stateSpec);
                }
            }
            else
            {
                Debug.LogWarning(string.Format("Vis Morphs: No state specification has been provided for Morph {0}. Is this correct?", morph.Name));
            }
        }

        #endregion States

        #region Signals

        private void ReadSignalsSpecification(Morph morph, JSONNode morphSpec)
        {
            // Initialise our helper classes which will create and re-use observables
            if (mouseObservablesHelper == null)
            {
                mouseObservablesHelper = new MouseObservablesHelper();
                mrtkObservablesHelper = new MRTKObservablesHelper();
                gameObjectObservablesHelper = new GameObjectObservablesHelper();
            }

            JSONNode signalsSpec = morphSpec["signals"];
            if (signalsSpec != null)
            {
                foreach (JSONNode signalSpec in signalsSpec.Children)
                {
                    JSONNode signalSpecInferred = ValidateAndInferSignal(signalSpec);

                    string signalName = signalSpecInferred["name"];

                    /// We handle signals differently depending on whether it is a global or local signal
                    /// Global signals are those that can easily be shared across multiple visualisations (e.g., controller events with no targets)
                    /// Local signals are those that are specific to a visualisation and its componnts (e.g., the vis's rotation, a targeted mark)
                    ///     We also consider expressions to be local, at least for now
                    /// This script will handle global signals, but each Morphable will need to create these local signals independently
                    if (IsSignalGlobal(signalSpecInferred))
                    {
                        IObservable<dynamic> observable = CreateObservableFromSpec(signalSpecInferred);
                        SaveGlobalSignal(signalName, observable);
                        morph.GlobalSignals.Add(signalSpecInferred);
                    }
                    /// We store a collection of local signal specs which each Morphable will need to create
                    else
                    {
                        morph.LocalSignals.Add(signalSpecInferred);
                    }
                }
            }
            else
            {
                Debug.LogWarning(string.Format("Vis Morphs: No signal specification has been provided for Morph {0}. Is this correct?", morph.Name));
            }
        }

        /// <summary>
        /// Checks and validates the base properties of the signal spec. There may be other validation rules specific to some configurations however.
        /// We allow these to be resolved as the signals are being created
        /// Returns the inferred signal spec
        /// </summary>
        private JSONNode ValidateAndInferSignal(JSONNode signalSpec)
        {
            JSONNode signalSpecInferred = signalSpec.Clone();

            // All signals need to have a name
            if (signalSpec["name"] == null)
                throw new Exception("Vis Morphs: All signals need to have a name property.");

            // The name cannot be blank
            if (signalSpec["name"].Value.Trim() == "")
                throw new Exception("Vis Morphs: Signal names cannot be blank.");

            // Expression signals can only have a name and the "expression" property, and no others
            if (signalSpec["expression"] != null)
            {
                if (signalSpec.Children.Count() > 2)
                    throw new Exception(string.Format("Vis Morphs: The Signal \"{0}\" has an expression property but contains other non-expression related properties.", signalSpec["name"]));
            }
            // Otherwise it is a regular Signal. Perform checks for this
            else
            {
                // All non-expression Signals need to have a source property
                if (signalSpec["source"] == null)
                    throw new Exception(string.Format("Vis Morphs: The Signal \"{0}\" does not have a source property specified.", signalSpec["name"]));

                // Mouse, hand, and controller sources should have a handedness property
                if (signalSpec["source"] == "mouse" || signalSpec["source"] == "hand" || signalSpec["source"] == "controller")
                {
                    // If the handedness property doesn't exist, infer a new one (default to right hand)
                    if (signalSpec["handedness"] == null)
                    {
                        signalSpecInferred.Add("handedness", "right");
                    }
                    // If it does exist, make sure that it is either left or right
                    // TODO: Make this support "any"
                    else
                    {
                        if (!(signalSpec["handedness"] == "left" || signalSpec["handedness"] == "right"))
                            throw new Exception(string.Format("Vis Morphs: The Signal \"{0}\" has an invalid handedness, \"{1}\" given.", signalSpec["name"], signalSpec["handedness"]));
                    }
                }

                // If a target is defined, check to see if it matches one of the tags and change it to it, in order to account for prefixes and case-sensititivity
                if (signalSpec["target"] != null)
                {
                    string target = signalSpec["target"];

                    foreach (string tag in tagNames)
                    {
                        // For now this uses Contains(). This might cause some false positives
                        if (tag.ToLower().Contains(target))
                        {
                            target = tag;
                            break;
                        }
                    }

                    signalSpecInferred["target"] = target;
                }
            }

            return signalSpecInferred;
        }

        /// <summary>
        /// Returns true if the signal is global, or false if it is local
        /// </summary>
        private bool IsSignalGlobal(JSONNode signalSpec)
        {
            // Expressions are always local
            if (signalSpec["expression"] != null)
                return false;

            // The only Signals that are global are those which:
            //  a. Are not a "Vis" source; and
            //  b. Don't target anything
            if (signalSpec["source"] == "vis")
                return false;

            if (signalSpec["target"] != "none")
                return false;

            return true;
        }

        /// <summary>
        /// Saves a given signal such that it can be accessed later on, typically by external Morphable scripts
        /// </summary>
        private void SaveGlobalSignal(string name, IObservable<dynamic> observable)
        {
            // Make the signal a ReplaySubject which returns the most recently emitted item as soon as it is subscribed to
            observable = observable.Replay(1).RefCount().DistinctUntilChanged();

            // Subscribe to both force the signal to behave as a hot observable, and also for debugging purposes
            observable.DistinctUntilChanged().Subscribe(_ =>
            {
                if (DebugSignals)
                    Debug.Log("Global Signal " + name + ": " + _);
            });

            if (!GlobalSignalObservables.ContainsKey(name))
            {
                GlobalSignalObservables.Add(name, observable);
            }
            else
            {
                Debug.LogWarning(string.Format("Vis Morphs: A global signal with the name {0} already exists and therefore has not been overwritten. Is this intentional?", name));
            }
        }

        public IObservable<dynamic> GetGlobalSignal(string name)
        {
            if (GlobalSignalObservables.TryGetValue(name, out IObservable<dynamic> observable))
            {
                return observable;
            }

            return null;
        }

        /// <summary>
        /// Creates an observable based off of a given signal spec.
        ///
        /// This function first categorises signals into two types:
        /// 1. Expression based signals
        /// 2. Source based signals
        ///
        /// Source based signals are then categorised into the following six types:
        /// 1. Mouse based signals that are targetless (e.g., mouse position, left/mouse button presses)
        /// 2. Mouse based signals that are targeted (e.g., clicked target)
        /// 3. Hand based sources that are targetless (e.g., hand position, arbitrary controller button presses)
        /// 4. Hand based sources that compare themselves to a target (e.g., grabbed object, surface which the hand touched)
        /// 5. Object based sources that are targetless (e.g., head direction, vis rotation)
        /// 6. Object based sources that compare themselves to a target (e.g., distance between head and vis, closest surface to vis)
        ///
        /// There are a lot of these types for multiple reasons:
        /// a. Mouse sources are on a two-dimensional plane versus the three-dimensional space of hand and object sources
        /// b. Hand sources rely on MRTK which is not really user friendly in getting state
        /// c. Targetless signals involve only one variable whereas targeted involve two
        ///
        /// These differences mean that for now it's easier just to separate these out.
        ///
        /// TODO: Make this not suck, or at least just make it more modular and cool so that it leverages functional reactive programming concepts more
        /// </summary>
        public static IObservable<dynamic> CreateObservableFromSpec(JSONNode signalSpec, Morphable morphable = null)
        {
            if (signalSpec["expression"] != null)
            {
                if (morphable != null)
                {
                    return CreateObservableFromExpression(signalSpec["expression"], morphable);
                }
                else
                {
                    return null;
                }
            }

            string source = signalSpec["source"];
            string target = signalSpec["target"];

            if (source == "mouse")
            {
                if (target == null || target == "none")
                {
                    return CreateMouseUntargetedObservable(signalSpec, morphable);
                }
                else
                {
                    return CreateMouseTargetedObservable(signalSpec, morphable);
                }
            }
            else if (source == "controller" || source == "hand")
            {
                if (target == null ||target == "none")
                {
                    return CreateControllerUntargetedObservable(signalSpec, morphable);
                }
                else
                {
                    return CreateControllerTargetedObservable(signalSpec, morphable);
                }
            }
            else
            {
                if (target == null ||target == "none")
                {
                    return CreateObservableFromObjectTargetless(signalSpec, morphable);
                }
                else
                {
                    return CreateObservableFromObjectTargeted(signalSpec, morphable);
                }
            }
        }

        public static IObservable<dynamic> CreateMouseUntargetedObservable(JSONNode signalSpec, Morphable morphable = null)
        {
            string handedness = signalSpec["handedness"];
            string value = signalSpec["value"];

            switch (value)
            {
                case "position":
                    return mouseObservablesHelper.GetMousePositionObservable().Select(_ => (dynamic)_);

                case "press":
                    return mouseObservablesHelper.GetMouseButtonPressedObservable(handedness).Select(_ => (dynamic)_);

                case "click":
                    return mouseObservablesHelper.GetMouseButtonClickedObservable(handedness).Select(_ => (dynamic)_);

                default:
                    throw new Exception(string.Format("Vis Morphs: The Signal \"{0}\" is a targetless mouse source with an unsupported value property.", signalSpec["name"]));
            }
        }

        public static IObservable<dynamic> CreateMouseTargetedObservable(JSONNode signalSpec, Morphable morphable = null)
        {
            string handedness = signalSpec["handedness"];
            string target = signalSpec["target"];
            string criteria = signalSpec["criteria"];
            string value = signalSpec["value"];

            // Get an observable that performs raycasts
            IObservable<RaycastHit[]> raycastHitObservable = mouseObservablesHelper.GetMouseRaycastHitsObservable();
            // Filter this raycast hits observable depending on the handedness, target, and criteria
            IObservable<GameObject> targetObservable = null;

            switch (criteria)
            {
                case "touch":
                    targetObservable = raycastHitObservable.Select(hits =>
                        {
                            return hits.Where(hit => hit.collider.transform.tag == target)
                                    .Where(hit => hit.transform.GetComponentInParent<Morphable>() == morphable) // TODO: This filtering should only be done if the Target is something that is Vis specific (i.e., vis, mark, axis, legend)
                                    .Select(hit => hit.collider.transform.gameObject).FirstOrDefault();
                        });
                    break;

                case "select":
                    // Get the observable for mouse button presses
                    IObservable<bool> buttonPressedObservable = mouseObservablesHelper.GetMouseButtonPressedObservable(handedness);
                    targetObservable = buttonPressedObservable
                        .WithLatestFrom(raycastHitObservable, (pressed, hits) =>
                        {
                            if (pressed)
                                return hits.Where(hit => hit.collider.transform.tag == target)
                                           .Where(hit => hit.transform.GetComponentInParent<Morphable>() == morphable)
                                           .Select(hit => hit.collider.transform.gameObject).FirstOrDefault();

                            return null;
                        });
                    break;

                case "click":
                    // Get the observable for mouse button is clicked
                    IObservable<bool> buttonClickedObservable = mouseObservablesHelper.GetMouseButtonClickedObservable(handedness);
                    targetObservable = buttonClickedObservable
                        .WithLatestFrom(raycastHitObservable, (clicked, hits) =>
                        {
                            if (clicked)
                                return hits.Where(hit => hit.collider.transform.tag == target)
                                        .Where(hit => hit.transform.GetComponentInParent<Morphable>() == morphable)
                                        .Select(hit => hit.collider.transform.gameObject).FirstOrDefault();

                            return null;
                        });
                    break;

                case null:
                    {
                        // If no criteria is defined, assume that the target is some specific object
                        GameObject criteriaGameObject = null;
                        switch (target)
                        {
                            case "head":
                                criteriaGameObject = CameraCache.Main.gameObject;
                                break;
                            case "vis":
                            case "DxRVis":  // Targets get converted to specific names if they match a tag. This is a workaround for now
                                criteriaGameObject = morphable.gameObject;
                                break;
                            default:
                                criteriaGameObject = GameObject.Find(target);
                                break;
                        }
                        targetObservable = Observable.EveryUpdate().Select(_ => criteriaGameObject);
                        break;
                    }

                default:
                    throw new Exception(string.Format("Vis Morphs: The Signal \"{0}\" is a targeted mouse source with an unsupported criteria property.", signalSpec["name"]));
            }

            return CreateValueObservableFromTarget(signalSpec, targetObservable, morphable);
        }

        public static IObservable<dynamic> CreateControllerUntargetedObservable(JSONNode signalSpec, Morphable morphable = null)
        {
            string source = signalSpec["source"];
            Handedness handedness = signalSpec["handedness"] == "left" ? Handedness.Left : Handedness.Right;
            string value = signalSpec["value"];

            switch (value)
            {
                case "select":
                    return mrtkObservablesHelper.GetControllerSelectObservable(handedness).Select(_ => (dynamic)_);

                case "pinch":
                    return mrtkObservablesHelper.GetControllerSelectObservable(handedness).Select(f => (dynamic)(f > 0.8f));

                case "position":
                    return mrtkObservablesHelper.GetControllerGameObjectObservable(handedness).Select(controller => (dynamic)controller.transform.position);

                default:
                    throw new Exception(string.Format("Vis Morphs: The Signal \"{0}\" is a targetless hand or controller source with an unsupported value property.", signalSpec["name"]));
            }
        }

        public static IObservable<dynamic> CreateControllerTargetedObservable(JSONNode signalSpec, Morphable morphable = null)
        {
            Handedness handedness = signalSpec["handedness"] == "left" ? Handedness.Left : Handedness.Right;
            string target = signalSpec["target"];
            string criteria = signalSpec["criteria"];
            string value = signalSpec["value"];

            // Find the target based on the specified target and criteria
            IObservable<GameObject> targetObservable = null;
            switch (criteria)
            {
                case "touch":
                    {
                        IObservable<Collider[]> touchingGameObjectsObservable = mrtkObservablesHelper.GetControllerTouchingGameObjectsObservable(handedness);
                        targetObservable = touchingGameObjectsObservable.Select(colliders =>
                        {
                            return colliders.Where(collider => collider.tag == target)
                                            .Where(collider => collider.GetComponentInParent<Morphable>() == morphable)
                                            .Select(collider => collider.gameObject)
                                            .FirstOrDefault();
                        });
                        break;
                    }

                case "select":
                    {
                        IObservable<Collider[]> touchingGameObjectsObservable = mrtkObservablesHelper.GetControllerTouchingGameObjectsObservable(handedness);
                        IObservable<float> selectObservable = mrtkObservablesHelper.GetControllerSelectObservable(handedness).Where(s => s > 0.8f);
                        targetObservable = selectObservable.WithLatestFrom(touchingGameObjectsObservable, (f, colliders) =>
                        {
                            return colliders.Where(collider => collider.tag == target)
                                            .Where(collider => collider.GetComponentInParent<Morphable>() == morphable)
                                            .Select(collider => collider.gameObject)
                                            .FirstOrDefault();
                        });
                        break;
                    }

                case "point":
                    {
                        IObservable<RaycastHit[]> pointingGameObjectsObservable = mrtkObservablesHelper.GetControllerPointingGameObjectsObservable(handedness);
                        targetObservable = pointingGameObjectsObservable.Select(hits =>
                        {
                            return hits.Where(hit => hit.collider.tag == target)
                                       .Where(hit => hit.transform.GetComponentInParent<Morphable>() == morphable)
                                       .Select(hit => hit.collider.gameObject)
                                       .FirstOrDefault();
                        });
                        break;
                    }

                case "closest":
                    {
                        IObservable<GameObject[]> proximityGameObjectsObservable = mrtkObservablesHelper.GetControllerProximityGameObjectsObservable(handedness, target);
                        targetObservable = proximityGameObjectsObservable.Select(gameObjects => gameObjects.FirstOrDefault());
                        break;
                    }

                case "farthest":
                    {
                        IObservable<GameObject[]> proximityGameObjectsObservable = mrtkObservablesHelper.GetControllerProximityGameObjectsObservable(handedness, target);
                        targetObservable = proximityGameObjectsObservable.Select(gameObjects => gameObjects.LastOrDefault());
                        break;
                    }

                case null:
                    {
                        // If no criteria is defined, assume that the target is some specific object
                        GameObject criteriaGameObject = null;
                        switch (target)
                        {
                            case "head":
                                criteriaGameObject = CameraCache.Main.gameObject;
                                break;
                            case "vis":
                            case "DxRVis":  // Targets get converted to specific names if they match a tag. This is a workaround for now
                                criteriaGameObject = morphable.gameObject;
                                break;
                            default:
                                criteriaGameObject = GameObject.Find(target);
                                break;
                        }
                        targetObservable = Observable.EveryUpdate().Select(_ => criteriaGameObject);
                        break;
                    }
            }

            return CreateComparisonValueObservableFromControllerTarget(signalSpec, mrtkObservablesHelper.GetControllerGameObjectObservable(handedness), targetObservable, morphable);
        }


        public static IObservable<dynamic> CreateComparisonValueObservableFromControllerTarget(JSONNode signalSpec, IObservable<GameObject> controllerObservable, IObservable<GameObject> targetObservable, Morphable morphable = null)
        {
            string value = signalSpec["value"];

            switch (value)
            {
                case "distance":
                    return targetObservable.Where(_ => _ != null).WithLatestFrom(controllerObservable, (target, controller) =>
                        {
                            return (dynamic)Vector3.Distance(controller.transform.position, target.transform.position);
                        });

                case "closestdistance":
                    return targetObservable.Where(_ => _ != null).WithLatestFrom(controllerObservable, (target, controller) =>
                        {
                            Collider A = controller.GetComponentInChildren<Collider>();
                            Collider B = target.GetComponent<Collider>();
                            Vector3 ptA = B.ClosestPoint(A.transform.position);
                            return (dynamic)Vector3.Distance(ptA, controller.transform.position);
                        });

                case "angle":
                    return targetObservable.Where(_ => _ != null).WithLatestFrom(controllerObservable, (target, controller) =>
                        {
                            return (dynamic)Vector3.Angle(controller.transform.forward, target.transform.forward);
                        });

                case "intersection":
                    {
                    return targetObservable.Where(_ => _ != null).WithLatestFrom(controllerObservable, (target, controller) =>
                        {
                            Collider A = controller.GetComponentInChildren<Collider>();
                            Collider B = target.GetComponent<Collider>();
                            Vector3 ptA = B.ClosestPoint(A.transform.position);
                            Vector3 ptB = A.ClosestPoint(B.transform.position);
                            Vector3 ptM = ptA + (ptB - ptA) / 2;
                            Vector3 closestAtB = B.ClosestPoint(ptM);
                            return (dynamic)closestAtB;
                        });
                    }

                default:
                    return CreateValueObservableFromTarget(signalSpec, targetObservable, morphable);
            }
        }

        public static IObservable<dynamic> CreateObservableFromObjectTargetless(JSONNode signalSpec, Morphable morphable = null)
        {
            string source = signalSpec["source"];
            string value = signalSpec["value"];

            // Since we can't really share observables here, we don't use the helper class
            GameObject sourceGameObject;

            // Get the source gameobject that is tied to the value specified in "source"
            // TODO: Maybe force the source property to be "gameobject" to be able to reference arbitrary gameobjects
            switch (source)
            {
                case "head":
                    sourceGameObject = CameraCache.Main.transform.gameObject;
                    break;
                case "vis":
                    sourceGameObject = morphable.gameObject;
                    break;
                default:
                    sourceGameObject = GameObject.Find(source);
                    break;
            }

            if (sourceGameObject == null)
                throw new Exception(string.Format("Vis Morphs: Could not find any source GameObject with name \"{0}\".", source));

            // Leverage this function to access values from our object
            return CreateValueObservableFromTarget(signalSpec, gameObjectObservablesHelper.GetGameObjectObservable(sourceGameObject), morphable);
        }

        public static IObservable<dynamic> CreateObservableFromObjectTargeted(JSONNode signalSpec, Morphable morphable = null)
        {
            string source = signalSpec["source"];
            string target = signalSpec["target"];
            string criteria = signalSpec["criteria"];
            string value = signalSpec["value"];

            // TODO: Create a helper class to allow for sharing of common observables for each Morphable
            GameObject sourceGameObject;

            // Get the source gameobject that is tied to the value specified in "source"
            switch (source)
            {
                case "head":
                    sourceGameObject = CameraCache.Main.transform.gameObject;
                    break;
                case "vis":
                    sourceGameObject = morphable.gameObject;
                    break;
                default:
                    sourceGameObject = GameObject.Find(source);
                    break;
            }

            if (sourceGameObject == null)
                throw new Exception(string.Format("Vis Morphs: Could not find any source GameObject with name \"{0}\".", source));

            // Find the object that the source is targetting
            IObservable<GameObject> targetObservable = null;
            switch (criteria)
            {
                case "touch":
                    {
                        // Get the shared observable for this sourceGameObject
                        IObservable<Collider[]> overlapBoxObservable = gameObjectObservablesHelper.GetGameObjectOverlapBoxObservable(sourceGameObject);
                        targetObservable = overlapBoxObservable.Select(colliders =>
                            {
                                return colliders.Where(collider => collider.tag == target)
                                                .Select(collider => collider.gameObject)
                                                .FirstOrDefault();
                            });
                        break;
                    }

                case "point":
                    {
                        // Get the shared observable for this sourceGameObject
                        IObservable<RaycastHit[]> raycastHitObservable = gameObjectObservablesHelper.GetGameObjectRaycastHitObservable(sourceGameObject);
                        targetObservable = raycastHitObservable.Select(hits =>
                            {
                                return hits.Where(hit => hit.collider.tag == target)
                                           .Where(hit => hit.transform.GetComponentInParent<Morphable>() == morphable)
                                           .Select(hit => hit.collider.gameObject)
                                           .FirstOrDefault();
                            });
                        break;
                    }

                case null:
                    {
                        // If no criteria is defined, assume that the target is some specific object
                        GameObject criteriaGameObject = null;
                        switch (target)
                        {
                            case "head":
                                criteriaGameObject = CameraCache.Main.gameObject;
                                break;
                            case "vis":
                            case "DxRVis":  // Targets get converted to specific names if they match a tag. This is a workaround for now
                                criteriaGameObject = morphable.gameObject;
                                break;
                            default:
                                criteriaGameObject = GameObject.Find(target);
                                break;
                        }
                        targetObservable = gameObjectObservablesHelper.GetGameObjectObservable(criteriaGameObject);
                        break;
                    }

                default:
                    throw new Exception(string.Format("Vis Morphs: The Signal \"{0}\" is a targeted object source with an unsupported criteria property.", signalSpec["name"]));
            }

            return CreateComparisonValueObservableFromObjectTarget(signalSpec, sourceGameObject, targetObservable, morphable);
        }

        public static IObservable<dynamic> CreateComparisonValueObservableFromObjectTarget(JSONNode signalSpec, GameObject sourceGameObject, IObservable<GameObject> targetObservable, Morphable morphable = null)
        {
            string value = signalSpec["value"];

            switch (value)
            {
                case "distance":
                    {
                        return targetObservable.Where(_ => _ != null)
                                               .Select(target => (dynamic)Vector3.Distance(target.transform.position, sourceGameObject.transform.position));
                    }

                case "closestdistance":
                    {
                        Collider A = sourceGameObject.GetComponentInChildren<Collider>();
                        if (A != null)
                        {
                            return targetObservable.Where(_ => _ != null).Select(target =>
                                {
                                    Collider B = target.GetComponent<Collider>();
                                    Vector3 ptA = B.ClosestPoint(A.transform.position);
                                    Vector3 ptB = A.ClosestPoint(B.transform.position);
                                    return (dynamic)Vector3.Distance(ptA, ptB);
                                });
                        }
                        else
                        {
                            return targetObservable.Where(_ => _ != null).Select(target =>
                                {
                                    Collider B = target.GetComponent<Collider>();
                                    Vector3 ptA = B.ClosestPoint(sourceGameObject.transform.position);
                                    return (dynamic)Vector3.Distance(ptA, sourceGameObject.transform.position);
                                });
                        }
                    }

                case "angle":
                    {
                        return targetObservable.Where(_ => _ != null)
                                               .Select(target => (dynamic)Vector3.Angle(target.transform.forward, sourceGameObject.transform.forward));
                    }

                case "intersection":
                    {
                        Collider A = sourceGameObject.GetComponent<Collider>();
                        if (A != null)
                        {
                            return targetObservable.Where(_ => _ != null).Select(target =>
                            {
                                Collider B = target.GetComponent<Collider>();
                                Vector3 ptA = B.ClosestPoint(A.transform.position);
                                Vector3 ptB = A.ClosestPoint(B.transform.position);
                                Vector3 ptM = ptA + (ptB - ptA) / 2;
                                Vector3 closestAtB = B.ClosestPoint(ptM);
                                return (dynamic)closestAtB;
                            });
                        }
                        else
                        {
                            return targetObservable.Where(_ => _ != null).Select(target =>
                            {
                                Collider B = target.GetComponent<Collider>();
                                return (dynamic)B.ClosestPoint(sourceGameObject.transform.position);
                            });
                        }

                    }

                default:
                    return CreateValueObservableFromTarget(signalSpec, targetObservable, morphable);
            }
        }

        public static IObservable<dynamic> CreateValueObservableFromTarget(JSONNode signalSpec, IObservable<GameObject> targetObservable, Morphable morphable = null)
        {
            string target = signalSpec["target"];
            string value = signalSpec["value"];

            // Certain targets may support unique value types. Check for these first
            switch (target)
            {
                case "mark":
                case "axis":
                case "legend":
                case "surface":
                    break;
            }

            // If not, use one of the built-in Unity properties instead
            switch (value)
            {
                case "position":
                    return targetObservable.Where(_ => _ != null).Select((GameObject target) => (dynamic)target.transform.position);

                case "localposition":
                    return targetObservable.Where(_ => _ != null).Select((GameObject target) => (dynamic)target.transform.localPosition);

                case "rotation":
                    return targetObservable.Where(_ => _ != null).Select((GameObject target) => (dynamic)target.transform.rotation);

                case "localrotation":
                    return targetObservable.Where(_ => _ != null).Select((GameObject target) => (dynamic)target.transform.localRotation);

                case "eulerangles":
                    return targetObservable.Where(_ => _ != null).Select((GameObject target) => (dynamic)target.transform.eulerAngles);

                case "localeulerangles":
                    return targetObservable.Where(_ => _ != null).Select((GameObject target) => (dynamic)target.transform.localEulerAngles);

                case "scale":
                    return targetObservable.Where(_ => _ != null).Select((GameObject target) => (dynamic)target.transform.localScale);

                case "forward":
                    return targetObservable.Where(_ => _ != null).Select((GameObject target) => (dynamic)target.transform.forward);

                case "up":
                    return targetObservable.Where(_ => _ != null).Select((GameObject target) => (dynamic)target.transform.up);

                case "right":
                    return targetObservable.Where(_ => _ != null).Select((GameObject target) => (dynamic)target.transform.right);

                case "boolean":
                    return targetObservable.Select(_ => (dynamic)_ != null);

                // If the specified value does not match one of our pre-defined ones, use reflection to get the property
                default:
                {
                    if (target.GetPropValue(value) != null)
                    {
                        return targetObservable.Where(_ => _ != null).Select((GameObject target) => (dynamic)target.GetPropValue(value));
                    }
                    else
                    {
                        throw new Exception(string.Format("Vis Morphs: The Signal \"{0}\" has an unsupported value property. If using reflection, make sure the path is correct.", signalSpec["name"]));
                    }
                }
            }
        }

        private static IObservable<dynamic> CreateObservableFromExpression(string expression, Morphable morphable)
        {
            // Get the expression interpreter for this morphable. If it does not yet exist, initialise a new one
            Interpreter interpreter;

            if (!Interpreters.TryGetValue(morphable.GUID, out interpreter))
                interpreter = InitialiseExpressionInterpreter(morphable.GUID);

            // 1. Find all observables that this expression references
            // 2. When any of these emits:
            //      a. Update the variable on the interpreter
            //      b. Evaluate the interpreter expression
            //      c. Emit a new value

            // Iterate through all Signals that this expression references, checking both global and local signals
            List<IObservable<dynamic>> signalObservables = new List<IObservable<dynamic>>();

            foreach (KeyValuePair<string, IObservable<dynamic>> kvp in MorphManager.Instance.GlobalSignalObservables)
            {
                string signalName = kvp.Key;
                if (expression.Contains(signalName))
                {
                    IObservable<dynamic> observable = kvp.Value;
                    // When this Signal emits, we want to first ensure its variable is updated in the interpreter
                    var newObservable = observable.Select(x =>
                    {
                        // This will be repeated when multiple expressions reference the same Signal,
                        // but it should be okay performance wise I think
                        interpreter.SetVariable(signalName, x);
                        return x;
                    });
                    newObservable.Subscribe().AddTo(disposables);  // We need to have this subscribe here otherwise later selects don't work, for some reason

                    signalObservables.Add(newObservable);
                }
            }

            foreach (CandidateMorph candidateMorph in morphable.CandidateMorphs)
            {
                foreach (KeyValuePair<string, IObservable<dynamic>> kvp in candidateMorph.LocalSignalObservables)
                {
                    string signalName = kvp.Key;
                    if (expression.Contains(signalName))
                    {
                        IObservable<dynamic> observable = kvp.Value;
                        // When this Signal emits, we want to first ensure its variable is updated in the interpreter
                        var newObservable = observable.Select(x =>
                        {
                            // This will be repeated when multiple expressions reference the same Signal,
                            // but it should be okay performance wise I think
                            interpreter.SetVariable(signalName, x);
                            return x;
                        });
                        newObservable.Subscribe().AddTo(candidateMorph.Disposables);  // We need to have this subscribe here otherwise later selects don't work, for some reason

                        signalObservables.Add(newObservable);
                    }
                }
            }

            if (signalObservables.Count > 0)
            {
                var expressionObservable = signalObservables[0];

                for (int i = 1; i < signalObservables.Count; i++)
                {
                    expressionObservable = expressionObservable.Merge(signalObservables[i]);
                }

                return expressionObservable.Select(_ => {
                    try
                    {
                        return interpreter.Eval(expression);
                    }
                    // Sometimes a signal upstream would not have sent a value yet before the expression is evaluated. This try-catch prevents that
                    catch (DynamicExpresso.Exceptions.UnknownIdentifierException e)
                    {
                        Debug.LogWarning(e.ToString());
                        return null;
                    }
                }).Where(_ => _ != null)
                  .DistinctUntilChanged();
            }
            else
            {
                return Utils.CreateAnonymousObservable(interpreter.Eval(expression));
            }
        }

        /// <summary>
        /// Initialises the defined functions that are part of the DynamicExpresso expression interpreter.
        /// Each Morphable requires its own interpreter to evaluate expressions, as each one may have variables with the
        /// same name which overlap with one another.
        /// </summary>
        private static Interpreter InitialiseExpressionInterpreter(string guid)
        {
            if (Interpreters.ContainsKey(guid))
                throw new Exception(string.Format("Vis Morphs: There already exists a DynamicExpresso interpreter for the Morphable with guid {0}.", guid));

            Interpreter interpreter = new Interpreter();

            // Set types
            interpreter = interpreter.Reference(typeof(Vector3));
            interpreter = interpreter.Reference(typeof(Quaternion));
            interpreter = interpreter.Reference(typeof(Collider));

            // Set functions

            // Clamp
            Func<double, double, double, double> clamp = (input, min, max) => Math.Clamp(input, min, max);
            interpreter.SetFunction("clamp", clamp);

            // Normalise
            Func<double, double, double, double, double, double> normalise1 = (input, i0, i1, j0, j1) => Utils.NormaliseValue(input, i0, i1, j0, j1);
            Func<double, double, double, double> normalise2 = (input, i0, i1) => Utils.NormaliseValue(input, i0, i1);
            interpreter.SetFunction("normalise", normalise1);
            interpreter.SetFunction("normalise", normalise2);

            // Vector
            Func<double, double, double, Vector3> vector3 = (x, y, z) => new Vector3((float)x, (float)y, (float)z);
            interpreter.SetFunction("vector3", vector3);

            // Quaternion
            Func<double, double, double, double, Quaternion> quaternion = (x, y, z, w) => new Quaternion((float)x, (float)y, (float)z, (float)w);
            interpreter.SetFunction("quaternion", quaternion);

            // Vector
            Func<double, double, double, Quaternion> euler = (x, y, z) => Quaternion.Euler((float)x, (float)y, (float)z);
            interpreter.SetFunction("euler", euler);

            // Angle
            Func<Vector3, Vector3, Vector3, float> signedAngle = (from, to, axis) => Vector3.SignedAngle(from, to, axis);
            interpreter.SetFunction("signedangle", signedAngle);

            Func<Vector3, Vector3, float> angle = (from, to) => Vector3.Angle(from, to);
            interpreter.SetFunction("angle", angle);

            // Distance
            Func<Vector3, Vector3, float> distance = (a, b) => Vector3.Distance(a, b);
            interpreter.SetFunction("distance", distance);

            // Closest point
            Func<Collider, Vector3, Vector3> closestPoint = (collider, position) => collider.ClosestPoint(position);
            interpreter.SetFunction("closestpoint", closestPoint);

            // Look rotation
            Func<Vector3, Quaternion> lookRotation1 = (forward) => Quaternion.LookRotation(forward);
            interpreter.SetFunction("lookrotation", lookRotation1);

            Func<Vector3, Vector3, Quaternion> lookRotation2 = (forward, upwards) => Quaternion.LookRotation(forward, upwards);
            interpreter.SetFunction("lookrotation", lookRotation2);

            // Save this interpreter to the dictionary, matching it to the provided GUID
            Interpreters[guid] = interpreter;
            return interpreter;
        }

        public dynamic EvaluateExpression(Morphable morphable, string expression)
        {
            return EvaluateExpression(morphable.GUID, expression);
        }

        private static dynamic EvaluateExpression(string guid, string expression)
        {
            if (Interpreters.TryGetValue(guid, out Interpreter interpreter))
            {
                return interpreter.Eval(expression);
            }
            else
            {
                throw new Exception(string.Format("Vis Morphs: The expression interpreter for the Morphable with GUID {0} does not exist. This shouldn't happen.", guid));
            }
        }

        #endregion Signals

        #region Transitions

        private void ReadTransitionsSpecification(Morph morph, JSONNode morphSpec)
        {
            JSONNode transitionsSpec = morphSpec["transitions"];
            if (transitionsSpec != null)
            {
                foreach (JSONNode transitionSpec in transitionsSpec.Children)
                {
                    morph.Transitions.Add(transitionSpec);
                }
            }
            else
            {
                Debug.LogWarning(string.Format("Vis Morphs: No transition specification has been provided for Morph {0}. Is this correct?", morph.Name));
            }
        }

        #endregion Transitions

        public void ClearMorphableVariables(Morphable morphable)
        {
            if (Interpreters.ContainsKey(morphable.GUID))
                Interpreters.Remove(morphable.GUID);
        }

        public void EnableAllMorphs()
        {
            foreach (var morphSpec in MorphJsonSpecifications)
            {
                morphSpec.Enabled = true;
            }

            if (Application.isPlaying)
            {
                ReadMorphJsonSpecifications();
            }
        }

        public void DisableAllMorphs()
        {
            foreach (var morphSpec in MorphJsonSpecifications)
            {
                morphSpec.Enabled = false;
            }

            if (Application.isPlaying)
            {
                ReadMorphJsonSpecifications();
            }
        }
    }
}