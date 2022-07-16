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
        /// <summary>
        /// Debug variables. These don't actually do anything in the code other than print values to the Unity Inspector
        /// </summary>
        public bool DebugStates = false;
        public bool ShowValuesInInspector = false;
        public List<string> CandidateMorphNames = new List<string>();
        public List<string> CandidateStateNames = new List<string>();
        public List<string> CandidateTransitionNames = new List<string>();

        public List<string> ActiveTransitionNames = new List<string>();
        public List<CandidateMorph> CandidateMorphs = new List<CandidateMorph>();

        public bool AllowSimultaneousTransitions = true;

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

                queuedTransitionDeactivations.Clear();
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

                queuedTransitionActivations.Clear();
            }
        }

        public void Initialise()
        {
            if (!isInitialised)
            {
                parentVis = GetComponent<Vis>();
                parentVis.VisUpdated.AddListener(VisUpdated);
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
            var newCandidateTransitions = newCandidateMorphs.SelectMany(cm => cm.CandidateTransitions).Select(ct => (string)ct.Item1["name"]);

            List<string> transferredMorphNames = new List<string>();

            foreach (string activeTransitionName in ActiveTransitionNames.ToList())
            {
                // If the active transition is in the new list of candidate transitions, transfer its variable references over
                if (newCandidateTransitions.Contains(activeTransitionName))
                {
                    // Get the morph that this active transition belongs to
                    CandidateMorph sourceMorph = oldCandidateMorphs.Single(cm => cm.CandidateTransitions.Select(ct => (string)ct.Item1["name"]).Contains(activeTransitionName));
                    // Only copy over the equivalent transition and its subscriptions, and the local signal references
                    CandidateMorph targetMorph = newCandidateMorphs.Single(cm => cm.Name == sourceMorph.Name);

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

                    // We now actually create our subscriptions themselves

                    // If this Transition is using a Predicate as a tweener, we subscribe to it such that the Transition only actually begins if this tweener returns a value between 0 and 1 (exclusive)
                    if (transitionSpec["timing"] != null && transitionSpec["timing"]["control"] != null)
                    {
                        // Get the observable associated with the tweener signal. Check both the global and local signals for it
                        string tweenerName = transitionSpec["timing"]["control"];
                        var observable = candidateMorph.GetLocalSignal(tweenerName);
                        if (observable == null) observable = MorphManager.Instance.GetGlobalSignal(tweenerName);

                        if (observable != null)
                        {
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

                    // Subscribe to the rest of the Triggers. If no triggers are defined, then we can just skip this process entirely
                    var triggerNames = transitionSpec["triggers"];
                    if (triggerNames != null)
                    {
                        for (int k = 0; k < triggerNames.Count; k++)
                        {
                            // Set the index that will be used to then modify the boolean in our boolArray
                            int index = boolList.Count;
                            boolList.Add(false);

                            // Get the name of the trigger. If it has an ! in front of it, we inverse its value
                            string triggerName = triggerNames[k];
                            bool triggerInversed = triggerName.StartsWith("!");
                            if (triggerInversed) triggerName = triggerName.Remove(0, 1);      // Remove the ! from the name now

                            // Get the corresponding Signal observable by using its name, casting it to a boolean
                            var observable = candidateMorph.GetLocalSignal(triggerName);
                            if (observable == null) observable = MorphManager.Instance.GetGlobalSignal(triggerName);

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
                // Otherwise, we check all of the properties within this property (i.e., field, value, type) to ensure they match
                else
                {
                    foreach (var stateEncodingProperty in stateEncodingValue)
                    {
                        JSONNode stateEncodingPropertyValue = stateEncodingProperty.Value;
                        JSONNode visEncodingPropertyValue = visEncodingValue[stateEncodingProperty.Key];

                        // Condition 1: the value of this state property is defined, but as null
                        // e.g.,: "x": {
                        //          "field": null
                        //         }
                        if (stateEncodingProperty.Value.IsNull)
                        {
                            if (visEncodingPropertyValue != null && !visEncodingPropertyValue.IsNull)
                                return false;
                        }
                        // Condition 2: the value of this property is defined as a wildcard ("*")
                        // e.g.,: "x": {
                        //          "field": "*"
                        //         }
                        else if (stateEncodingProperty.Value.ToString() == "\"*\"")
                        {
                            if (visEncodingPropertyValue == null ||
                                (visEncodingPropertyValue != null && visEncodingPropertyValue.IsNull))
                                return false;
                        }
                        // Condition 3: the value of this property is some specific value
                        // e.g.,: "x": {
                        //          "field": "Miles_Per_Gallon"
                        //         }
                        else
                        {
                            if (visEncodingPropertyValue == null ||
                                (visEncodingPropertyValue != null && visEncodingPropertyValue.ToString() != stateEncodingPropertyValue.ToString()))
                                return false;
                        }
                    }
                }
            }

            return true;
        }

        /// <summary>
        /// Starts the given transition only if there is not already one with the same name already active. Meant to be called by signal tweeners.
        ///
        /// Only actually activates the transition at the end of the frame. This is to ensure all Signals have emitted their values before making any changes to the Vis.
        /// This function should be called by adding an anonymous lambda to queuedTransitionActivations in the form () => ActivateTransition(...)
        /// </summary>
        private void ActivateTransition(CandidateMorph candidateMorph, JSONNode transitionSpec, string transitionName, bool isReversed = false)
        {
            if (ActiveTransitionNames.Contains(transitionName))
            {
                Debug.LogError(string.Format("Vis Morphs: Transition {0} tried to activate, but it is already active", transitionName));
                return;
            }

            // Generate the initial and final vis states
            JSONNode initialState, finalState;
            if (!isReversed)
            {
                initialState = currentVisSpec;
                finalState = GenerateVisSpecKeyframeFromState(initialState,
                    candidateMorph.Morph.GetStateFromName(transitionSpec["states"][0]),
                    candidateMorph.Morph.GetStateFromName(transitionSpec["states"][1]));
            }
            else
            {
                // If the transition is being called in the reversed direction, the candidate vis actually starts from the *final* state,
                // in order to not mess with the tweening value. Note this does mean that it doesn't really work with time-based tweens
                finalState = currentVisSpec;
                initialState = GenerateVisSpecKeyframeFromState(finalState,
                    candidateMorph.Morph.GetStateFromName(transitionSpec["states"][1]),
                    candidateMorph.Morph.GetStateFromName(transitionSpec["states"][0]));
            }

            if (DebugStates)
            {
                JSONNode _initialState = initialState.Clone();
                JSONNode _finalState = finalState.Clone();
                _initialState.Remove("data");
                _finalState.Remove("data");
                Debug.Log("Vis Morphs: Initial state specification:\n" + _initialState.ToString());
                Debug.Log("Vis Morphs: Final state specification:\n" + _finalState.ToString());
            }

            // Call update to final state using a tweening observable
            var tweeningObservable = CreateTweeningObservable(candidateMorph, transitionSpec);
            bool success = parentVis.ApplyTransition(transitionName, initialState, finalState, tweeningObservable, isReversed);

            if (success)
            {
                ActiveTransitionNames.Add(transitionName);
            }
        }


        private JSONNode GenerateVisSpecKeyframeFromState(JSONNode visSpecs, JSONNode initialStateSpecs, JSONNode finalStateSpecs)
        {
            // SimpleJSON doesn't really work well with editing JSON objects, so we just use JSON.NET instead
            JObject _initialStateSpecs = Newtonsoft.Json.Linq.JObject.Parse(initialStateSpecs.ToString());
            JObject _finalStateSpecs = Newtonsoft.Json.Linq.JObject.Parse(finalStateSpecs.ToString());

            // Create another vis specs object which will be the one which we are actually modifying
            // But first, we should remove the data property from the vis spec in order to not have to serialise and parse it twice
            JSONNode dataSpecs = visSpecs["data"];
            visSpecs.Remove("data");
            JObject _newVisSpecs = Newtonsoft.Json.Linq.JObject.Parse(visSpecs.ToString());


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

            foreach (var encoding in ((JObject)_initialStateSpecs["encoding"]).Properties())
            {
                // Step 1: Check which encodings to remove
                if (IsJTokenNullOrUndefined(_finalStateSpecs["encoding"][encoding.Name]))
                {
                    _newVisSpecs["encoding"][encoding.Name]?.Parent.Remove();
                }
            }

            foreach (var encoding in ((JObject)_finalStateSpecs["encoding"]).Properties())
            {
                // Ignore any encodings marked as a wildcard (*)
                if (encoding.Value.ToString() == "*")
                    continue;

                // Ignore any encodings that are defined as null, as these were already handled in Step 1
                if (IsJTokenNullOrUndefined(encoding.Value))
                    continue;

                // Step 2a: Add any encodings to the vis state
                if (IsJTokenNullOrUndefined(_initialStateSpecs["encoding"][encoding.Name]))
                {
                    // TODO: We might have to resolve any instances of fields being declared by reference
                    // We use first Remove then Add here to forcefully replace any encodings already in the vis spec
                    _newVisSpecs["encoding"][encoding.Name]?.Parent.Remove();
                    ((JObject)_newVisSpecs["encoding"]).Add(encoding);
                }
                // Step 2b: Modify any encodings that are defined in both
                else
                {
                    // Step 2bi: Make sure that the vis state doesn't have both a field and value (the final state takes priority)
                    if (!IsJTokenNullOrUndefined(_finalStateSpecs["encoding"][encoding.Name]["field"]) || !IsJTokenNullOrUndefined(_finalStateSpecs["encoding"][encoding.Name]["value"]))
                    {
                        // If the final state has either a field or value, we remove fields and values from the vis state to simplify the merging later
                        if (_newVisSpecs["encoding"][encoding.Name] != null)
                        {
                            if (_newVisSpecs["encoding"][encoding.Name]["field"] != null)
                                _newVisSpecs["encoding"][encoding.Name]["field"].Parent.Remove();
                            if (_newVisSpecs["encoding"][encoding.Name]["value"] != null)
                                _newVisSpecs["encoding"][encoding.Name]["value"]?.Parent.Remove();
                        }
                    }

                    ((JObject)_newVisSpecs["encoding"][encoding.Name]).Merge(
                        _finalStateSpecs["encoding"][encoding.Name], new JsonMergeSettings
                        {
                            MergeArrayHandling = MergeArrayHandling.Replace,
                            MergeNullValueHandling = MergeNullValueHandling.Merge
                        });
                }
            }

            // We now also apply the same logic as above, except this time to the view-level properties
            foreach (var property in ((JObject)_initialStateSpecs).Properties())
            {
                if (property.Name == "data" || property.Name == "mark" || property.Name == "encoding" || property.Name == "name" || property.Name == "title")
                    continue;

                // Step 1: Check which encodings to remove
                if (IsJTokenNullOrUndefined(_finalStateSpecs["encoding"][property.Name]))
                {
                    _newVisSpecs[property.Name]?.Parent.Remove();
                }
            }

            foreach (var property in ((JObject)_finalStateSpecs).Properties())
            {
                if (property.Name == "data" || property.Name == "mark" || property.Name == "encoding" || property.Name == "name" || property.Name == "title")
                    continue;

                // Ignore any properties that are defined as null, as these were already handled in Step 1
                if (IsJTokenNullOrUndefined(property.Value))
                    continue;

                // Step 2a: Add any properties to the vis state
                if (IsJTokenNullOrUndefined(_initialStateSpecs[property.Name]))
                {
                    _newVisSpecs[property.Name]?.Parent.Remove();
                    ((JObject)_newVisSpecs).Add(property);
                }
                // Step 2b: Modify any properties that are defined in both
                else
                {
                    if (!IsJTokenNullOrUndefined(_finalStateSpecs[property.Name]))
                    {
                        // Take the value from the final state
                        if (_newVisSpecs[property.Name] != null)
                            _newVisSpecs[property.Name].Parent.Remove();
                        _newVisSpecs.Add(property.Name, _finalStateSpecs[property.Name]);
                    }
                }
            }

            // Clean up any nulls in the vis specs
            RemoveNullProperties(ref _newVisSpecs);

            // Add the data specs back in
            JSONNode __newVisSpecs = JSONObject.Parse(Newtonsoft.Json.JsonConvert.SerializeObject(_newVisSpecs));
            visSpecs.Add("data", dataSpecs);
            __newVisSpecs.Add("data", dataSpecs);

            return __newVisSpecs;
        }

        private bool IsJTokenNullOrUndefined(JToken? jObject)
        {
            return jObject == null || (jObject != null && jObject.Type == JTokenType.Null);
        }

        /// <summary>
        /// Removes all properties with a value of null in a JSON.NET object
        /// </summary>
        private void RemoveNullProperties(ref JObject specs)
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

        private IObservable<float> CreateTweeningObservable(CandidateMorph candidateMorph, JSONNode transitionSpec)
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
                    return CreateTimerObservable(candidateMorph, transitionSpec, timingSpecs["control"]);
                }
            }
        }

        private IObservable<float> CreateTimerObservable(CandidateMorph candidateMorph, JSONNode transitionSpec, float duration)
        {
            float startTime = Time.time;

            var cancellationObservable = Observable.Timer(TimeSpan.FromSeconds(duration));
            var timerObservable = Observable.EveryUpdate().Select(_ =>
            {
                float timer = Time.time - startTime;
                return Mathf.Clamp(timer / duration, 0, 1);
            })
                .TakeUntil(cancellationObservable);

            bool goToEnd = transitionSpec["timing"]["elapsed"] != null ? transitionSpec["timing"]["elapsed"] == "end" : true;

            // HACKY WORKAROUND: We need some way to cancel this timer early in case it gets interrupted. For now we just find the composite
            // disposable tied to the transition and the subscription to it. Ideally this should be done alongside with all of the other signals
            cancellationObservable.Subscribe(_ => DeactivateTransition(candidateMorph, transitionSpec, transitionSpec["name"], goToEnd))
                .AddTo(candidateMorph.CandidateTransitionsWithSubscriptions.Single(cts => cts.Item1["name"] == transitionSpec["name"]).Item2);

            return timerObservable;
        }

        /// <summary>
        /// Stops the specified transition if there is any. Meant to be called by signal tweeners.
        ///
        /// Only actually activates the transition at the end of the frame. This is to ensure all Signals have emitted their values before making any changes to the Vis.
        /// This function should be called by adding an anonymous lambda to queuedTransitionActivations in the form () => ActivateTransition(...)
        /// </summary>
        private void DeactivateTransition(CandidateMorph candidateMorph, JSONNode transitionSpec, string transitionName, bool goToEnd = true)
        {
            if (!ActiveTransitionNames.Contains(transitionName))
            {
                Debug.LogError(string.Format("Vis Morphs: Transition {0} tried to deactivate, but it is not active in the first place", transitionName));
                return;
            }

            parentVis.StopTransition(transitionName, goToEnd);

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

        private void OnDestroy()
        {
            Reset();
            parentVis.VisUpdated.RemoveListener(VisUpdated);
        }
    }
}