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
        public string ActiveTransitionName;

        public List<CandidateMorph> CandidateMorphs = new List<CandidateMorph>();

        private Vis parentVis;
        private JSONNode currentVisSpec;
        private bool isInitialised;
        private JSONNode activeTransition;
        private bool isTransitionActive = false;
        private bool isTransitionReversed = false;

        private void Start()
        {
            Initialise();
        }

        public void Initialise()
        {
            if (!isInitialised)
            {
                parentVis = GetComponent<Vis>();
                parentVis.VisUpdated.AddListener(VisUpdated);
                isInitialised = true;
            }
            // If this Morphable is already initialised, call Reset() again just to be safe
            else
            {
                Reset();
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

            // We reset our morphing variables each time the visualisation is updated
            // TODO: Make this retain some information between morphs if the candidates are still the same, for performance reasons
            Reset();

            // First, we get a list of Morphs which we deem as "candidates"
            // Each object in this list also stores the set of candidate states and transitions which match our current vis spec
            List<CandidateMorph> newCandidateMorphs = new List<CandidateMorph>();
            GetCandidateMorphs(visSpec, ref newCandidateMorphs);

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
                    CandidateStateNames = CandidateMorphs.SelectMany(_ => _.CandidateStates).Select(_ => _["name"].ToString()).ToList();
                    CandidateTransitionNames = CandidateMorphs.SelectMany(_ => _.CandidateTransitions).Select(_ => _.Item1["name"].ToString()).ToList();
                }
            }
            else
            {
                CandidateMorphs.Clear();
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
            foreach (CandidateMorph candidateMorph in candidateMorphs)
            {
                foreach (Tuple<JSONNode, bool> candidateTransition in candidateMorph.CandidateTransitions)
                {
                    // Create the data structures which we need to store information regarding each of these candidate transitions with subscriptions
                    JSONNode transitionSpec = candidateTransition.Item1;
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
                            observable.Subscribe(f =>
                            {
                                boolList[0] = 0 < f && f < 1;

                                // If all of the predicates for this Transition returned true...
                                if (!boolList.Contains(false))
                                {
                                    // AND there is not an already active transition, we can then formally activate the Transition
                                    if (!isTransitionActive)
                                        ActivateTransition(candidateMorph, transitionSpec, isReversed);
                                }
                                else
                                {
                                    // Otherwise, if this Tweener has now reached either end of the tweening range, end the transition
                                    if (isTransitionActive)
                                    {
                                        // If the tweening value is 1 or more, the Vis should rest at the final state
                                        bool goToEnd = f >= 1;
                                        DeactivateTransition(candidateMorph, transitionSpec, goToEnd);
                                    }
                                }
                            }).AddTo(disposables);
                        }
                    }

                    // Subscribe to the rest of the Triggers. If no triggers are defined, then we can just skip this process entirely
                    var triggerNames = transitionSpec["triggers"];
                    if (triggerNames != null)
                    {
                        for (int i = 0; i < triggerNames.Count; i++)
                        {
                            // Set the index that will be used to then modify the boolean in our boolArray
                            int index = boolList.Count;
                            boolList.Add(false);

                            // Get the corresponding Signal observable by using its name, casting it to a boolean
                            var observable = candidateMorph.GetLocalSignal(triggerNames[i]);
                            if (observable == null) observable = MorphManager.Instance.GetGlobalSignal(triggerNames[i]);

                            if (observable != null)
                            {
                                IObservable<bool> triggerObservable = observable.Select(x => (bool)x);

                                triggerObservable.Subscribe(b =>
                                {
                                    boolList[index] = b;

                                    // If all of the predicates for this Transition returned true...
                                    if (!boolList.Contains(false))
                                    {
                                        // AND there is not an already active transition, we can then formally activate the Transition
                                        if (!isTransitionActive)
                                            ActivateTransition(candidateMorph, transitionSpec, isReversed);
                                    }
                                    else
                                    {
                                        // Otherwise, if this Transition WAS active but now no longer meets the trigger conditions,
                                        if (isTransitionActive)
                                        {
                                            // If the Transition specification includes which direction (start or end) to reset the Vis to, we use it
                                            bool goToEnd = false;

                                            if (transitionSpec["interrupt"]["control"] == "reset")
                                            {
                                                goToEnd = transitionSpec["interrupt"]["value"] == "end";
                                            }

                                            DeactivateTransition(candidateMorph, transitionSpec, goToEnd);
                                        }
                                    }
                                }).AddTo(disposables);
                            }
                            else
                            {
                                throw new Exception(string.Format("Vis Morphs: Trigger with name {0} cannot be found.", triggerNames[i]));
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

        private void ActivateTransition(CandidateMorph candidateMorph, JSONNode transitionSpec, bool isReversed = false)
        {
            if (isTransitionActive)
                return;

            ActiveTransitionName = candidateMorph.Morph.Name + "." + transitionSpec["name"];

            activeTransition = transitionSpec;
            isTransitionReversed = isReversed;

            // Generate the initial and final vis states
            JSONNode initialState, finalState;
            if (!isTransitionReversed)
            {
                initialState = currentVisSpec;
                finalState = GenerateVisSpecKeyframeFromState(initialState,
                    candidateMorph.Morph.GetStateFromName(activeTransition["states"][0]),
                    candidateMorph.Morph.GetStateFromName(activeTransition["states"][1]));
            }
            else
            {
                // If the transition is being called in the reversed direction, the candidate vis actually starts from the *final* state,
                // in order to not mess with the tweening value. Note this does mean that it doesn't really work with time-based tweens
                finalState = currentVisSpec;
                initialState = GenerateVisSpecKeyframeFromState(finalState,
                    candidateMorph.Morph.GetStateFromName(activeTransition["states"][1]),
                    candidateMorph.Morph.GetStateFromName(activeTransition["states"][0]));
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

            // Change to initial state instantly
            parentVis.UpdateVisSpecsFromJSONNode(initialState, false, false);

            // Call update to final state using a tweening observable
            var tweeningObservable = CreateMorphTweenObservable(candidateMorph, transitionSpec);
            parentVis.ApplyVisMorph(finalState, tweeningObservable);

            isTransitionActive = true;
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
                        _newVisSpecs["encoding"][encoding.Name]["field"]?.Parent.Remove();
                        _newVisSpecs["encoding"][encoding.Name]["value"]?.Parent.Remove();
                    }

                    ((JObject)_newVisSpecs["encoding"][encoding.Name]).Merge(
                        _finalStateSpecs["encoding"][encoding.Name], new JsonMergeSettings
                        {
                            MergeArrayHandling = MergeArrayHandling.Replace,
                            MergeNullValueHandling = MergeNullValueHandling.Merge
                        });
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

        private IObservable<float> CreateMorphTweenObservable(CandidateMorph candidateMorph, JSONNode transitionSpec)
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

        private IObservable<float> CreateTimerObservable(CandidateMorph candidateMorph, JSONNode transitionSpecs, float duration)
        {
            float startTime = Time.time;

            var cancellationObservable = Observable.Timer(TimeSpan.FromSeconds(duration));
            var timerObservable = Observable.EveryUpdate().Select(_ =>
            {
                float timer = Time.time - startTime;
                return Mathf.Clamp(timer / duration, 0, 1);
            })
                .TakeUntil(cancellationObservable);

            cancellationObservable.Subscribe(_ => DeactivateTransition(candidateMorph, transitionSpecs, true));

            return timerObservable;
        }

        private void DeactivateTransition(CandidateMorph candidateMorph, JSONNode transitionSpec, bool goToEnd = true)
        {
            if (isTransitionActive)
            {
                // Only the morph which activated the transition can stop it
                string deactivatingTransitionName = candidateMorph.Morph.Name + "." + transitionSpec["name"];

                if (ActiveTransitionName == deactivatingTransitionName)
                {
                    Reset(goToEnd);

                    CheckForMorphs();
                }
            }

        }

        public void Reset(bool goToEnd = false)
        {
            // Dispose of all subscriptions before doing anything else
            foreach (CandidateMorph candidateMorph in CandidateMorphs)
            {
                foreach (var candidateTransition in candidateMorph.CandidateTransitionsWithSubscriptions)
                {
                    candidateTransition.Item2.Dispose();
                }
            }

            CandidateMorphs.Clear();

            if (isTransitionActive)
            {
                parentVis.StopVisMorph(goToEnd);
                isTransitionActive = false;
                isTransitionReversed = false;
            }
        }

        private void OnDestroy()
        {
            Reset();
            parentVis.VisUpdated.RemoveListener(VisUpdated);
        }
    }
}