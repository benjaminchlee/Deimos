using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using SimpleJSON;
using UniRx;
using UnityEngine;

namespace DxR.VisMorphs
{
    [RequireComponent(typeof(Vis))]
    public class Morphable : MonoBehaviour
    {
        public string GUID;
        public bool AllowSimultaneousTransitions = true;
        /// <summary>
        /// Debug variables. These don't actually do anything in the code other than print values to the Unity Inspector
        /// </summary>
        public bool ShowValuesInInspector = false;
        public bool DebugStates = false;
        public bool DebugTransitionCalls = false;
        public List<string> CandidateMorphNames = new List<string>();
        public List<string> CandidateStateNames = new List<string>();
        public List<string> CandidateTransitionNames = new List<string>();
        public List<string> ActiveTransitionNames = new List<string>();

        public List<CandidateMorph> CandidateMorphs = new List<CandidateMorph>();

        private Vis parentVis;
        private JSONNode currentVisSpec;
        private bool isInitialised;
        private Dictionary<string, Tuple<Action, int>> queuedTransitionActivations = new Dictionary<string, Tuple<Action, int>>();
        private Dictionary<string, Tuple<Action, int>> queuedTransitionDeactivations = new Dictionary<string, Tuple<Action, int>>();

        private void Start()
        {
            Initialise();
        }

        private void LateUpdate()
        {
            // Resolve all deactivations first
            if (queuedTransitionDeactivations.Count > 0)
            {
                foreach (var kvp in queuedTransitionDeactivations.OrderByDescending(kvp => kvp.Value.Item2).ToList())
                {
                    Action Deactivation = kvp.Value.Item1;
                    queuedTransitionDeactivations.Remove(kvp.Key);
                    Deactivation();
                }
            }

            // Then resolve all activations
            if (queuedTransitionActivations.Count > 0)
            {
                foreach (var kvp in queuedTransitionActivations.OrderByDescending(kvp => kvp.Value.Item2).ToList())
                {
                    Action Activation = kvp.Value.Item1;
                    queuedTransitionActivations.Remove(kvp.Key);
                    Activation();
                }
            }
        }

        public void Initialise()
        {
            if (!isInitialised)
            {
                parentVis = GetComponent<Vis>();
                parentVis.VisUpdated.AddListener(VisUpdated);
                GUID = System.Guid.NewGuid().ToString().Substring(0, 8);
                isInitialised = true;
            }

            CheckForMorphs();
        }

        public void CheckForMorphs()
        {
            if (!isInitialised)
                Initialise();

            currentVisSpec = parentVis.GetVisSpecs();
            VisUpdated(parentVis, currentVisSpec);
        }

        /// <summary>
        /// Called whenever a change is made to the parent vis
        /// </summary>
        private void VisUpdated(Vis vis, JSONNode visSpec)
        {
            if (!isInitialised)
                return;

            currentVisSpec = visSpec;

            // Only check if there are no more activations/deactivations to go
            if (queuedTransitionActivations.Count > 0 || queuedTransitionDeactivations.Count > 0)
            {
                return;
            }

            // First, we get a list of Morphs which we deem as "candidates"
            // Each object in this list also stores the set of candidate states and transitions which match our current vis spec
            List<CandidateMorph> newCandidateMorphs = new List<CandidateMorph>();
            GetCandidateMorphs(visSpec, ref newCandidateMorphs);

            // If there were indeed some Morphs which are candidates, we need to create subscriptions to their observables and so on
            if (newCandidateMorphs.Count > 0)
            {
                // Any morphs that are currently active and that are still valid should be retained. Ones that are no longer valid should be deactivated
                TransferActiveCandidateMorphsAndTransitions(CandidateMorphs, ref newCandidateMorphs);

                CandidateMorphs = newCandidateMorphs;

                CreateCandidateMorphSignals(ref CandidateMorphs);
                CreateCandidateMorphSubscriptions(ref CandidateMorphs);

                // Update debug inspector variables
                if (ShowValuesInInspector)
                {
                    CandidateMorphNames = CandidateMorphs.Select(_ => _.Morph.Name).ToList();
                    CandidateStateNames = CandidateMorphs.SelectMany(_ => _.CandidateStates).Select(_ => _["name"].ToString()).ToList();
                    CandidateTransitionNames = CandidateMorphs.SelectMany(_ => _.CandidateTransitions).Select(_ => _.Item1["name"].ToString()).ToList();
                }
            }
            // Otherwise, clear all morphs
            else
            {
                if (CandidateMorphs.Count > 0)
                {
                    foreach (CandidateMorph morph in CandidateMorphs)
                    {
                        morph.ClearLocalSignals();
                    }

                    CandidateMorphs.Clear();
                }

                // Update debug inspector variables
                if (ShowValuesInInspector)
                {
                    CandidateMorphNames.Clear();
                    CandidateStateNames.Clear();
                    CandidateTransitionNames.Clear();
                }
            }
        }

        private void GetCandidateMorphs(JSONNode visSpec, ref List<CandidateMorph> newCandidateMorphs)
        {
            // We iterate through all of the states that are defined in the MorphManager, saving those which match this vis
            foreach (Morph morph in MorphManager.Instance.Morphs)
            {
                CandidateMorph candidateMorph = null;

                // If this morph has any one of its states matching the vis spec, add it to our list of candidates
                // We also keep checking, adding every matching state to the candidate morph
                foreach (JSONNode stateSpec in morph.States)
                {
                    if (CheckSpecsMatch(visSpec, stateSpec))
                    {
                        if (candidateMorph == null)
                        {
                            candidateMorph = new CandidateMorph(morph);
                        }

                        candidateMorph.CandidateStates.Add(stateSpec);

                        // We also keep going through and add all valid transitions starting from this state
                        string stateName = stateSpec["name"];
                        foreach (JSONNode transitionSpec in morph.Transitions)
                        {
                            JSONArray transitionStateNames = transitionSpec["states"].AsArray;

                            // Add this transition to our list if the first name in the states array matches the input state
                            if (transitionStateNames[0] == stateName)
                            {
                                candidateMorph.CandidateTransitions.Add(new Tuple<JSONNode, bool>(transitionSpec, false));
                            }
                            // We can also add it if the second name matches the input AND the transition is set to bidirectional
                            else if (transitionStateNames[1] == stateName && transitionSpec["bidirectional"])
                            {
                                candidateMorph.CandidateTransitions.Add(new Tuple<JSONNode, bool>(transitionSpec, true));
                            }
                        }
                    }
                }

                if (candidateMorph != null)
                    newCandidateMorphs.Add(candidateMorph);
            }
        }

