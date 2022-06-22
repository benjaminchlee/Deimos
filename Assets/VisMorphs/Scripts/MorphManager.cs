using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using SimpleJSON;
using UnityEditor;
using UnityEngine;
using Newtonsoft.Json.Linq;
using UniRx;

namespace DxR.VisMorphs
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

        public TextAsset geoJson;

        [TextArea(5, 100)]
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
            // Otherwise, if this Vis now no longer meets any state as it was morphing (i.e., Vis specification changed during Morph), we disable the Morph applied to it
            else if (isTransitionActive && vis == candidateVis)
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
            var triggerNames = transitionSpecs["triggers"];

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

            // Subscribe to the rest of the Predicates
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
                initialState = GenerateNewVisSpecFromState(candidateVis.GetVisSpecs(), currentState);
                finalState = GenerateNewVisSpecFromState(initialState, GetStateFromName(currentTransition["states"][1]), true);
            }
            else
            {
                finalState = GenerateNewVisSpecFromState(candidateVis.GetVisSpecs(), currentState, true);
                initialState = GenerateNewVisSpecFromState(finalState, GetStateFromName(currentTransition["states"][0]), true);

                var test = JSONNode.Parse(initialState.ToString());
                test.Remove("data");
                Debug.Log("initial " + test.ToString());
                var test2 = JSONNode.Parse(finalState.ToString());
                test2.Remove("data");
                Debug.Log("final " + test2.ToString());
            }

            // Change to initial state instantly
            candidateVis.UpdateVisSpecsFromJSONNode(initialState, false);

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
                bool matching = false;

                JSONNode transformBase = stateSpecs["base"];
                JSONNode transformExcludes = stateSpecs["excludes"];

                if (transformBase != null)
                {
                    matching = CompareBase(visSpecs, transformBase);
                    Debug.Log("MORPHS BASE FOR " + stateSpecs["name"] + ": " + matching);
                }

                if (transformExcludes != null && matching)
                {
                    matching = CompareExcludes(visSpecs, transformExcludes);
                    Debug.Log("MORPHS EXCLUDES FOR " + stateSpecs["name"] + ": " + matching);
                }

                if (matching)
                {
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

        private bool CompareBase(JSONNode visSpecs, JSONNode transformBase)
        {
            var visSpecsChildren = GetDeepChildrenWithPaths(visSpecs);
            var transformBaseChildren = GetDeepChildrenWithPaths(transformBase);

            foreach (var kvp in transformBaseChildren)
            {
                if (visSpecsChildren.ContainsKey(kvp.Key) && visSpecsChildren[kvp.Key].ToString() == kvp.Value.ToString())
                {
                    continue;
                }

                return false;
            }

            return true;
        }

        private bool CompareExcludes(JSONNode visSpecs, JSONNode transformExcludes)
        {
            var visSpecsChildren = GetDeepChildrenWithPaths(visSpecs);
            var transformExcludesChildren = GetDeepChildrenWithPaths(transformExcludes);

            foreach (var kvp in transformExcludesChildren)
            {
                // If the two Specs have the same key, and if either their values are the same or the value in
                // the Excludes specs is a wildcard (*), then it fails the Excludes check
                if (visSpecsChildren.ContainsKey(kvp.Key)
                    && (visSpecsChildren[kvp.Key] == kvp.Value
                        || kvp.Value == "*"))
                {
                    return false;
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

        private JSONNode GenerateNewVisSpecFromState(JSONNode visSpecs, JSONNode stateSpecs, bool includeBase = false)
        {
            // SimpleJSON doesn't really work well with merging JSON objects together
            // So we just use JSON.NET instead
            var _visSpecs = Newtonsoft.Json.Linq.JObject.Parse(visSpecs.ToString());
            var _stateSpecs = Newtonsoft.Json.Linq.JObject.Parse(stateSpecs.ToString());

            _visSpecs.Merge(_stateSpecs["override"]);
            if (includeBase)
            {
                _visSpecs.Merge(_stateSpecs["base"], new JsonMergeSettings { MergeArrayHandling  = MergeArrayHandling.Replace });
            }

            // Remove properties from the visSpecs that are defined in the excludes part of the stateSpecs
            if (_stateSpecs["excludes"] != null)
            {
                foreach (var i in ((JObject)_stateSpecs["excludes"]["encoding"]).Properties())
                {
                    _visSpecs["encoding"][i.Name]?.Parent.Remove();
                }
            }

            return JSONObject.Parse(Newtonsoft.Json.JsonConvert.SerializeObject(_visSpecs));
        }

        private IObservable<float> CreateMorphTweenObservable(JSONNode transitionSpecs)
        {
            JSONNode timingSpecs = transitionSpecs["timing"];

            // If no timing specs are given, we just use a generic timer
            if (timingSpecs == null)
            {
                return CreateTimerObservable(1);
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
                    return CreateTimerObservable(timingSpecs["control"]);
                }
            }
        }

        private IObservable<float> CreateTimerObservable(float duration)
        {
            float startTime = Time.time;

            var cancellationObservable = Observable.Timer(TimeSpan.FromSeconds(duration));
            var timerObservable = Observable.EveryUpdate().Select(_ =>
            {
                float timer = Time.time - startTime;
                return Mathf.Clamp(timer / duration, 0, 1);
            })
                .TakeUntil(cancellationObservable);

            return timerObservable;
        }
    }
}
