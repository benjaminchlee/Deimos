using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using SimpleJSON;
using UnityEditor;
using UnityEngine;
using Newtonsoft.Json.Linq;
using UniRx;

namespace DxR.VisMorphs.Deprecated
{
    /// <summary>
    /// Reads in a Morph JSON specification and applies morphs to any eligible DxR Vis.
    ///
    /// Currently, this only supports morphs being applied to a SINGLE Vis at a time. This means that morphs will only
    /// accurately apply to the most recently updated visualisation, and transition cannot occur simultaneously on two
    /// or more Vis objects.
    ///
    /// TODO: Make this support multiple Vis's.
    /// </summary>
    public class MorphManager : MonoBehaviour
    {
        public static MorphManager Instance { get; private set; }

        public bool DebugStates = false;

        [TextArea(5, 60)]
        public string Json;

        /// <summary>
        /// JSON container objects for the different specifications provided by the Json string
        /// </summary>
        private JSONNode transformationSpecification;
        private JSONNode statesSpecification;
        private JSONNode transitionsSpecification;

        /// <summary>
        /// Variables relating to the currently active morph
        /// </summary>
        private Vis candidateVis;                   // The Vis that matches one of the states in the Morph specification and is therefore a candidate for morphing
        private bool isCandidateActive = false;
        private JSONNode currentState;              // The state in the Morph specification which the candidate Vis matches
        private List<Tuple<JSONNode, CompositeDisposable, bool>> candidateTransitions; // The transitions in the Morph specification which are candidates to be applied to the Vis, assuming that their Predicates are matched
        private JSONNode currentTransition;         // The transition in the Morph specification which is currently being applied (i.e., the Vis is morphing)
        private bool isTransitionActive = false;    // Indicates whether the Morph is being applied or not

        private bool isInitialised = false;
        private bool isTransitionReversed = false;

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

            ReadTransformationJson(Json);
        }

        private void OnValidate()
        {
            if (EditorApplication.isPlaying && isInitialised)
            {
                ReadTransformationJson(Json);

                // Dirty hack override: Call the ActiveVisualisationSpecficationUpdated function an all visualisations to update them
                foreach (Vis vis in FindObjectsOfType<Vis>())
                    VisUpdated(vis, vis.GetVisSpecs());
            }
        }

        public bool ReadTransformationJson(string jsonString)
        {
            if (jsonString == "")
                return false;

            return ReadTransformationJson(JSON.Parse(jsonString));
        }

        public bool ReadTransformationJson(JSONNode transformationsSpecs)
        {
            if (isInitialised)
            {
                SignalManager.Instance.ResetObservables();
                // PredicateManager.Instance.ResetObservables();
            }

            transformationSpecification = transformationsSpecs;

            // LAZY IMPLEMENTATION: Use Newtonsoft's JSON.NET for now, will need to swap to SimpleJSON later
            JObject jObject = JObject.Parse(transformationsSpecs.ToString());
            JToken signalJToken = jObject.SelectToken("signals");
            SignalManager.Instance.GenerateSignals(signalJToken);

            // JToken predicateJToken = jObject.SelectToken("predicates");
            // PredicateManager.Instance.GeneratePredicates(predicateJToken);

            statesSpecification = transformationsSpecs["states"];
            transitionsSpecification = transformationsSpecs["transitions"];

            isInitialised = true;

            return true;
        }

        public void RegisterVisualisation(Vis vis)
        {
            vis.VisUpdated.AddListener(VisUpdated);
        }

        public void DeregisterVisualisation(Vis vis)
        {
            vis.VisUpdated.RemoveListener(VisUpdated);
        }

        /// <summary>
        /// Called whenever a DxR visualisation is updated.
        /// </summary>
        /// <param name="vis"></param>
        /// <param name="visSpecs"></param>
        private void VisUpdated(Vis vis, JSONNode visSpecs)
        {
            if (!isInitialised)
                return;

            // If there is already a morph that is being applied by this MorphManager on a different Vis, then we ignore it
            if (isCandidateActive && vis != candidateVis)
                return;

            // Get the state specification that matches the Vis which was just updated, if there even is one
            var newCurrentState = GetMatchingState(visSpecs);

            // If there is a matching state specification, we continue
            if (newCurrentState != null)
            {
                // If the new state is the same as the old one, we ignore
                // NOTE THIS MIGHT BREAK THINGS
                if (newCurrentState == currentState)
                    return;

                // If this Vis was actually undergoing a Morph and has changed states, we should reset the (now previous) Morph before we progress further
                if (isTransitionActive && currentState["name"] != newCurrentState["name"])
                {
                    ResetMorph();
                }

                candidateVis = vis;
                isCandidateActive = true;
                currentState = newCurrentState;
                Debug.Log("VISMORPHS: New state \"" + currentState["name"] + "\" found!");

                // Get a list of Transition specifications that have our new Current State as the starting state
                List<Tuple<JSONNode, bool>> newCandidateTransitions = GetCandidateTransitionsFromState(currentState);

                // Create Tuples of JSONNode, CompositeDisposable, and boolean which tracks:
                // 1. What each Candidate Transition is
                // 2. The subscriptions of that Candidate Transition to its defined Triggers
                // 3. Whether or not the transition is being applied from a reverse direction
                candidateTransitions = new List<Tuple<JSONNode, CompositeDisposable, bool>>();
                foreach (var transitionInfo in newCandidateTransitions)
                {
                    SubscribeToTransitionTriggers(transitionInfo.Item1, transitionInfo.Item2, ref candidateTransitions);
                }
            }
            // Otherwise, reset the morph. Even if no morph is currently in progress, we do this anyway just to be extra safe
            else
            {
                ResetMorph();
            }
        }