        /// <summary>
        /// Transfers any transitions that are currently active and still valid from one list of candidate morphs to the other.
        /// Retains variable references to minimise re-instantiation and performance hits.
        /// This function will automatically disable any active transitions that are no longer valid.
        /// </summary>
        private void TransferActiveCandidateMorphsAndTransitions(List<CandidateMorph> oldCandidateMorphs, ref List<CandidateMorph> newCandidateMorphs)
        {
            List<string> newCandidateMorphNames = newCandidateMorphs.Select(cm => cm.Name).ToList();
            List<string> newCandidateTransitionNames = newCandidateMorphs.SelectMany(cm => cm.CandidateTransitions).Select(ct => (string)ct.Item1["name"]).ToList();

            List<string> transferredMorphNames = new List<string>();

            // For each transition which we still have active, copy over all of its variables so that it can continue transitioning without interruption
            foreach (string activeTransitionName in ActiveTransitionNames.ToList())
            {
                // If the active transition is in the new list of candidate transitions, transfer its variable references over
                if (newCandidateTransitionNames.Contains(activeTransitionName))
                {
                    // Get the morph that this active transition belongs to and the new morph that we are transfering its variables to
                    CandidateMorph sourceMorph = oldCandidateMorphs.Single(cm => cm.CandidateTransitions.Select(ct => (string)ct.Item1["name"]).Contains(activeTransitionName));
                    CandidateMorph targetMorph = newCandidateMorphs.Single(cm => cm.Name == sourceMorph.Name);

                    // Only copy over the equivalent transition and its subscriptions, and the local signal references
                    Tuple<JSONNode, CompositeDisposable, List<bool>, bool> sourceTransitionWithSubscriptions = sourceMorph.CandidateTransitionsWithSubscriptions.Single(ct => ct.Item1["name"] == activeTransitionName);
                    targetMorph.CandidateTransitionsWithSubscriptions.Add(sourceTransitionWithSubscriptions);
                    targetMorph.LocalSignalObservables = sourceMorph.LocalSignalObservables;

                    // Keep track of this old morph, we will not clear its local signals later
                    transferredMorphNames.Add(sourceMorph.Name);
                }
                // Otherwise, it means that the conditions of the candidate morph are no longer valid. Force disable it
                else
                {
                    // This probably causes issues whereby changing visualisation encodings means that sometimes conditions will no longer be met, but oh well
                    parentVis.StopTransition(activeTransitionName);
                    ActiveTransitionNames.Remove(activeTransitionName);
                }
            }

            // We also copy over all of the stored keyframes from any old morphs to the new morphs, assuming that they are still active
            // TODO: This is a bit of a naive implementation and will probably break in one or two edge cases, mostly when changes are made
            //       to a visualisation by the user whilst a transition is in progress
            foreach (CandidateMorph oldMorph in oldCandidateMorphs)
            {
                if (newCandidateMorphNames.Contains(oldMorph.Name))
                {
                    newCandidateMorphs.Single(cm => cm.Name == oldMorph.Name).StoredVisKeyframes = oldMorph.StoredVisKeyframes;
                }
            }

            // Clear the signals of the old candidate morphs that did not have a single transition transferred
            foreach (CandidateMorph oldMorph in oldCandidateMorphs)
            {
                if (!transferredMorphNames.Contains(oldMorph.Name))
                {
                    oldMorph.ClearLocalSignals();
                }
            }
        }

        /// <summary>
        /// Creates signals associated with each candidate morph, and stores the observables separately
        /// </summary>
        private void CreateCandidateMorphSignals(ref List<CandidateMorph> candidateMorphs)
        {
            foreach (CandidateMorph candidateMorph in candidateMorphs)
            {
                Morph morph = candidateMorph.Morph;

                foreach (JSONNode signalSpec in morph.LocalSignals)
                {
                    IObservable<dynamic> observable = MorphManager.CreateObservableFromSpec(signalSpec, this);
                    candidateMorph.SaveLocalSignal(signalSpec["name"], observable);
                }
            }
        }

