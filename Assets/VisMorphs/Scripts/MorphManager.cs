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
        public bool DebugSignals;
        public List<MorphSpecification> MorphJsonSpecifications;

        public static MorphManager Instance { get; private set; }
        public static Dictionary<string, Interpreter> Interpreters = new Dictionary<string, Interpreter>();

        public List<Morph> Morphs = new List<Morph>();
        private Dictionary<string, IObservable<dynamic>> GlobalSignalObservables = new Dictionary<string, IObservable<dynamic>>();
        private static CompositeDisposable disposables;

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
            JSONNode signalsSpec = morphSpec["signals"];
            if (signalsSpec != null)
            {
                foreach (JSONNode signalSpec in signalsSpec.Children)
                {
                    string signalName = signalSpec["name"];
                    if (signalName == null)
                        throw new Exception("Vis Morphs: All signals need to have a name.");

                    /// We handle signals differently depending on whether it is a global or local signal
                    /// Global signals are those that can easily be shared across multiple visualisations (e.g., controller events)
                    /// Local signals are those that are specific to a visualisation (e.g., its position/rotation)
                    ///     We also consider expressions to be local, at least for now
                    /// This script will handle global signals, but each Morphable will need to create these local signals independently
                    if (IsSignalGlobal(signalSpec))
                    {
                        IObservable<dynamic> observable = CreateObservableFromSpec(signalSpec);
                        SaveGlobalSignal(signalName, observable);
                        morph.GlobalSignals.Add(signalSpec);
                    }
                    else
                    {
                        morph.LocalSignals.Add(signalSpec);
                    }
                }
            }
            else
            {
                Debug.LogWarning(string.Format("Vis Morphs: No signal specification has been provided for Morph {0}. Is this correct?", morph.Name));
            }
        }

        /// <summary>
        /// Returns true if the signal is global, or false if it is local
        /// </summary>
        private bool IsSignalGlobal(JSONNode signalSpec)
        {
            // For now, any signal that has to do with hand/controller input is considered global
            // TODO: Expand this to detect global/local expression-based signals too
            if (signalSpec["source"] != null &&
                (signalSpec["source"]["type"] == "hands" || signalSpec["source"]["type"] == "mouse"
                || signalSpec["source"]["type"] == "controller" || signalSpec["source"]["type"] == "gameobject"))
                return true;

            return false;
        }

        /// <summary>
        /// Saves a given signal such that it can be accessed later on, typically by external Morphable scripts
        /// </summary>
        private void SaveGlobalSignal(string name, IObservable<dynamic> observable)
        {
            // Make the signal a ReplaySubject which returns the most recently emitted item as soon as it is subscribed to
            observable = observable.Replay(1).RefCount();

            // Subscribe to both force the signal to behave as a hot observable, and also for debugging purposes
            observable.Subscribe(_ =>
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

        public static IObservable<dynamic> CreateObservableFromSpec(JSONNode signalSpec, Morphable morphable = null)
        {
            if (signalSpec["source"] != null)
            {
                JSONNode sourceSpec = signalSpec["source"];
                string sourceType = sourceSpec["type"] ?? "";        // What type of source is this observable?
                string reference = sourceSpec["ref"] ?? "";          // What is the name of the actual entity to get the value from?
                string selector = sourceSpec["select"] ?? "";        // What specific value from this entity do we then extract?

                switch (sourceType)
                {
                    case "mouse":
                        {
                            return CreateObservableFromMouseSpec(reference, selector);
                        }
                    case "controller":
                        {
                            return CreateObservableFromControllerSpec(reference, selector);
                        }

                    case "hands":
                        {
                            return CreateObservableFromHandSpec(reference, selector);
                        }

                    case "head":
                        {
                            return CreateObservableFromHeadSpec(reference, selector);
                        }

                    case "gameobject":
                        {
                            return CreateObservableFromGameObjectSpec(reference, selector);
                        }

                    case "vis":
                        {
                            // Creating an observable from a vis signal should only be done by a Morphable
                            if (morphable != null)
                                return CreateObservableFromVisSpec(reference, selector, morphable);
                            return null;
                        }

                    default:
                        return null;
                }
            }
            else
            {
                // Creating an expression observable from a vis signal should only be done by a Morphable
                string expression = signalSpec["expression"];
                if (expression != null && morphable != null)
                {
                    return CreateObservableFromExpression(expression, morphable);
                }
                else
                {
                    throw new Exception(string.Format("Vis Morphs: Signal {0} needs either a source or expression property.", signalSpec["name"]));
                }
            }
        }

        private static IObservable<dynamic> CreateObservableFromMouseSpec(string reference, string selector)
        {
            // Mouse signals shouldn't need a reference
            switch (selector)
            {
                case "leftmousedown":
                    return Observable.EveryUpdate()
                        .Select(_ => Input.GetMouseButton(0))
                        .DistinctUntilChanged()
                        .Select(_ => (dynamic)_);

                case "rightmousedown":
                    return Observable.EveryUpdate()
                        .Select(_ => Input.GetMouseButton(1))
                        .DistinctUntilChanged()
                        .Select(_ => (dynamic)_);

                case "position":
                    throw new NotImplementedException();

                default:
                    throw new Exception(string.Format("Vis Morphs: Mouse event of selector {0} does not exist.", selector));
            }
        }

        private static IObservable<dynamic> CreateObservableFromControllerSpec(string reference, string selector)
        {
            Handedness handedness = reference == "left" ? Handedness.Left : reference == "right" ? Handedness.Right : Handedness.Any;

            switch (selector)
            {
                case "position":
                {
                    return Observable.EveryUpdate()
                        .Select(_ =>
                        {
                            foreach (var controller in CoreServices.InputSystem.DetectedControllers)
                            {
                                if (controller.ControllerHandedness == handedness && controller.Visualizer != null)
                                {
                                    return (controller.InputSource.Pointers[0] as MonoBehaviour).gameObject;
                                }
                            }

                            return null;
                        })
                        .Where(_ => _ != null)
                        .Select(x => (dynamic)x.transform.position)
                        .StartWith(Vector3.zero);
                }

                case "rotation":
                {
                    return Observable.EveryUpdate()
                        .Select(_ =>
                        {
                            foreach (var controller in CoreServices.InputSystem.DetectedControllers)
                            {
                                if (controller.ControllerHandedness == handedness && controller.Visualizer != null)
                                {
                                    return (controller.InputSource.Pointers[0] as MonoBehaviour).gameObject;
                                }
                            }

                            return null;
                        })
                        .Where(_ => _ != null)
                        .Select(x => (dynamic)x.transform.rotation)
                        .StartWith(Vector3.zero);
                }

                case "select":
                case "grip":
                {
                    var mrtkInputDown = new ObservableMRTKInputDown(selector, Handedness.Any);
                    return mrtkInputDown.OnMRTKInputDownAsObservable()
                                .DistinctUntilChanged()
                                .Select(_ => (dynamic)_);
                }

                default:
                    throw new Exception(string.Format("Vis Morphs: Controller event of select {0} does not exist.", reference));
            }
        }

        private static IObservable<dynamic> CreateObservableFromHandSpec(string reference, string selector)
        {
            Handedness handedness = reference == "left" ? Handedness.Left : reference == "right" ? Handedness.Right : Handedness.Any;

            switch (selector)
            {
                case "pinch":
                    return Observable.EveryUpdate()
                        .Select(_ =>
                        {
                            if (HandJointUtils.FindHand(handedness) != null)
                                return (dynamic)HandPoseUtils.CalculateIndexPinch(handedness);
                            else
                                return 0;
                        })
                        .StartWith(0)
                        .DistinctUntilChanged();

                case "position":
                    return Observable.EveryUpdate()
                        .Select(_ =>
                        {
                            foreach (var controller in CoreServices.InputSystem.DetectedControllers)
                            {
                                if (controller.ControllerHandedness == Handedness.Right && controller.Visualizer != null)
                                {
                                    return (controller.InputSource.Pointers[0] as MonoBehaviour).gameObject;
                                }
                            }

                            return null;
                        })
                        .Where(_ => _ != null)
                        .Select(x => (dynamic)x.transform.position)
                        .StartWith(Vector3.zero)
                        .DistinctUntilChanged();

                default:
                    throw new Exception(string.Format("Vis Morphs: Hand event of selector {0} does not exist.", selector));
            }
        }

        private static IObservable<dynamic> CreateObservableFromHeadSpec(string reference, string selector)
        {
            if (selector == "")
                throw new Exception("Vis Morphs: Signal of type head requires a select expression.");

            switch (selector)
            {
                default:
                    {
                        return Camera.main.transform.gameObject
                            .ObserveEveryValueChanged(x => (dynamic)x.GetPropValue(selector))
                            .DistinctUntilChanged();
                    }
            }
        }

        private static IObservable<dynamic> CreateObservableFromGameObjectSpec(string reference, string selector)
        {
            if (selector == "")
                throw new Exception("Vis Morphs: Signal of type gameobject requires a select expression.");

            // Find the referenced gameobject and emit values whenever the selected property has changed
            return GameObject.Find(reference)
                .ObserveEveryValueChanged(x => (dynamic)x.GetPropValue(selector))
                .DistinctUntilChanged();
        }

        private static IObservable<dynamic> CreateObservableFromVisSpec(string reference, string selector, Morphable morphable)
        {
            if (reference != null && reference != "")
            {
                // Special observables for when surfaces are referenced
                if (reference == "surface")
                {
                    switch (selector)
                    {
                        // Emits true if the vis is attached to a surface, otherwise emits false
                        case "attached":
                        // For now we just use the same code as for intersecting
                        // Emits true if the Vis is intersecting with any surface, otherwise emits false
                        case "intersecting":
                            {
                                BoxCollider boxCollider = morphable.GetComponent<BoxCollider>();
                                return Observable.EveryUpdate()
                                    .Select(_ =>
                                    {
                                        var surfaceColliders = Physics.OverlapBox(morphable.transform.TransformPoint(boxCollider.center), (boxCollider.size / 2f) + (Vector3.one * 0.05f), morphable.transform.rotation)
                                                                      .Where(c => c.gameObject.tag == "Surface");
                                        return (dynamic)surfaceColliders.Count() > 0;
                                    })
                                    .DistinctUntilChanged();
                            }

                        // Emits a Vector3 in world space of the collision point between the vis and the largest surface. Does not emit if no collision is found
                        case "collisionpoint":
                            {
                                BoxCollider boxCollider = morphable.GetComponent<BoxCollider>();
                                return Observable.EveryUpdate()
                                    .Select(_ =>
                                    {
                                        var surfaceColliders = Physics.OverlapBox(morphable.transform.TransformPoint(boxCollider.center), (boxCollider.size / 2f) + (Vector3.one * 0.05f), morphable.transform.rotation)
                                                                      .Where(c => c.gameObject.tag == "Surface");
                                        if (surfaceColliders.Count() > 0)
                                        {
                                            Collider largestTouchingSurface = surfaceColliders.OrderByDescending(c => c.gameObject.transform.localScale.x * c.gameObject.transform.localScale.y * c.gameObject.transform.localScale.z).First();
                                            Collider A = boxCollider;
                                            Collider B = largestTouchingSurface;
                                            Vector3 ptA = B.ClosestPoint(A.transform.position);
                                            Vector3 ptB = A.ClosestPoint(B.transform.position);
                                            Vector3 ptM = ptA + (ptB - ptA) / 2;
                                            Vector3 closestAtB = B.ClosestPoint(ptM);
                                            return (dynamic)closestAtB;
                                        }
                                        return null;
                                    })
                                    .Where(_ => _ != null)
                                    .DistinctUntilChanged();
                            }

                        // Emis a Vector3 in world space of the closest point on the surface closest to the vis, regardless of whether it is touching or not. Does not emit if there are no surfaces
                        case "closestpoint":
                            {
                                return Observable.EveryUpdate()
                                    .Select(_ =>
                                    {
                                        var surfaces = GameObject.FindGameObjectsWithTag("Surface");
                                        var closestPoints = surfaces.Select(surface => surface.GetComponent<Collider>().ClosestPoint(morphable.transform.position))
                                                                    .OrderBy(closestPoint => Vector3.Distance(morphable.transform.position, closestPoint));
                                        return (closestPoints.Count() > 0) ? closestPoints.First() : (dynamic)null;
                                    })
                                    .Where(_ => _ != null)
                                    .DistinctUntilChanged();
                            }

                        // Emis a Vector3 in world space of the closest point on the surface closest to the vis, regardless of whether it is touching or not. Does not emit if there are no surfaces
                        case "closestcollider":
                            {
                                return Observable.EveryUpdate()
                                    .Select(_ =>
                                    {
                                        var colliders = GameObject.FindGameObjectsWithTag("Surface").Select(surface => surface.GetComponent<Collider>());
                                        var closestColliders = colliders.OrderBy(collider => Vector3.Distance(morphable.transform.position, collider.ClosestPoint(morphable.transform.position)));
                                        return (closestColliders.Count() > 0) ? closestColliders.First() : (dynamic)null;
                                    })
                                    .Where(_ => _ != null)
                                    .DistinctUntilChanged();
                            }

                        // Finds the largest surface that the vis is touching and emits the selected property of the surface (not the vis)
                        default:
                            {
                                BoxCollider boxCollider = morphable.GetComponent<BoxCollider>();
                                return Observable.EveryUpdate()
                                    .Select(_ =>
                                    {
                                        var surfaceColliders = Physics.OverlapBox(morphable.transform.TransformPoint(boxCollider.center), (boxCollider.size / 2f) + (Vector3.one * 0.05f), morphable.transform.rotation)
                                                                      .Where(c => c.gameObject.tag == "Surface");
                                        if (surfaceColliders.Count() > 0)
                                        {
                                            Collider largestTouchingSurface = surfaceColliders.OrderByDescending(c => c.gameObject.transform.localScale.x * c.gameObject.transform.localScale.y * c.gameObject.transform.localScale.z).First();
                                            return largestTouchingSurface.GetPropValue(selector);
                                        }
                                        return null;
                                    })
                                    .Where(_ => _ != null)
                                    .DistinctUntilChanged();
                            }
                    }
                }
                // Special observables for when the specification is referenced
                else if (reference.ToLower().StartsWith("spec"))
                {
                    // Create an observable which subscribes to the VisUpdated event, and emits the value as specified using the JSON path format (full stops)
                    return morphable.ParentVis.VisInferredUpdated.AsObservable<Vis, JSONNode>().Select((vis, spec) =>
                        {
                            return Utils.GetValueFromJSONNodePath(spec, selector);
                        })
                        .StartWith(Utils.GetValueFromJSONNodePath(morphable.ParentVis.GetVisSpecsInferred(), selector))
                        .Where(_ => _ != null)
                        .DistinctUntilChanged();
                }
            }

            switch (selector)
            {
                case "collider":
                    {
                        return Observable.EveryUpdate().Select(_ =>
                        {
                            return morphable.GetComponent<Collider>();
                        })
                        .DistinctUntilChanged();
                    }

                case null:
                case "":
                    throw new Exception("Vis Morphs: Signal of type vis that do not reference surfaces requires a select expression.");

                default:
                    {
                    // Use the Vis itself and emit values whenever the selected property has changed
                    return morphable.gameObject
                        .ObserveEveryValueChanged(x => (dynamic)x.GetPropValue(selector))
                        .StartWith(morphable.gameObject.GetPropValue(selector))
                        .DistinctUntilChanged();
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

            // Save this interpreter to the dictionary, matching it to the provided GUID
            Interpreters[guid] = interpreter;
            return interpreter;
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
    }
}