        private void ResetMorph()
        {
            candidateVis = null;

            // Dispose of all subscriptions
            // If there was a Transition in progress, terminate it early
            if (isTransitionActive)
            {
                DeactivateTransition(currentTransition);

                // Dispose of all subscriptions
                foreach (var tuple in candidateTransitions)
                {
                    tuple.Item2.Dispose();
                }
                candidateTransitions.Clear();
            }

            candidateVis = null;
            currentState = null;
            isCandidateActive = false;
            isTransitionActive = false;
            isTransitionReversed = false;
        }

        /// <summary>
        /// Creates all required subscriptions to a Transition's defined Triggers. These Triggers hook onto the Signals defined separately in the Morph
        /// </summary>
        private void SubscribeToTransitionTriggers(JSONNode transitionSpecs, bool isReversed, ref List<Tuple<JSONNode, CompositeDisposable, bool>> candidateTransitions)
        {
            // Create an array of booleans that will be modified by the later observables
            // This is probably bad coding practice but eh it works for now
            List<bool> boolList = new List<bool>();

            // Create a new CompositeDisposable that will store the subscriptions for this specific transitionSpecs
            CompositeDisposable disposables = new CompositeDisposable();

            // If this Transition is using a Predicate as a tweener, we subscribe to it such that the Transition only
            // actually begins if this tweener returns a value between 0 and 1 (exclusive)
            if (transitionSpecs["timing"] != null && transitionSpecs["timing"]["control"] != null)
            {
                string tweenerName = transitionSpecs["timing"]["control"];
                var observable = SignalManager.Instance.GetObservable(tweenerName);

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
                                ActivateTransition(transitionSpecs, isReversed);
                        }
                        else
                        {
                            // Otherwise, if this Tweener has now reached either end of the tweening range, end the transition
                            if (isTransitionActive && currentTransition == transitionSpecs)
                            {
                                // If the tweening value is 1 or more, the Vis should rest at the final state
                                bool goToEnd = f >= 1;
                                DeactivateTransition(transitionSpecs, goToEnd);
                            }
                        }
                    }).AddTo(disposables);
                }
            }

            // Subscribe to the rest of the Triggers. If no triggers are defined, then we can just skip this process entirely
            var triggerNames = transitionSpecs["triggers"];
            if (triggerNames != null)
            {
                for (int i = 0; i < triggerNames.Count; i++)
                {
                    // Set the index that will be used to then modify the boolean in our boolArray
                    int index = boolList.Count;
                    boolList.Add(false);

                    // Get the corresponding Signal observable by using its name, casting it to a boolean
                    IObservable<bool> triggerObservable = SignalManager.Instance.GetObservable(triggerNames[i]).Select(x => (bool)x);
                    triggerObservable.Subscribe(b =>
                    {
                        boolList[index] = b;

                        // If all of the predicates for this Transition returned true...
                        if (!boolList.Contains(false))
                        {
                            // AND there is not an already active transition, we can then formally activate the Transition
                            if (!isTransitionActive)
                                ActivateTransition(transitionSpecs, isReversed);
                        }
                        else
                        {
                            // Otherwise, if this Transition WAS active but now no longer meets the trigger conditions,
                            if (isTransitionActive && currentTransition == transitionSpecs)
                            {
                                // If the Transition specification includes which direction (start or end) to reset the Vis to, we use it
                                bool goToEnd = false;

                                if (transitionSpecs["interrupt"]["control"] == "reset")
                                {
                                    goToEnd = transitionSpecs["interrupt"]["value"] == "end";
                                }

                                DeactivateTransition(transitionSpecs, goToEnd);
                            }
                        }
                    }).AddTo(disposables);
                }
            }

            candidateTransitions.Add(new Tuple<JSONNode, CompositeDisposable, bool>(transitionSpecs, disposables, isReversed));
        }

        private void ActivateTransition(JSONNode newTransitionSpecs, bool isReversed = false)
        {
            if (candidateVis == null)
                return;

            currentTransition = newTransitionSpecs;
            isTransitionReversed = isReversed;

            // Generate the initial and final states
            JSONNode initialState, finalState;
            if (!isTransitionReversed)
            {
                initialState = candidateVis.GetVisSpecs();
                finalState = GenerateVisSpecKeyframeFromState(initialState, GetStateFromName(currentTransition["states"][0]), GetStateFromName(currentTransition["states"][1]));
            }
            else
            {
                // If the transition is being called in the reversed direction, the candidate vis actually starts from the *final* state,
                // in order to not mess with the tweening value. Note this does mean that it doesn't really work with time-based tweens
                finalState = candidateVis.GetVisSpecs();
                initialState = GenerateVisSpecKeyframeFromState(finalState, GetStateFromName(currentTransition["states"][1]), GetStateFromName(currentTransition["states"][0]));
                //candidateVis.UpdateVisSpecsFromJSONNode(initialState, false, false);
            }

            if (DebugStates)
            {
                var initial = JSONNode.Parse(initialState.ToString());
                initial.Remove("data");
                Debug.Log("DEBUG INITIAL STATE: " + initial.ToString());
                var final = JSONNode.Parse(finalState.ToString());
                final.Remove("data");
                Debug.Log("DEBUG FINAL STATE " + final.ToString());
            }

            // Change to initial state instantly
            candidateVis.UpdateVisSpecsFromJSONNode(initialState, false, false);

            // Call update to final state using a tweening observable
            var tweeningObservable = CreateMorphTweenObservable(currentTransition);
            candidateVis.ApplyVisMorph(finalState, tweeningObservable);

            isTransitionActive = true;
            Debug.Log("MORPHS: New transition \"" + currentTransition["name"] + "\" now active!");
        }

        private void DeactivateTransition(JSONNode oldTransitionSpecs, bool goToEnd = false)
        {
            if (candidateVis == null || !isTransitionActive)
                return;

            candidateVis.StopVisMorph(goToEnd);

            isTransitionActive = false;
            isTransitionReversed = false;
            Debug.Log("MORPHS: Transition \"" + oldTransitionSpecs["name"] + "\" now deactive!");

            // Do another check on this Vis to see if it now matches any new states
            //VisUpdated(candidateVis, candidateVis.GetVisSpecs());
        }

        /// <summary>
        /// Checks for and returns the state specified in the transformation JSON which the given visualisation specification
        /// matches. If no state matches, returns null
        /// </summary>
        /// <param name="visSpec"></param>
        /// <returns></returns>
        private JSONNode GetMatchingState(JSONNode visSpecs)
        {
            foreach (JSONNode stateSpecs in statesSpecification.Children)
            {
                if (CheckSpecsMatching(visSpecs, stateSpecs))
                {
                    Debug.Log("Visualisation matching state\"" + stateSpecs["name"] + "\" found.");
                    return stateSpecs;
                }
            }

            return null;
        }

        private JSONNode GetStateFromName(string name)
        {
            foreach (JSONNode stateSpecs in statesSpecification.Children)
            {
                if (stateSpecs["name"] == name)
                    return stateSpecs;
            }
            return null;
        }

        private Dictionary<string, JSONNode> GetDeepChildrenWithPaths(JSONNode jsonNode)
        {
            List<string> path = new List<string>();
            Dictionary<string, JSONNode> dictionary = new Dictionary<string, JSONNode>();

            return GetDeepChildrenWithPaths(jsonNode, ref path, ref dictionary);
        }

        private Dictionary<string, JSONNode> GetDeepChildrenWithPaths(JSONNode jsonNode, ref List<string> path, ref Dictionary<string, JSONNode> dictionary)
        {
            if (jsonNode.Children.Count() == 0 || jsonNode.IsArray)
            {
                dictionary.Add(string.Join(".", path), jsonNode);
                return dictionary;
            }

            foreach (string key in jsonNode.Keys)
            {
                path.Add(key);
                GetDeepChildrenWithPaths(jsonNode[key], ref path, ref dictionary);
                path.RemoveAt(path.Count() - 1);
            }

            return dictionary;
        }

        private int KeyCount(JSONNode jsonNode)
        {
            int count = 0;
            foreach (var key in jsonNode.Keys)
            {
                count++;
            }
            return count;
        }

        private bool CheckSpecsMatching(JSONNode visSpecs, JSONNode stateSpecs)
        {
            return CheckViewSpecsMatching(visSpecs, stateSpecs) &&
                   CheckEncodingSpecsMatching(visSpecs["encoding"], stateSpecs["encoding"]);
        }

        private bool CheckViewSpecsMatching(JSONNode visSpecs, JSONNode stateSpecs)
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
        /// Returns a list of all Transition specifications that have the given state as its starting state
        /// </summary>
        private List<Tuple<JSONNode, bool>> GetCandidateTransitionsFromState(JSONNode stateSpecification)
        {
            string stateName = stateSpecification["name"].ToString();
            List<Tuple<JSONNode, bool>> candidateTransitions = new List<Tuple<JSONNode, bool>>();

            foreach (JSONNode transition in transitionsSpecification.Children)
            {
                // Get the tuple of states
                JSONArray states = transition["states"].AsArray;

                // Add this transition to our list if the first name in the states array matches the input state
                if (states[0].ToString() == stateName)
                {
                    candidateTransitions.Add(new Tuple<JSONNode, bool>(transition, false));
                }
                // We can also add it if the second name matches the input AND the transition is set to bidirectional
                else if (states[1].ToString() == stateName && transition["bidirectional"])
                {
                    candidateTransitions.Add(new Tuple<JSONNode, bool>(transition, true));
                }
            }

            return candidateTransitions;
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

        /// <summary>
        /// Clean up a vis spec such that it may render properly during a morph
        /// </summary>
        public void CleanVisSpec(ref JObject specs, JObject stateSpecs)
        {
            foreach (string dim in new string[] { "x", "y", "z" })
            {
                string offset = dim + "offset";
                string offsetpct = dim + "offsetpct";

                // If there is no base spatial encoding given, remove its associated offset encodings (if any)
                if (specs["encoding"][dim] == null)
                {
                    specs["encoding"][offset]?.Parent.Remove();
                    specs["encoding"][offsetpct]?.Parent.Remove();
                }
            }

            // Check to see if any defined encoding now has BOTH a field and value defined within in
            foreach (var kvp in ((JObject)specs["encoding"]).Properties())
            {
                var encoding = specs["encoding"][kvp.Name];

                if (encoding["field"] != null && encoding["value"] != null)
                {
                    // If it does, then we keep the one which is defined in the state specs
                    if (stateSpecs["base"]["encoding"][kvp.Name] != null)
                    {
                        if (stateSpecs["base"]["encoding"][kvp.Name]["field"] != null)
                        {
                            encoding["value"].Parent.Remove();
                        }
                        else if (stateSpecs["base"]["encoding"][kvp.Name]["value"] != null)
                        {
                            encoding["field"].Parent.Remove();
                        }
                    }
                    else if (stateSpecs["override"]["encoding"][kvp.Name] != null)
                    {
                        if (stateSpecs["override"]["encoding"][kvp.Name]["field"] != null)
                        {
                            encoding["value"].Parent.Remove();
                        }
                        else if (stateSpecs["override"]["encoding"][kvp.Name]["value"] != null)
                        {
                            encoding["field"].Parent.Remove();
                        }
                    }
                }
            }

            // Make sure that all of the offset encodings are at the end
            ((JObject)specs.GetValue("encoding")).Properties().OrderBy(p => p.Name.Contains("offset"));
        }

        private IObservable<float> CreateMorphTweenObservable(JSONNode transitionSpecs)
        {
            JSONNode timingSpecs = transitionSpecs["timing"];

            // If no timing specs are given, we just use a generic timer
            if (timingSpecs == null)
            {
                return CreateTimerObservable(transitionSpecs, 1);
            }
            else
            {
                // Otherwise, if the name of the control corresponds with a parameter, use that instead
                string timerName = timingSpecs["control"];
                if (timerName != null && SignalManager.Instance.GetObservable(timerName) != null)
                {
                    return SignalManager.Instance.GetObservable(timerName).Select(_ => (float)_);
                }
                // Otherwise, use the time provided in the specification
                else
                {
                    return CreateTimerObservable(transitionSpecs, timingSpecs["control"]);
                }
            }
        }

        private IObservable<float> CreateTimerObservable(JSONNode transitionSpecs, float duration)
        {
            float startTime = Time.time;

            var cancellationObservable = Observable.Timer(TimeSpan.FromSeconds(duration));
            var timerObservable = Observable.EveryUpdate().Select(_ =>
            {
                float timer = Time.time - startTime;
                return Mathf.Clamp(timer / duration, 0, 1);
            })
                .TakeUntil(cancellationObservable);

            cancellationObservable.Subscribe(_ => DeactivateTransition(transitionSpecs, true));

            return timerObservable;
        }
    }
}