        private void CreateCandidateMorphSubscriptions(ref List<CandidateMorph> candidateMorphs)
        {
            // Create subscriptions for each candidate transition
            for (int i = 0; i < candidateMorphs.Count; i++)
            {
                CandidateMorph candidateMorph = candidateMorphs[i];

                for (int j = 0; j < candidateMorph.CandidateTransitions.Count; j++)
                {
                    Tuple<JSONNode, bool> candidateTransition = candidateMorph.CandidateTransitions[j];
                    JSONNode transitionSpec = candidateTransition.Item1;
                    string transitionName = transitionSpec["name"];
                    int transitionPriority = transitionSpec["priority"] != null ? transitionSpec["priority"].AsInt : 0;

                    // If this candidate transition already has a version with subscriptions, skip it (caused by an old transition being transferred)
                    if (candidateMorph.CandidateTransitionsWithSubscriptions.Select(cts => (string)cts.Item1["name"]).Contains(transitionName))
                        continue;

                    // Create the data structures which we need to store information regarding each of these candidate transitions with subscriptions
                    CompositeDisposable disposables = new CompositeDisposable();    // A container object to hold all of the observable subscriptions. We call Dispose() on this object when the subscriptions are no longer needed
                    List<bool> boolList = new List<bool>();                         // A list of booleans which will be modified by later observables. The transition begins when all booleans are true
                    bool isReversed = candidateTransition.Item2;                    // Whether the transition is reversed

                    // We create a boolean that will track whether or not this transition is based on a timer
                    bool isTimerUsed = true;

                    // We now actually create the subscriptions themselves
                    // If this Transition is using a Predicate as a tweener, we subscribe to it such that the Transition only actually begins if this tweener returns a value between 0 and 1 (exclusive)
                    if (transitionSpec["timing"] != null && transitionSpec["timing"]["control"] != null)
                    {
                        // Get the observable associated with the tweener signal. Check both the global and local signals for it
                        string tweenerName = transitionSpec["timing"]["control"];
                        var observable = GetLocalOrGlobalSignal(tweenerName, candidateMorph);

                        if (observable != null)
                        {
                            isTimerUsed = false;

                            boolList.Add(false);
                            // Delay subscription until end of frame so that all signals can be subscribed to
                            observable.DelayFrameSubscription(0, FrameCountType.EndOfFrame).Subscribe(f =>
                            {
                                boolList[0] = 0 < f && f < 1;

                                // If all of the predicates for this Transition returned true...
                                if (!boolList.Contains(false))
                                {
                                    // AND this morph is not currently active, we can then formally activate the Transition
                                    if (!ActiveTransitionNames.Contains(transitionName) && !queuedTransitionActivations.ContainsKey(transitionName))
                                    {
                                        queuedTransitionActivations.Add(transitionName, new Tuple<Action, int>(() => ActivateTransition(candidateMorph, transitionSpec, transitionName, isReversed), transitionPriority));
                                    }
                                }
                                else
                                {
                                    // Otherwise, if this Tweener has now reached either end of the tweening range, end the transition
                                    if (ActiveTransitionNames.Contains(transitionName) && !queuedTransitionDeactivations.ContainsKey(transitionName))
                                    {
                                        // If the tweening value is 1 or more, the Vis should rest at the final state
                                        bool goToEnd = f >= 1;

                                        queuedTransitionDeactivations.Add(transitionName, new Tuple<Action, int>(() => DeactivateTransition(candidateMorph, transitionSpec, transitionName, goToEnd), transitionPriority));
                                    }
                                }
                            }).AddTo(disposables);
                        }
                    }

                    // If this observable uses a timer, we want to accommodate the reverse transition. Therefore, the transition should start if all triggers
                    // return FALSE, rather than remain true, as they will already be true by the time the forward transition finishes
                    // This boolean helps us reverse the required truthiness
                    bool useReverseTrigger = isTimerUsed && isReversed;

                    // Subscribe to the rest of the Triggers. If no triggers are defined, then we can just skip this process entirely
                    var triggerNames = transitionSpec["triggers"];
                    if (triggerNames != null)
                    {
                        for (int k = 0; k < triggerNames.Count; k++)
                        {
                            // Set the index that will be used to then modify the boolean in our boolArray
                            int index = boolList.Count;

                            // Set the default value of the bool list. This is to ensure that it doesn't immediately call an activation signal before at least one signal has resolved
                            // For standard transitions, all of the booleans start as false
                            // For reverse transitions, all of the booleans start as true
                            boolList.Add(useReverseTrigger);

                            // Get the name of the trigger. If it has an ! in front of it, we inverse its value
                            string triggerName = triggerNames[k];
                            bool triggerInversed = triggerName.StartsWith("!");
                            if (triggerInversed) triggerName = triggerName.Remove(0, 1);      // Remove the ! from the name now

                            // Get the corresponding Signal observable by using its name, casting it to a boolean
                            var observable = GetLocalOrGlobalSignal(triggerName, candidateMorph);

                            if (observable != null)
                            {
                                IObservable<bool> triggerObservable;

                                // Inverse if necessary
                                if (!triggerInversed)
                                    triggerObservable = observable.Select(x => (bool)x);
                                else
                                    triggerObservable = observable.Select(x => (bool)!x);

                                triggerObservable.DelayFrameSubscription(0, FrameCountType.EndOfFrame).Subscribe(b =>
                                {
                                    boolList[index] = b;

                                    // Determine whether or not our list of booleans and triggers have met the conditions necessary to trigger the transition
                                    // This is only applicable for transitions that use timers
                                    // For forward transitions, ALL booleans in the list need to be true
                                    // For reverse transitions, at least one of the booleans in the list needs to be false
                                    bool triggersMet = false;
                                    if (!useReverseTrigger)
                                        triggersMet = !boolList.Contains(false);
                                    else
                                        triggersMet = boolList.Contains(false);

                                    if (triggersMet)
                                    {
                                        // AND this morph is not currently active, we can then formally activate the Transition
                                        if (!ActiveTransitionNames.Contains(transitionName) && !queuedTransitionActivations.ContainsKey(transitionName))
                                        {
                                            queuedTransitionActivations.Add(transitionName, new Tuple<Action, int>(() => ActivateTransition(candidateMorph, transitionSpec, transitionName, isReversed), transitionPriority));
                                        }
                                    }
                                    else
                                    {
                                        // Otherwise, if this Transition WAS active but now no longer meets the trigger conditions,
                                        if (ActiveTransitionNames.Contains(transitionName) && !queuedTransitionDeactivations.ContainsKey(transitionName))
                                        {
                                            // If the Transition specification includes which direction (start or end) to reset the Vis to, we use it
                                            bool goToEnd = false;

                                            if (transitionSpec["interrupt"]["control"] == "reset")
                                            {
                                                goToEnd = transitionSpec["interrupt"]["value"] == "end";
                                            }

                                            queuedTransitionDeactivations.Add(transitionName, new Tuple<Action, int>(() => DeactivateTransition(candidateMorph, transitionSpec, transitionName, goToEnd), transitionPriority));
                                        }
                                    }
                                }).AddTo(disposables);
                            }
                            else
                            {
                                throw new Exception(string.Format("Vis Morphs: Trigger with name {0} cannot be found.", triggerNames[k]));
                            }
                        }
                    }

                    candidateMorph.CandidateTransitionsWithSubscriptions.Add(new Tuple<JSONNode, CompositeDisposable, List<bool>, bool>(
                        transitionSpec,
                        disposables,
                        boolList,
                        isReversed
                    ));
                }
            }
        }

