using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Microsoft.MixedReality.Toolkit.SpatialManipulation;
using Newtonsoft.Json.Linq;
using SimpleJSON;
using UniRx;
using UnityEngine;
using static DxR.VisMorphs.EasingFunction;

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

        public Vis ParentVis { get; private set; }
        public JSONNode CurrentVisSpec { get; private set; }
        private bool isInitialised;
        private Dictionary<string, Tuple<Action, int>> queuedTransitionActivations = new Dictionary<string, Tuple<Action, int>>();
        private Dictionary<string, Tuple<Action, int>> queuedTransitionDeactivations = new Dictionary<string, Tuple<Action, int>>();

        private ObjectManipulator objectManipulator;
        private Vector3 restingPosition;
        private Quaternion restingRotation;

        private void Start()
        {
            Initialise();
        }

        private void LateUpdate()
        {
            // Resolve all deactivations first
            if (queuedTransitionDeactivations.Count > 0)
            {
                // Sort these in order of their priority (stored as item 2 in the tuple)
                foreach (var kvp in queuedTransitionDeactivations.OrderByDescending(kvp => kvp.Value.Item2).ToList())
                {
                    Action Deactivation = kvp.Value.Item1;
                    queuedTransitionDeactivations.Remove(kvp.Key);
                    Deactivation();
                }

                CheckForMorphs();
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

            // Store the position and rotation of this GameObject at its resting state (i.e., not being grabbed)
            // This is so that we can reset its position if we need to release the grab when a transition starts
            if (objectManipulator != null && !objectManipulator.isSelected)
            {
                restingPosition = transform.position;
                restingRotation = transform.rotation;
            }
        }

        public void Initialise()
        {
            if (!isInitialised)
            {
                ParentVis = GetComponent<Vis>();
                ParentVis.VisUpdated.AddListener(VisUpdated);
                GUID = System.Guid.NewGuid().ToString().Substring(0, 8);
                objectManipulator = GetComponent<ObjectManipulator>();
                isInitialised = true;
            }
        }

        public void CheckForMorphs()
        {
            if (!isInitialised)
                Initialise();

            CurrentVisSpec = ParentVis.GetVisSpecs();
            VisUpdated(ParentVis, CurrentVisSpec);
        }

        /// <summary>
        /// Called whenever a change is made to the parent vis
        /// </summary>
        private void VisUpdated(Vis vis, JSONNode visSpec)
        {
            if (!isInitialised)
                return;

            CurrentVisSpec = visSpec;

            // Only check if there are no more activations/deactivations to go. This shouldn't be true normally, but it's a good emergency measure
            if (queuedTransitionActivations.Count > 0 || queuedTransitionDeactivations.Count > 0)
            {
                return;
            }

            // First, we get a list of Morphs which we deem as "candidates"
            // Each object in this list also stores the set of candidate states and transitions which match our current vis spec
            List<CandidateMorph> newCandidateMorphs = new List<CandidateMorph>();
            GetCandidateMorphs(visSpec, CandidateMorphs, ref newCandidateMorphs);

            // If there were indeed some Morphs which are candidates, we need to create subscriptions to their observables and so on
            if (newCandidateMorphs.Count > 0)
            {
                CandidateMorphs = newCandidateMorphs;

                CreateCandidateMorphSignals(ref CandidateMorphs);
                CreateCandidateMorphSubscriptions(ref CandidateMorphs);

                // Update debug inspector variables
                if (ShowValuesInInspector)
                {
                    CandidateMorphNames = CandidateMorphs.Select(_ => _.Morph.Name).ToList();
                    CandidateStateNames = CandidateMorphs.Select(_ => _.CandidateState["name"].Value).ToList();
                    CandidateTransitionNames = CandidateMorphs.SelectMany(_ => _.CandidateTransitions).Select(_ => _.Item1["name"].Value).ToList();
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

        /// <summary>
        /// Populates a list of CandidateMorphs that are valid for the given vis spec.
        /// This function also retains any ongoing transitions by copying over their observables and keyframes
        /// This function effectively acts as the state machine, deciding which states the vis can transition to depending on the following:
        ///     1) Does the vis spec match any of the Morph's state specs?
        ///     2) Which transitions in the Morph does the above state match?
        ///     3) Is the state accessible from outside of the Morph?
        ///     4) Is this Morph already ongoing and is it already in a particular state?
        /// </summary>
        private void GetCandidateMorphs(JSONNode visSpec, List<CandidateMorph> oldCandidateMorphs, ref List<CandidateMorph> newCandidateMorphs)
        {
            // We iterate through all the states that are defined in the MorphManager
            // Any which match (and also meet other criteria) we save to the list of new candidate Morphs
            foreach (Morph morph in MorphManager.Instance.Morphs)
            {
                // If this Morph was not already a candidate Morph, we potentially add it as a brand new candidate
                if (!oldCandidateMorphs.Select(cm => cm.Name).Contains(morph.Name))
                {
                    // Go through all state specs and see if any matches the current Vis spec
                    foreach (JSONNode stateSpec in morph.States)
                    {
                        // Since this Morph is brand new, we cannot access any restricted states (i.e., those that can only be access via a transition)
                        // Therefore we simply skip these
                        if (stateSpec["access"] != null && stateSpec["access"] == false)
                            continue;

                        if (CheckSpecsMatch(visSpec, stateSpec, morph))
                        {
                            CandidateMorph newCandidateMorph = CreateCandidateMorphFromStateSpec(morph, stateSpec);
                            newCandidateMorphs.Add(newCandidateMorph);

                            // We can only have one candidate state per candidate Morph, hence we break here
                            break;
                        }
                    }
                }
                // If it was a candidate Morph, we handle this process differently, including transferring references
                else
                {
                    // Get the old candidate Morph object
                    CandidateMorph oldCandidateMorph = oldCandidateMorphs.First(cm => cm.Name == morph.Name);
                    CandidateMorph newCandidateMorph = null;

                    // Check to see if the candidate state in the previous morph is still valid
                    if (CheckSpecsMatch(visSpec, oldCandidateMorph.CandidateState, morph))
                    {
                        // Instead of enumerating through and picking a candidate state, we use this old one
                        // Note that we are still finding new candidate transitions as these may be outdated
                        // Any active transitions will still be transferred over from old to new candidate Morph objects
                        newCandidateMorph = CreateCandidateMorphFromStateSpec(morph, oldCandidateMorph.CandidateState);
                    }
                    // If it is no longer valid, we need to find a new candidate state
                    else
                    {
                        // Go through all state specs and see if any matches the current Vis spec
                        foreach (JSONNode stateSpec in morph.States)
                        {
                            // Note that since this Morph is still active, we can access any restricted states without issue (unlike above)

                            if (CheckSpecsMatch(visSpec, stateSpec, morph))
                            {
                                newCandidateMorph = CreateCandidateMorphFromStateSpec(morph, stateSpec);

                                // We can only have one candidate state per candidate Morph, hence we break here
                                break;
                            }
                        }
                    }

                    // If the old candidate Morph is still active (i.e., we have made an accompanying new candidate Morph object)
                    if (newCandidateMorph != null)
                    {
                        newCandidateMorphs.Add(newCandidateMorph);

                        List<string> morphTransitions = morph.Transitions.Select(t => t["name"].Value).ToList();
                        List<string> newCandidateMorphTransitions = newCandidateMorph.CandidateTransitions.Select(ct => ct.Item1["name"].Value).ToList();

                        // Transfer over all observable and subscription references for any transitions that are still active
                        bool isTransitionActive = false;
                        foreach (string activeTransitionName in ActiveTransitionNames.ToList())
                        {
                            if (morphTransitions.Contains(activeTransitionName))
                            {
                                if (newCandidateMorphTransitions.Contains(activeTransitionName))
                                {
                                    var oldTransitionWithSubscriptions = oldCandidateMorph.CandidateTransitionsWithSubscriptions.Single(ct => ct.Item1["name"] == activeTransitionName);
                                    newCandidateMorph.CandidateTransitionsWithSubscriptions.Add(oldTransitionWithSubscriptions);
                                    newCandidateMorph.LocalSignalObservables = oldCandidateMorph.LocalSignalObservables;
                                    isTransitionActive = true;
                                }
                                else
                                {
                                    // This probably causes issues whereby changing visualisation encodings means that sometimes conditions will no longer be met, but oh well
                                    ParentVis.StopTransition(activeTransitionName);
                                    ActiveTransitionNames.Remove(activeTransitionName);
                                }
                            }
                        }

                        // If none of the transitions were actually active, clear all local signals so that we start fresh
                        if (!isTransitionActive)
                            oldCandidateMorph.ClearLocalSignals();

                        // Transfer over all keyframes old candidate Morph to the new one
                        newCandidateMorph.StoredVisKeyframes = oldCandidateMorph.StoredVisKeyframes;
                    }
                    // If it is no longer active, kill any active transitions associated with this Morph
                    else
                    {
                        List<string> morphTransitions = morph.Transitions.Select(t => t["name"].Value).ToList();

                        foreach (string activeTransitionName in ActiveTransitionNames.ToList())
                        {
                            if (morphTransitions.Contains(activeTransitionName))
                            {
                                ParentVis.StopTransition(activeTransitionName);
                                ActiveTransitionNames.Remove(activeTransitionName);
                            }
                        }

                        oldCandidateMorph.ClearLocalSignals();
                    }
                }
            }
        }

        /// <summary>
        /// Creates a CandidateMorph object. As these can only have a single candidate state, it requires a state spec as a parameter.
        /// This function also finds all transitions that involve said state
        /// </summary>
        private CandidateMorph CreateCandidateMorphFromStateSpec(Morph morph, JSONNode stateSpec)
        {
            CandidateMorph newCandidateMorph = new CandidateMorph(morph, stateSpec);

            // We keep going through and add all valid transitions starting from this state
            string stateName = stateSpec["name"];
            foreach (JSONNode transitionSpec in morph.Transitions)
            {
                JSONArray transitionStateNames = transitionSpec["states"].AsArray;

                // Add this transition to our list if the first name in the states array matches the input state
                if (transitionStateNames[0] == stateName)
                {
                    newCandidateMorph.CandidateTransitions.Add(new Tuple<JSONNode, bool>(transitionSpec, false));
                }
                // We can also add it if the second name matches the input AND the transition is set to bidirectional
                else if (transitionStateNames[1] == stateName && transitionSpec["bidirectional"])
                {
                    newCandidateMorph.CandidateTransitions.Add(new Tuple<JSONNode, bool>(transitionSpec, true));
                }
            }

            return newCandidateMorph;
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
                    IObservable<dynamic> observable = MorphManager.Instance.CreateObservableFromSpec(signalSpec, this);
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
                                            bool goToEnd = false;
                                            // The transition spec provides additional rules for what to do when the transition is interrupted
                                            if (transitionSpec["interrupt"]["control"] != null)
                                            {
                                                // If the control is set to "ignore", it means we ignore this call to disable. Return out of this function
                                                if (transitionSpec["interrupt"]["control"] == "ignore")
                                                {
                                                    return;
                                                }
                                                // If the control is set to "reset", we reset the transition back to either the initial or final state, depending on what is specified
                                                else if (transitionSpec["interrupt"]["control"] == "reset")
                                                {
                                                    goToEnd = transitionSpec["interrupt"]["value"] == "end";
                                                }
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

        private bool CheckSpecsMatch(JSONNode visSpec, JSONNode stateSpec, Morph morph)
        {
            return CheckViewLevelSpecsMatching(visSpec, stateSpec, morph) && CheckEncodingSpecsMatching(visSpec["encoding"], stateSpec["encoding"], morph);
        }

        private bool CheckViewLevelSpecsMatching(JSONNode visSpecs, JSONNode stateSpecs, Morph morph)
        {
            foreach (var property in stateSpecs)
            {
                // Ignore the name, encoding, and any other Morph specific properties properties
                if (property.Key == "name" || property.Key == "encoding" || property.Key == "access")
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
                // Condition 2: the value of this property is defined as a wildcard ("*"), is prefixed with "this." or "other.", or is the name of a Signal
                else if (statePropertyValue == "*" ||
                         ((string)statePropertyValue).StartsWith("this.") || ((string)statePropertyValue).StartsWith("other.") ||
                         morph.SignalNames.Contains(statePropertyValue))
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

        private bool CheckEncodingSpecsMatching(JSONNode visEncodingSpecs, JSONNode stateEncodingSpecs, Morph morph)
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
                // If the value of this encoding is null, it means that our vis specs should NOT have it
                // e.g., "x": null
                if (stateEncodingValue.IsNull)
                {
                    // If the vis specs does actually have this encoding with a properly defined value, then it fails the check
                    if (visEncodingValue != null && !visEncodingValue.IsNull)
                    {
                        return false;
                    }
                }
                // If the value of this encoding is as a wildcard ("*"), or is prefixed with "this." or "other.", or is the name of a Signal,
                // it means our vis specs should at least have this encoding, no matter its contents
                else if (stateEncodingValue == "*" ||
                         ((string)stateEncodingValue).StartsWith("this.") || ((string)stateEncodingValue).StartsWith("other.") ||
                         morph.SignalNames.Contains(stateEncodingValue)
                         )
                {
                    // The vis should have a value for this property. If it doesn't, it fails the check
                    if (visEncodingValue == null ||
                        (visEncodingValue != null && visEncodingValue.IsNull))
                        return false;

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

                        /// The value of this property in the state is defined as a wildcard ("*"), is prefixed with "this." or "other.", or is the name of a Signal
                        /// e.g.,: "x": {
                        ///          "field": "*"
                        ///          "type": "this.encoding.y.type"
                        ///         }
                        else if (stateEncodingPropertyValue == "*" ||
                                 ((string)stateEncodingPropertyValue).StartsWith("this.") || ((string)stateEncodingPropertyValue).StartsWith("other.") ||
                                 morph.SignalNames.Contains(stateEncodingPropertyValue))
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
            string transitionName = transitionSpec["name"];
            int transitionPriority = transitionSpec["priority"] != null ? transitionSpec["priority"].AsInt : 0;
            cancellationObservable.Subscribe(_ => queuedTransitionDeactivations.Add(transitionName, new Tuple<Action, int>(() => DeactivateTransition(candidateMorph, transitionSpec, transitionName, goToEnd), transitionPriority)))
                .AddTo(candidateMorph.CandidateTransitionsWithSubscriptions.Single(cts => cts.Item1["name"] == transitionSpec["name"]).Item2);

            // cancellationObservable.Subscribe(_ => DeactivateTransition(candidateMorph, transitionSpec, transitionSpec["name"], goToEnd))
            //     .AddTo(candidateMorph.CandidateTransitionsWithSubscriptions.Single(cts => cts.Item1["name"] == transitionSpec["name"]).Item2);

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
            GenerateVisSpecKeyframes(CurrentVisSpec,
                                     candidateMorph.Morph.GetStateFromName(transitionSpec["states"][0]),
                                     candidateMorph.Morph.GetStateFromName(transitionSpec["states"][1]),
                                     candidateMorph,
                                     isReversed,
                                     out initialState,
                                     out finalState);

            // Get the easing function which we will apply to this transition (if any). If no easing function is defined, we don't set the easing function
            // as we don't want to have to calculate the linear function when we don't need to
            Ease ease = (transitionSpec["timing"]["easing"] != null) ? EasingFunction.GetEaseFromString(transitionSpec["timing"]["easing"]) : Ease.Linear;
            Function easingFunction = (ease != Ease.Linear) ? EasingFunction.GetEasingFunction(ease) : null;

            // Get the set of stages that are defined in this transition (if any). We pass this onto the Vis
            Dictionary<string, Tuple<float, float>> stages = GetTransitionStages(transitionSpec, transitionName);

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
            // We pass in a Func instead as there is a chance that the transition will not be successfully applied. If this is the case,
            // then a tweening observable will not be created.
            Func<IObservable<float>> tweeningObservableCreateFunc = () => { return CreateTweeningObservable(candidateMorph, transitionSpec, isReversed); };

            bool success = ParentVis.ApplyTransition(transitionName, initialState, finalState, tweeningObservableCreateFunc, easingFunction, stages);

            if (success)
            {
                ActiveTransitionNames.Add(transitionName);

                // If the flag is set to true, then the system will automatically release the grab when the trasition begins
                if (transitionSpec["disable-grab"] != null && transitionSpec["disable-grab"] == true && objectManipulator != null)
                {
                    if (objectManipulator.isSelected)
                    {
                        // This is obviously using a deprecated function, will need to find the correct way of doing this
                        objectManipulator.interactionManager.CancelInteractableSelection(objectManipulator);

                        // Force set the position in case it has drifted
                        transform.position = restingPosition;
                        transform.rotation = restingRotation;
                    }
                }
            }
        }

        /// <summary>
        /// Stops the specified transition if there is any. Meant to be called by signal tweeners.
        ///
        /// Only actually activates the transition at the end of the frame. This is to ensure all Signals have emitted their values before making any changes to the Vis.
        /// This function should be called by adding an anonymous lambda to queuedTransitionActivations in the form () => ActivateTransition(...)
        /// As such, it is not necessary to call CheckForMorphs in this function, as it will be called automatically at the end of the frame
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

            ParentVis.StopTransition(transitionName, goToEnd, true);

            // Unsubscribe to this transition's signals
            candidateMorph.DisposeTransitionSubscriptions(transitionName);

            // Remove this transition from our active transitions
            ActiveTransitionNames.Remove(transitionName);

            // Change the CandidateState in our CandidateMorph object to the state that this vis has just transitioned to
            // This allows the next CheckForMorphs to be properly aware of the current state in the theoretical state machine
            string targetStateName = goToEnd ? transitionSpec["states"][1] : transitionSpec["states"][0];
            candidateMorph.CandidateState = candidateMorph.Morph.GetStateFromName(targetStateName);
        }

        /// <summary>
        /// Does a complete reset.
        ///
        /// Stops all active transitions if there are any, and resets the Morphable back to a neutral state.
        /// </summary>
        public void Reset(bool goToEnd = false, bool checkForMorphs = true)
        {
            // Dispose of all subscriptions
            foreach (CandidateMorph candidateMorph in CandidateMorphs)
            {
                candidateMorph.ClearLocalSignals();
            }

            // If there were transitions in progress, stop all of them
            foreach (string activeMorph in ActiveTransitionNames)
            {
                ParentVis.StopTransition(activeMorph, goToEnd);
            }
            ActiveTransitionNames.Clear();

            // Reset variables
            CandidateMorphs.Clear();
            CandidateMorphNames.Clear();
            CandidateStateNames.Clear();
            CandidateTransitionNames.Clear();

            // Check for morphs again to allow for further morphing without needing to update the vis
            if (checkForMorphs)
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
                ResolveLeafValuesInVisSpec(ref _finalVisSpec, _initialVisSpec, _finalVisSpec, _finalStateSpec, candidateMorph);
            }
            else
            {
                candidateMorph.StoredVisKeyframes[_finalStateSpec["name"].ToString()] = _originalVisSpec;
                _finalVisSpec = _originalVisSpec;
                _initialVisSpec = GenerateVisSpecFromStateSpecs(_finalVisSpec, _finalStateSpec, _initialStateSpec, candidateMorph);
                ResolveLeafValuesInVisSpec(ref _initialVisSpec, _initialVisSpec, _finalVisSpec, _initialStateSpec, candidateMorph);
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
        ///
        /// Note that any leaf values which reference Signals in the state specs will override the changes made by this function, as these override the keyframes
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
                if (property.Name == "data" || property.Name == "mark" || property.Name == "encoding" || property.Name == "access" ||
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
        /// This function does four things:
        /// 1. Ensures that all leaf values that were Signals in the final state spec remain as Signals, in order to override any values set from keyframes
        /// 2. Replaces all leaf values that include names of signals with said signal's values, in JToken form
        /// 3. Copies JTokens from one property/encoding to the other using JSON.NET's path format, prefixed with "this." or "other."
        /// 4. Evaluates all leaf values that are left as expressions
        private void ResolveLeafValuesInVisSpec(ref JObject visSpec, JObject initialVisSpec, JObject finalVisSpec, JObject finalStateSpec, CandidateMorph candidateMorph)
        {
            /// FUNCTION 1: Ensure leaf values are Signals

            // Get all leaf nodes in the final state spec
            var descendants = finalStateSpec.Descendants().Where(descendant => !descendant.HasValues);

            // Loop through and see if any of these are a signal
            foreach (var descendant in descendants)
            {
                if (candidateMorph.Morph.SignalNames.Contains(descendant.ToString()))
                {
                    // Replace any value in the vis spec with this one
                    (visSpec.SelectToken(descendant.Path).Parent as JProperty).Value = descendant;
                }
            }

            /// FUNCTION 2: Replace Signal leaf values with actual values; and
            /// FUNCTION 3: Copy JTokens prefixed with this. and other.

            // Get all leaf nodes in the vis spec
            descendants = visSpec.Descendants().Where(descendant => !descendant.HasValues);

            List<Tuple<string, JToken>> descendantsToUpdate = new List<Tuple<string, JToken>>();

            // Loop through and resolve each leaf value independently
            foreach (var descendant in descendants)
            {
                // Ignore certain properties
                if (descendant.Parent.Type == JTokenType.Property)
                {
                    string propertyName = ((JProperty)descendant.Parent).Name.ToString();
                    if (propertyName == "name" || propertyName == "title")
                        continue;
                }

                // Split the leaf into individual values. This allows us to resolve instances where multiple JSON path references are used
                string[] tokens = descendant.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries);

                for (int i = 0; i < tokens.Length; i++)
                {
                    string token = tokens[i];

                    // If this token references another property, resolve it
                    if (token.StartsWith("this.") || token.StartsWith("other."))
                    {
                        // If this token is part of an expression, but it is not of type

                        // We get the value from either the initial or final vis spec depending on the prefix
                        JObject specToCheck = null;
                        string sourcePath = "";
                        if (token.StartsWith("this."))
                        {
                            specToCheck = initialVisSpec;
                            sourcePath = token.Replace("this.", "");
                        }
                        else if (token.StartsWith("other."))
                        {
                            specToCheck = initialVisSpec;
                            sourcePath = token.Replace("other.", "");
                        }

                        JToken sourceValue = specToCheck.SelectToken(sourcePath);
                        if (sourceValue != null)
                        {
                            // Make sure that it and nothing inside of it is also another reference path
                            // There is probably a slim chance that this returns a false positive. Good enough for our purposes though
                            if (sourceValue.ToString().Contains("this.") || sourceValue.ToString().StartsWith("other."))
                            {
                                throw new Exception(string.Format("Vis Morphs: A JSON path reference in a state specification cannot refer to another property that also has a JSON path reference (i.e., no loops). Error found in {0}.", descendant.Path + "." + descendant.ToString()));
                            }
                            // If this isn't the only token in the split, we need to make sure that the data type is correct
                            else if (tokens.Length > 1)
                            {
                                if (sourceValue.Type == JTokenType.Integer || sourceValue.Type == JTokenType.Float || sourceValue.Type == JTokenType.String)
                                {
                                    // If the type is correct, replace the token with the new value. We will concatenate these later
                                    tokens[i] = sourceValue.ToString();
                                }
                                else
                                {
                                    throw new Exception(string.Format("Vis Morphs: A JSON path reference in a state specification cannot refer to a non string/number value while also using an expression. Error found in:\n{0}", descendant.Path + "." + descendant.ToString()));
                                }
                            }
                            else
                            {
                                descendantsToUpdate.Add(new Tuple<string, JToken>(descendant.Path, sourceValue));
                            }
                        }
                        else
                        {
                            throw new Exception(string.Format("Vis Morphs: Cound not find property from the JSON path reference \"{0}\"", token));
                        }
                    }
                }

                // If this value has more than one token (i.e., it's an expression)
                // Note that any values where we replaced the leaf with a JObject would have already been caught, meaning we don't have to worry about it here
                if (tokens.Length > 1)
                {
                    // Rejoin the tokens together. This will be the value set in the spec for now
                    JToken newValue = new JValue(string.Join(' ', tokens));
                    descendantsToUpdate.Add(new Tuple<string, JToken>(descendant.Path, newValue));
                }
            }

            // Properly update the descendants in the vis spec to resolve JSON path references
            foreach (var tuple in descendantsToUpdate)
            {
                var descendant = visSpec.SelectToken(tuple.Item1).Parent as JProperty;
                descendant.Value = tuple.Item2;
            }

            /// FUNCTION 4: Evaluate expressions

            // Now we do the process AGAIN, but this time resolving expressions and converting them to their appropriate JToken formats
            descendants = visSpec.Descendants().Where(descendant => !descendant.HasValues);
            descendantsToUpdate = new List<Tuple<string, JToken>>();

            foreach (var descendant in descendants)
            {
                // Ignore certain properties
                if (descendant.Parent.Type == JTokenType.Property)
                {
                    string propertyName = ((JProperty)descendant.Parent).Name.ToString();
                    if (propertyName == "name" || propertyName == "title")
                        continue;
                }

                // Check if the descendant is a string. If it's not, then chances are it isn't an expression nor a Signal
                if (descendant.Type == JTokenType.String)
                {
                    string descendantString = descendant.ToString();

                    // If the descendent has spaces, and is not the name of a field in the dataset, then there's a reasonably good chance it is an expression. Resolve it
                    if (descendantString.Contains(' ') && !ParentVis.data.fieldNames.Contains(descendantString))
                    {
                        // Pass the expression to the expression interpreter to resolve it
                        // The interpreter should already have the variables for our Signals already stored, meaning that we don't need to pass them onto it
                        dynamic value = MorphManager.Instance.EvaluateExpression(this, descendantString);
                        JToken valueJToken = ConvertDynamicValueToJToken(value);
                        descendantsToUpdate.Add(new Tuple<string, JToken>(descendant.Path, valueJToken));
                    }
                    // Otherwise, see if it's a Signal and set its value
                    else
                    {
                        JToken valueJToken = GetJTokenFromSignalName(descendantString, candidateMorph);
                        if (valueJToken != null)
                        {
                            descendantsToUpdate.Add(new Tuple<string, JToken>(descendant.Path, valueJToken));
                        }
                    }
                }
            }

            // Properly update the descendants in the vis spec again to resolve expressions and Signal names
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
                return ConvertDynamicValueToJToken(value);
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

        private JToken ConvertDynamicValueToJToken(dynamic value)
        {
            string[] split = value.GetType().ToString().Split('.');
            string type = split[split.Length - 1].ToLower();

            // NOTE: These are most of the C# built-in types + some Unity types. Add in more if an error was thrown
            switch (type)
            {
                case "boolean":
                    bool _boolean = (bool)value;
                    return new JValue(_boolean);

                case "byte":
                    byte _byte = (byte)value;
                    return new JValue(_byte);

                case "char":
                    char _char = (char)value;
                    return new JValue(_char);

                case "decimal":
                    decimal _decimal = (decimal)value;
                    return new JValue(_decimal);

                case "single":
                    float _single = (float)value;
                    return new JValue(_single);

                case "double":
                    double _double = (double)value;
                    return new JValue(_double);

                case "int16":
                    short _int16 = (short)value;
                    return new JValue(_int16);

                case "int32":
                    int _int32 = (int)value;
                    return new JValue(_int32);

                case "int64":
                    long _int = (long)value;
                    return new JValue(_int);

                case "string":
                    string _string = value.ToString();
                    return new JValue(_string);

                case "vector2":
                    Vector2 vector2 = (Vector3)value;
                    return new JArray(vector2.x, vector2.y);

                case "vector3":
                    Vector3 vector3 = (Vector3)value;
                    return new JArray(vector3.x, vector3.y, vector3.z);

                case "quaternion":
                    Quaternion quaternion = (Quaternion)value;
                    return new JArray(quaternion.x, quaternion.y, quaternion.z, quaternion.w);

                default:
                    throw new Exception(string.Format("Vis Morphs: Could not convert type {0} to a JToken. Please add your type into the switch statement.", value.GetType().ToString()));
            }
        }

        /// <summary>
        /// Creates a dictionary of the different stages defined within the given transition.
        /// The key is the name of the channel (must be unique), the value is a float tuple with two values between 0 to 1 (inclusive)
        /// Returns an empty dictionary if no stages are defined.
        /// </summary>
        private Dictionary<string, Tuple<float, float>> GetTransitionStages(JSONNode transitionSpec, string transitionName)
        {
            // Get the set of stages from the transition spec. We pass this onto the Vis when we're done
            Dictionary<string, Tuple<float, float>> stages = new Dictionary<string, Tuple<float, float>>();
            if (transitionSpec["staging"] != null)
            {
                foreach (var kvp in transitionSpec["staging"])
                {
                    if (stages.ContainsKey(kvp.Key))
                        throw new Exception(string.Format("Vis Morphs: Transition \"{0}\" has the staging channel \"{1}\" multiple times. All stages must be unique.", transitionName, kvp.Key));

                    if (kvp.Value.Count != 2 || !kvp.Value[0].IsNumber || !kvp.Value[1].IsNumber)
                        throw new Exception(string.Format("Vis Morphs: Transition \"{0}\" has a staging channel \"{1}\" that does not have two numbers in an array.", transitionName, kvp.Key));

                    float initialValue = kvp.Value[0].AsFloat;
                    float finalValue = kvp.Value[1].AsFloat;

                    if (initialValue < 0 || initialValue > 1 || finalValue < 0 || finalValue > 1)
                        throw new Exception(string.Format("Vis Morphs: Transition \"{0}\" has a staging channel \"{1}\" that has values not within 0 to 1 (inclusive).", transitionName, kvp.Key));

                    if (initialValue > finalValue)
                        throw new Exception(string.Format("Vis Morphs: Transition \"{0}\" has a staging channel \"{1}\" with a end value larger than the start value", transitionName, kvp.Key));

                    stages.Add(kvp.Key, new Tuple<float, float>(initialValue, finalValue));
                }
            }

            return stages;
        }

        private void OnDestroy()
        {
            Reset(checkForMorphs: false);
            ParentVis.VisUpdated.RemoveListener(VisUpdated);
            MorphManager.Instance.ClearMorphableVariables(this);
        }
    }
}