        private IObservable<dynamic> GetLocalOrGlobalSignal(string name, CandidateMorph candidateMorph)
        {
            IObservable<dynamic> observable = candidateMorph.GetLocalSignal(name);
            if (observable == null)
                observable = MorphManager.Instance.GetGlobalSignal(name);
            return observable;
        }

        private bool CheckSpecsMatch(JSONNode visSpec, JSONNode stateSpec)
        {
            return CheckViewLevelSpecsMatching(visSpec, stateSpec) && CheckEncodingSpecsMatching(visSpec["encoding"], stateSpec["encoding"]);
        }

        private bool CheckViewLevelSpecsMatching(JSONNode visSpecs, JSONNode stateSpecs)
        {
            foreach (var property in stateSpecs)
            {
                // Ignore the name and encoding properties
                if (property.Key == "name" || property.Key == "encoding")
                    continue;

                // We also ignore position and rotation properties
                if (property.Key == "position" || property.Key == "rotation")
                    continue;

                // We also ignore the data property for now
                // TODO: Check the data as well
                if (property.Key == "data")
                    continue;

                JSONNode statePropertyValue = property.Value;
                JSONNode visPropertyValue = visSpecs[property.Key];

                // Condition 1: the value of this property is defined, but as null
                if (statePropertyValue.IsNull)
                {
                    if (visPropertyValue != null && !visPropertyValue.IsNull)
                    {
                        return false;
                    }
                }
                // Condition 2: the value of this property is defined as a wildcard ("*")
                else if (statePropertyValue.ToString() == "\"*\"")
                {
                    if (visPropertyValue == null ||
                        (visPropertyValue != null && visPropertyValue.IsNull))
                        return false;
                }
                // Condition 3: the value of this property is defined as a specific value
                else
                {
                    if (visPropertyValue == null ||
                        (visPropertyValue != null && visPropertyValue.ToString() != statePropertyValue.ToString()))
                        return false;
                }
            }

            return true;
        }

        private bool CheckEncodingSpecsMatching(JSONNode visEncodingSpecs, JSONNode stateEncodingSpecs)
        {
            if (stateEncodingSpecs == null)
                return true;

            if (stateEncodingSpecs.IsNull)
                return true;


            foreach (var encoding in stateEncodingSpecs)
            {
                string stateEncodingKey = encoding.Key.ToLower();
                JSONNode stateEncodingValue = encoding.Value;

                JSONNode visEncodingValue = visEncodingSpecs[stateEncodingKey];

                /// If the value of this encoding is null, it means that our vis specs should NOT have it
                /// e.g., "x": null
                if (stateEncodingValue.IsNull)
                {
                    // If the vis specs does actually have this encoding with a properly defined value, then it fails the check
                    if (visEncodingValue != null && !visEncodingValue.IsNull)
                    {
                        return false;
                    }
                }
                // Otherwise, we check all of the properties within this property (i.e., field, value, type) to ensure they match
                else
                {
                    foreach (var stateEncodingProperty in stateEncodingValue)
                    {
                        JSONNode stateEncodingPropertyValue = stateEncodingProperty.Value;
                        JSONNode visEncodingPropertyValue = visEncodingValue[stateEncodingProperty.Key];

                        /// The value of this property in the state is defined, but as null
                        /// e.g.,: "x": {
                        ///          "field": null
                        ///         }
                        if (stateEncodingProperty.Value.IsNull)
                        {
                            // The vis should not have a value for this property. If it does, it fails the check
                            if (visEncodingPropertyValue != null && !visEncodingPropertyValue.IsNull)
                                return false;
                        }

                        /// The value of this property in the state is defined as a wildcard ("*") or is prefixed with "this." or "other."
                        /// e.g.,: "x": {
                        ///          "field": "*"
                        ///          "type": "this.encoding.y.type"
                        ///         }
                        else if (stateEncodingPropertyValue == "*" ||
                                ((string)stateEncodingPropertyValue).StartsWith("this.") ||
                                ((string)stateEncodingPropertyValue).StartsWith("other."))
                        {
                            // The vis should have a value for this property. If it doesn't, it fails the check
                            if (visEncodingPropertyValue == null ||
                                (visEncodingPropertyValue != null && visEncodingPropertyValue.IsNull))
                                return false;
                        }

                        /// The value of this property is some specific value that is not null, not a wildcard or a path selector
                        /// e.g.,: "x": {
                        ///          "field": "Miles_Per_Gallon"
                        ///         }
                        else
                        {
                            // The value in the vis should match the value in the state. If it doesn't it fails the check
                            if (visEncodingPropertyValue == null ||
                                (visEncodingPropertyValue != null && visEncodingPropertyValue.ToString() != stateEncodingPropertyValue.ToString()))
                                return false;
                        }
                    }
                }
            }

            return true;
        }

        private IObservable<float> CreateTweeningObservable(CandidateMorph candidateMorph, JSONNode transitionSpec, bool isReversed)
        {
            JSONNode timingSpecs = transitionSpec["timing"];

            // If no timing specs are given, we just use a generic timer
            if (timingSpecs == null)
            {
                return CreateTimerObservable(candidateMorph, transitionSpec, 1);
            }
            else
            {
                // Otherwise, if the name of the control corresponds with a parameter, use that instead
                string timerName = timingSpecs["control"];
                var observable = MorphManager.Instance.GetGlobalSignal(timerName);
                if (observable == null) observable = candidateMorph.GetLocalSignal(timerName);

                if (timerName != null && observable != null)
                {
                    return observable.Select(_ => (float)_);
                }
                // Otherwise, use the time provided in the specification
                else
                {
                    return CreateTimerObservable(candidateMorph, transitionSpec, timingSpecs["control"], isReversed);
                }
            }
        }

        /// <summary>
        /// Creates an observable that returns a normalised value between 0 to 1 over the specified duration. This is meant to be used for tweening.
        /// By default, the value will increment starting from 0 and automatically termating at 1. If isReversed is set to true, then this will be
        /// inversed, starting at 1 and terminating at 0.
        /// </summary>
        private IObservable<float> CreateTimerObservable(CandidateMorph candidateMorph, JSONNode transitionSpec, float duration, bool isReversed = false)
        {
            float startTime = Time.time;

            var cancellationObservable = Observable.Timer(TimeSpan.FromSeconds(duration));
            var timerObservable = Observable.EveryUpdate().Select(_ =>
            {
                float timer = Time.time - startTime;
                float tween = Mathf.Clamp(timer / duration, 0, 1);

                if (!isReversed)
                    return tween;
                else
                    return 1 - tween;
            })
                .TakeUntil(cancellationObservable);

            bool goToEnd = transitionSpec["timing"]["elapsed"] != null ? transitionSpec["timing"]["elapsed"] == "end" : true;

            // If the transition is reversed, "end" actually means the initial state, therefore we flip this boolean
            if (isReversed)
                goToEnd = !goToEnd;

            // HACKY WORKAROUND: We need some way to cancel this timer early in case it gets interrupted. For now we just find the composite
            // disposable tied to the transition and the subscription to it. Ideally this should be done alongside with all of the other signals
            cancellationObservable.Subscribe(_ => DeactivateTransition(candidateMorph, transitionSpec, transitionSpec["name"], goToEnd))
                .AddTo(candidateMorph.CandidateTransitionsWithSubscriptions.Single(cts => cts.Item1["name"] == transitionSpec["name"]).Item2);

            return timerObservable;
        }

        /// <summary>
        /// Starts the given transition only if there is not already one with the same name already active. Meant to be called by signal tweeners.
        ///
        /// Only actually activates the transition at the end of the frame. This is to ensure all Signals have emitted their values before making any changes to the Vis.
        /// This function should be called by adding an anonymous lambda to queuedTransitionActivations in the form () => ActivateTransition(...)
        /// </summary>
        private void ActivateTransition(CandidateMorph candidateMorph, JSONNode transitionSpec, string transitionName, bool isReversed = false)
        {
            if (DebugTransitionCalls)
            {
                Debug.Log(string.Format("Vis Morphs: Transition \"{0}\" called Activate function.", transitionName));
            }

            if (ActiveTransitionNames.Contains(transitionName))
            {
                Debug.LogError(string.Format("Vis Morphs: Transition \"{0}\" tried to activate, but it is already active.", transitionName));
                return;
            }

            if (ActiveTransitionNames.Count > 0 && !AllowSimultaneousTransitions)
            {
                Debug.LogWarning(string.Format("Vis Morphs: Transition \"{0}\" could not be applied as there is already a transition active, and the AllowSimultaneousTransitions flag is set to false.", transitionName));
                return;
            }

            // Generate the initial and final vis states
            JSONNode initialState, finalState;
            GenerateVisSpecKeyframes(currentVisSpec,
                                     candidateMorph.Morph.GetStateFromName(transitionSpec["states"][0]),
                                     candidateMorph.Morph.GetStateFromName(transitionSpec["states"][1]),
                                     candidateMorph,
                                     isReversed,
                                     out initialState,
                                     out finalState);

            if (DebugStates)
            {
                // Remove the values node because it is usually way to long to properly print to console
                JSONNode _initialState = initialState.Clone();
                JSONNode _finalState = finalState.Clone();
                if (_initialState["data"]["values"] != null)
                    _initialState["data"].Remove("values");
                if (_finalState["data"]["values"] != null)
                    _finalState["data"].Remove("values");
                Debug.Log(string.Format("Vis Morphs: Initial state specification for transition \"{0}\":\n{1}", transitionName, _initialState.ToString()));
                Debug.Log(string.Format("Vis Morphs: Final state specification for transition \"{0}\":\n{1}", transitionName, _finalState.ToString()));
            }

            // Call update to final state using a tweening observable
            var tweeningObservable = CreateTweeningObservable(candidateMorph, transitionSpec, isReversed);
            bool success = parentVis.ApplyTransition(transitionName, initialState, finalState, tweeningObservable, isReversed);

            if (success)
            {
                ActiveTransitionNames.Add(transitionName);
            }
        }

        /// <summary>
        /// Stops the specified transition if there is any. Meant to be called by signal tweeners.
        ///
        /// Only actually activates the transition at the end of the frame. This is to ensure all Signals have emitted their values before making any changes to the Vis.
        /// This function should be called by adding an anonymous lambda to queuedTransitionActivations in the form () => ActivateTransition(...)
        /// </summary>
        private void DeactivateTransition(CandidateMorph candidateMorph, JSONNode transitionSpec, string transitionName, bool goToEnd = true)
        {
            if (DebugTransitionCalls)
            {
                Debug.Log(string.Format("Vis Morphs: Transition \"{0}\" called Deactivate function.", transitionName));
            }

            if (!ActiveTransitionNames.Contains(transitionName))
            {
                Debug.LogError(string.Format("Vis Morphs: Transition \"{0}\" tried to deactivate, but it is not active in the first place", transitionName));
                return;
            }

            parentVis.StopTransition(transitionName, goToEnd, true);

            // Unsubscribe to this transition's signals
            candidateMorph.DisposeTransitionSubscriptions(transitionName);

            ActiveTransitionNames.Remove(transitionName);

            // Check for morphs again so that they can seamlessly progress
            CheckForMorphs();
        }

        /// <summary>
        /// Does a complete reset.
        ///
        /// Stops all active transitions if there are any, and resets the Morphable back to a neutral state.
        /// </summary>
        public void Reset(bool goToEnd = false)
        {
            // Dispose of all subscriptions
            foreach (CandidateMorph candidateMorph in CandidateMorphs)
            {
                candidateMorph.ClearLocalSignals();
            }

            // If there were transitions in progress, stop all of them
            foreach (string activeMorph in ActiveTransitionNames)
            {
                parentVis.StopTransition(activeMorph, goToEnd);
            }
            ActiveTransitionNames.Clear();

            // Reset variables
            CandidateMorphs.Clear();
            CandidateMorphNames.Clear();
            CandidateStateNames.Clear();
            CandidateTransitionNames.Clear();

            // Check for morphs again to allow for further morphing without needing to update the vis
            CheckForMorphs();
        }

        /// <summary>
        /// Calculates the initial and final keyframes of a given transition. This function handles everything using JSON.NET due to its
        /// increased functionality for manipulating JSON objects. This returns SimpleJSON objects however to be used in the transition itself.
        /// </summary>
        private void GenerateVisSpecKeyframes(JSONNode originalVisSpec, JSONNode initialStateSpec, JSONNode finalStateSpec,
                                                      CandidateMorph candidateMorph, bool isReversed,
                                                      out JSONNode initialVisSpec, out JSONNode finalVisSpec)
        {
            // SimpleJSON doesn't really work well with editing JSON objects, so we just use JSON.NET instead until we finish calculating the keyframes
            // Remove the data property on the original vis spec, as it is very computationally expensive to parse into JSON.NET and back again
            JSONNode originalDataSpecs = originalVisSpec["data"];
            originalVisSpec.Remove("data");

            // Create JSON.NET equivalents of all of these specs
            JObject _originalVisSpec = Newtonsoft.Json.Linq.JObject.Parse(originalVisSpec.ToString());
            JObject _initialStateSpec = Newtonsoft.Json.Linq.JObject.Parse(initialStateSpec.ToString());
            JObject _finalStateSpec = Newtonsoft.Json.Linq.JObject.Parse(finalStateSpec.ToString());

            // Generate the initial and final vis specs based on the provided state specs
            JObject _initialVisSpec, _finalVisSpec;

            if (!isReversed)
            {
                // Before we do anything, store the current vis spec so that we can come back to it later
                candidateMorph.StoredVisKeyframes[_initialStateSpec["name"].ToString()] = _originalVisSpec;

                // Generate our initial and final specs
                _initialVisSpec = _originalVisSpec;
                _finalVisSpec = GenerateVisSpecFromStateSpecs(_initialVisSpec, _initialStateSpec, _finalStateSpec, candidateMorph);

                // Replace all of the leaf nodes that reference other values by JSON path or by Signal names with their proper values
                ReplaceLeafValuesInVisSpec(ref _finalVisSpec, _initialVisSpec, _finalVisSpec, candidateMorph);

            }
            else
            {
                candidateMorph.StoredVisKeyframes[_finalStateSpec["name"].ToString()] = _originalVisSpec;
                _finalVisSpec = _originalVisSpec;
                _initialVisSpec = GenerateVisSpecFromStateSpecs(_finalVisSpec, _finalStateSpec, _initialStateSpec, candidateMorph);
                ReplaceLeafValuesInVisSpec(ref _initialVisSpec, _initialVisSpec, _finalVisSpec, candidateMorph);
            }

            // Convert back to SimpleJSON
            initialVisSpec = JSONObject.Parse(Newtonsoft.Json.JsonConvert.SerializeObject(_initialVisSpec));
            finalVisSpec = JSONObject.Parse(Newtonsoft.Json.JsonConvert.SerializeObject(_finalVisSpec));

            // Add the data specs back in
            initialVisSpec.Add("data", originalDataSpecs);
            finalVisSpec.Add("data", originalDataSpecs);
        }

        /// <summary>
        /// Generates a vis spec based on two given state specs.
        ///
        /// There are three different types of encoding changes that are possible here, one of which has two sub-conditions:
        /// A) undefined -> defined (i.e., encoding is added)
        /// B) defined -> undefined (i.e., encoding is removed)
        /// C) defined -> defined (i.e., encoding is changed)
        ///
        /// We simplify this down to the following psudocode:
        /// 1. For each encoding defined in the initial state, if it is not defined in the final state, it will be removed from the vis state (REMOVED)
        /// 2. For each encoding defined in the final state:
        ///     a. If it was not defined in the initial state, we add it to the vis state (ADDED)
        ///     b. If it was defined in the initial state, we modify the vis state depending on the following rules: (CHANGED)
        ///         i. If the final state defines a field or value, remove any pre-exisiting field or value in the vis state before adding the one from the final state
        ///         ii. If the final state specifies NULLs anywhere, these are removed from the vis state. Everything else is left unchanged (for now)
        ///
        /// We also update the vis spec based on a stored history of keyframe specs that were created within the context of this Candidate Morph. This is to allow
        /// instances where a reverse transition "redos" any changes made to it. For example, a transition which removes an encoding can easily add it back
        /// in when applied in reverse due to this memory.
        ///
        /// There are three different scenarios where this applicable, but only if the final state has a matching keyframe spec stored:
        /// A) undefined -> defined: We copy over everything in the stored keyframe spec
        /// B) defined -> undefined: We copy over everything in the stored keyframe spec, but ONLY if it is undefined by omission, rather than by an explicit NULL
        /// C) defined -> defined: We copy over everything in the stored keyframe spec
        /// </summary>
        private JObject GenerateVisSpecFromStateSpecs(JObject _visSpec, JObject _initialStateSpec, JObject _finalStateSpec, CandidateMorph candidateMorph)
        {
            JObject _newVisSpec = _visSpec.DeepClone() as JObject;

            // Get the stored keyframe spec based on the final state's name, if any. If there isn't we will skip the vis spec history process
            JObject _storedKeyframeSpec = null;
            candidateMorph.StoredVisKeyframes.TryGetValue(_finalStateSpec["name"].ToString(), out _storedKeyframeSpec);

            // Check encodings
            foreach (var encoding in ((JObject)_initialStateSpec["encoding"]).Properties())
            {
                // Step 1: Check which encodings to remove. We remove encodings that are defined in initial but not in final
                if (IsJTokenNullOrUndefined(_finalStateSpec["encoding"][encoding.Name]))
                {
                    // If the final state does not explicitly specify NULL, and we have a stored keyframe spec, copy over its value to the vis spec instead (scenario B)
                    if (_finalStateSpec["encoding"][encoding.Name] == null &&
                        (_storedKeyframeSpec != null && !IsJTokenNullOrUndefined(_storedKeyframeSpec["encoding"][encoding.Name])))
                    {
                        _newVisSpec["encoding"][encoding.Name] = _storedKeyframeSpec["encoding"][encoding.Name];
                    }
                    // Otherwise, just remove it from the vis spec
                    else
                    {
                        _newVisSpec["encoding"][encoding.Name]?.Parent.Remove();
                    }
                }
            }

            foreach (var encoding in ((JObject)_finalStateSpec["encoding"]).Properties())
            {
                // Ignore any encodings marked as a wildcard (*)
                // However, we still copy over any changes from our stored keyframe spec, if any
                // TODO: This might actually break something, I'm not sure
                if (encoding.Value.ToString() == "*")
                {
                    if (_storedKeyframeSpec != null && !IsJTokenNullOrUndefined(_storedKeyframeSpec["encoding"][encoding.Name]))
                    {
                        // We use first Remove then Add here to forcefully replace any encodings already in the vis spec
                        _newVisSpec["encoding"][encoding.Name]?.Parent.Remove();
                        ((JObject)_newVisSpec["encoding"]).Add(_storedKeyframeSpec["encoding"][encoding.Name].Parent);
                    }

                    continue;
                }

                // Ignore any encodings that are defined as null, as these were already handled in Step 1
                if (IsJTokenNullOrUndefined(encoding.Value))
                    continue;

                // Step 2a: Add any encodings to the vis spec that were not in the initial spec but are in the final spec
                if (IsJTokenNullOrUndefined(_initialStateSpec["encoding"][encoding.Name]))
                {
                    // Copy over the values from the stored keyframe spec if it exists (scenario A)
                    if (_storedKeyframeSpec != null && !IsJTokenNullOrUndefined(_storedKeyframeSpec["encoding"][encoding.Name]))
                    {
                        // We use first Remove then Add here to forcefully replace any encodings already in the vis spec
                        _newVisSpec["encoding"][encoding.Name]?.Parent.Remove();
                        ((JObject)_newVisSpec["encoding"]).Add(_storedKeyframeSpec["encoding"][encoding.Name].Parent);
                    }
                    // Otherwise just add whatever value that was in the final spec
                    else
                    {
                        _newVisSpec["encoding"][encoding.Name]?.Parent.Remove();
                        ((JObject)_newVisSpec["encoding"]).Add(encoding);
                    }
                }
                // Step 2b: Modify any encodings that are defined in both
                else
                {
                    // Copy over the values from the stored keyframe spec if it exists (scenario C)
                    // We should be able to do a direct copy since all the values in the stored keyframe should be valid
                    if (_storedKeyframeSpec != null && !IsJTokenNullOrUndefined(_storedKeyframeSpec["encoding"][encoding.Name]))
                    {
                        _newVisSpec["encoding"][encoding.Name]?.Parent.Remove();
                        ((JObject)_newVisSpec["encoding"]).Add(_storedKeyframeSpec["encoding"][encoding.Name].Parent);
                    }
                    // Otherwise just compute as per normal
                    // Step 2bi: Make sure that the vis state doesn't have both a field and value (the final state takes priority)
                    else
                    {
                        if (!IsJTokenNullOrUndefined(_finalStateSpec["encoding"][encoding.Name]["field"]) || !IsJTokenNullOrUndefined(_finalStateSpec["encoding"][encoding.Name]["value"]))
                        {
                            // If the final state has either a field or value, we remove fields and values from the vis state to simplify the merging later
                            if (_newVisSpec["encoding"][encoding.Name] != null)
                            {
                                if (_newVisSpec["encoding"][encoding.Name]["field"] != null)
                                    _newVisSpec["encoding"][encoding.Name]["field"].Parent.Remove();
                                if (_newVisSpec["encoding"][encoding.Name]["value"] != null)
                                    _newVisSpec["encoding"][encoding.Name]["value"]?.Parent.Remove();
                            }
                        }

                        ((JObject)_newVisSpec["encoding"][encoding.Name]).Merge(
                            _finalStateSpec["encoding"][encoding.Name], new JsonMergeSettings
                            {
                                MergeArrayHandling = MergeArrayHandling.Replace,
                                MergeNullValueHandling = MergeNullValueHandling.Merge
                            });
                    }
                }
            }

            // We now also apply the same logic as above, except this time to the view-level properties
            foreach (var property in ((JObject)_initialStateSpec).Properties())
            {
                // TODO: Currently there's a bandaid fix in place whereby we can't actually remove width/height/depth values from a specification,
                // otherwise the inference fails and causes the scale object to not calculate the correct range. Fix this later if this causes further issues
                if (property.Name == "data" || property.Name == "mark" || property.Name == "encoding" ||
                    property.Name == "name" || property.Name == "title" ||
                    property.Name == "width" || property.Name == "height" || property.Name == "depth")
                    continue;

                // Step 1: Check which encodings to remove
                if (IsJTokenNullOrUndefined(_finalStateSpec[property.Name]))
                {
                    if (_finalStateSpec[property.Name] == null &&
                        (_storedKeyframeSpec != null && !IsJTokenNullOrUndefined(_storedKeyframeSpec[property.Name])))
                    {
                        _newVisSpec[property.Name] = _storedKeyframeSpec[property.Name];
                    }
                    else
                    {
                        _newVisSpec[property.Name]?.Parent.Remove();
                    }
                }
            }

            foreach (var property in ((JObject)_finalStateSpec).Properties())
            {
                if (property.Name == "data" || property.Name == "mark" || property.Name == "encoding" || property.Name == "name" || property.Name == "title")
                    continue;

                // Ignore any properties that are defined as null, as these were already handled in Step 1
                if (IsJTokenNullOrUndefined(property.Value))
                    continue;

                // Step 2a: Add any properties to the vis state
                if (IsJTokenNullOrUndefined(_initialStateSpec[property.Name]))
                {
                    if (_storedKeyframeSpec != null && !IsJTokenNullOrUndefined(_storedKeyframeSpec[property.Name]))
                    {
                        // We use first Remove then Add here to forcefully replace any encodings already in the vis spec
                        _newVisSpec[property.Name]?.Parent.Remove();
                        ((JObject)_newVisSpec).Add(_storedKeyframeSpec[property.Name].Parent);
                    }
                    else
                    {
                        _newVisSpec[property.Name]?.Parent.Remove();
                        ((JObject)_newVisSpec).Add(property);
                    }
                }
                // Step 2b: Modify any properties that are defined in both
                else
                {
                    if (_storedKeyframeSpec != null && !IsJTokenNullOrUndefined(_storedKeyframeSpec[property.Name]))
                    {
                        _newVisSpec[property.Name]?.Parent.Remove();
                        ((JObject)_newVisSpec).Add(_storedKeyframeSpec[property.Name].Parent);
                    }
                    else if (!IsJTokenNullOrUndefined(_finalStateSpec[property.Name]))
                    {
                        // Take the value from the final state
                        if (_newVisSpec[property.Name] != null)
                            _newVisSpec[property.Name].Parent.Remove();
                        _newVisSpec.Add(property.Name, _finalStateSpec[property.Name]);
                    }
                }
            }

            // Clean up any nulls in the vis specs
            RemoveNullPropertiesInVisSpec(ref _newVisSpec);

            return _newVisSpec;
        }

        private bool IsJTokenNullOrUndefined(JToken jToken)
        {
            return jToken == null || (jToken != null && jToken.Type == JTokenType.Null);
        }

        /// <summary>
        /// Removes all properties with a value of null in a JSON.NET object
        /// </summary>
        private void RemoveNullPropertiesInVisSpec(ref JObject specs)
        {
            var descendants = specs.Descendants()
                .Where(x => !x.HasValues);

            List<string> pathsToRemove = new List<string>();

            foreach (var descendant in descendants)
            {
                if (descendant.Type == JTokenType.Null)
                {
                    pathsToRemove.Add(descendant.Parent.Path);
                }
            }

            foreach (string path in pathsToRemove)
            {
                specs.SelectToken(path).Parent.Remove();
            }
        }

        /// <summary>
        /// This function does two things:
        /// 1. Replaces all leaf values that are names of signals with said signal's values, in JToken form
        /// 2. Copies JTokens from one property/encoding to the other using JSON.NET's path format, prefixed with "this." or ".other"
        /// </summary>
        private void ReplaceLeafValuesInVisSpec(ref JObject visSpec, JObject initialVisSpec, JObject finalVisSpec, CandidateMorph candidateMorph)
        {
            // Get all leaf nodes in the vis specs
            var descendants = visSpec.Descendants().Where(descendant => !descendant.HasValues);

            // Loop through and find all nodes that reference signals, and calculate their JToken representation
            List<Tuple<string, JToken>> descendantsToUpdate = new List<Tuple<string, JToken>>();
            foreach (var descendant in descendants)
            {
                string leafValue = descendant.ToString();

                // Function 1: Replace signal names with their values
                if (!(leafValue.StartsWith("this.") || leafValue.StartsWith("other.")))
                {
                    JToken value = GetJTokenFromSignalName(leafValue, candidateMorph);
                    if (value != null)
                    {
                        // Store the path and JToken for us to update after this foreach loop
                        descendantsToUpdate.Add(new Tuple<string, JToken>(descendant.Path, value));
                    }
                }
                // Function 2: Replace values with full stops with respective value from a different property/encoding
                else
                {
                    // We get the value from either the initial or final vis spec depending on the prefix
                    JObject specToCheck = null;
                    string sourcePath = "";
                    if (leafValue.StartsWith("this."))
                    {
                        specToCheck = initialVisSpec;
                        sourcePath = leafValue.Replace("this.", "");
                    }
                    else if (leafValue.StartsWith("other."))
                    {
                        specToCheck = initialVisSpec;
                        sourcePath = leafValue.Replace("other.", "");
                    }

                    JToken sourceValue = specToCheck.SelectToken(sourcePath);
                    if (sourceValue != null)
                    {
                        descendantsToUpdate.Add(new Tuple<string, JToken>(descendant.Path, sourceValue));
                    }
                    else
                    {
                        throw new Exception(sourcePath);
                    }
                }
            }

            // Update the descendants
            foreach (var tuple in descendantsToUpdate)
            {
                var descendant = visSpec.SelectToken(tuple.Item1).Parent as JProperty;
                descendant.Value = tuple.Item2;
            }
        }

        private JToken GetJTokenFromSignalName(string signalName, CandidateMorph candidateMorph)
        {
            var observable = GetLocalOrGlobalSignal(signalName, candidateMorph);

            if (observable != null)
            {
                dynamic value = GetLastValueFromObservable(observable);
                return ConvertObservableValueToJToken(value);
            }

            return null;
        }

        private dynamic GetLastValueFromObservable(IObservable<dynamic> observable)
        {
            dynamic value = null;
            IDisposable disposable = observable.Subscribe(_ => {
                value = _;
            });
            disposable.Dispose();
            return value;
        }

        private JToken ConvertObservableValueToJToken(dynamic value)
        {
            switch (value.GetType().ToString())
            {
                case "UnityEngine.Vector3":
                    {
                        Vector3 vector3 = (Vector3)value;
                        return new JArray(vector3.x, vector3.y, vector3.z);
                    }

                case "UnityEngine.Quaternion":
                    {
                        Quaternion quaternion = (Quaternion)value;
                        return new JArray(quaternion.x, quaternion.y, quaternion.z, quaternion.w);
                    }

                default:
                    {
                        return null;
                    }
            }
        }

        private void OnDestroy()
        {
            Reset();
            parentVis.VisUpdated.RemoveListener(VisUpdated);
            MorphManager.Instance.ClearMorphableVariables(this);
        }
    }
}