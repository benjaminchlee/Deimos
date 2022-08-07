using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using SimpleJSON;
using System.IO;
using System;
using System.Linq;
using UnityEngine.Events;
using UniRx;
using GeoJSON.Net.Geometry;
using Newtonsoft.Json;
using GeoJSON.Net.Feature;
using GeoJSON.Net;
using DxR.VisMorphs;
using static DxR.VisMorphs.EasingFunction;

namespace DxR
{
    /// <summary>
    /// This component can be attached to any GameObject (_parentObject) to create a
    /// data visualization. This component takes a JSON specification as input, and
    /// generates an interactive visualization as output. The JSON specification
    /// describes the visualization using ONE type of mark and one or more channels.
    /// </summary>
    public class Vis : MonoBehaviour
    {
        public string visSpecsURL = "example.json";                     // URL of vis specs; relative to specsRootPath directory.
        public bool enableGUI = true;                                   // Switch for in-situ GUI editor.
        public bool enableSpecsExpansion = false;                       // Switch for automatically replacing the vis specs text file on disk with inferrence result.
        public bool enableTooltip = true;                               // Switch for tooltip that shows datum attributes on-hover of mark instance.
        public bool verbose = true;                                     // Switch for verbose log.

        public static readonly string UNDEFINED = "undefined";                   // Value used for undefined objects in the JSON vis specs.
        public static readonly float SIZE_UNIT_SCALE_FACTOR = 1.0f / 1000.0f;    // Conversion factor to convert each Unity unit to 1 meter.
        public static readonly float DEFAULT_VIS_DIMS = 500.0f;                  // Default dimensions of a visualization, if not specified.

        private Parser parser = null;                                           // Parser of JSON specs and data in text format to JSONNode object specs.
        private GUI gui = null;                                                 // GUI object (class) attached to GUI game object.
        private GameObject tooltip = null;                                      // Tooltip game object for displaying datum info, e.g., on-hover.

        // Vis Properties:
        public Data data;                                                       // Object containing data.
        private string title;                                                   // Title of vis displayed.
        private float width;                                                    // Width of scene in millimeters.
        private float height;                                                   // Heigh of scene in millimeters.
        private float depth;                                                    // Depth of scene in millimeters.
        private string markType;                                                // Type or name of mark used in vis.
        private bool IsLinked = false;                                          // Link to other vis object for interaction.
        private string data_name = null;

        private JSONNode visSpecs;                                              // The original vis specs as provided by the user
        private JSONNode visSpecsExpanded;                                      // An expanded version of the vis specs with the data stored in-line. This is a separate variable as the in-line data can be very heavy
        private JSONNode visSpecsInferred;                                      // This is the inferred vis specs and is ultimately used for construction.

        private GameObject parentObject = null;                         // Parent game object for all generated objects associated to vis.
        private GameObject viewParentObject = null;                     // Parent game object for all view related objects - axis, legend, marks.
        private GameObject marksParentObject = null;                    // Parent game object for all mark instances.
        private GameObject guidesParentObject = null;                   // Parent game object for all guies (axes/legends) instances.
        private GameObject interactionsParentObject = null;             // Parent game object for all interactions, e.g., filters.
        private GameObject centreOffsetObject = null;
        private BoxCollider boxCollider;
        private new Rigidbody rigidbody;
        private GameObject markPrefab = null;                           // Prefab game object for instantiating marks.

        private List<ChannelEncoding> channelEncodings = null;          // List of channel encodings.
        private List<string> marksList;                                 // List of mark prefabs that can be used at runtime.
        private List<string> dataList;                                  // List of local data that can be used at runtime.
        public List<GameObject> markInstances;                          // List of mark instances; each mark instance corresponds to a datum.
        private Dictionary<string, Axis> axisInstances = new Dictionary<string, Axis>();
        private Dictionary<string, List<Axis>> facetedAxisInstances = new Dictionary<string, List<Axis>>();
        private Dictionary<string, ActiveTransition> activeTransitions = new Dictionary<string, ActiveTransition>();
        private Tuple<string, Vector3, Vector3> posePositionChanges;
        private Tuple<string, Quaternion, Quaternion> poseRotationChanges;
        private IDisposable posePositionDisposable;
        private IDisposable poseRotationDisposable;

        [Serializable]
        public class VisUpdatedEvent : UnityEvent<Vis, JSONNode> { }
        [HideInInspector]
        public VisUpdatedEvent VisUpdated;
        [HideInInspector]
        public VisUpdatedEvent VisUpdatedExpanded;
        [HideInInspector]
        public VisUpdatedEvent VisUpdatedInferred;

        private bool isReady = false;
        public bool IsReady { get { return isReady; } }

        private void Awake()
        {
            if (VisUpdated == null)
            {
                VisUpdated = new VisUpdatedEvent();
                VisUpdatedExpanded = new VisUpdatedEvent();
                VisUpdatedInferred = new VisUpdatedEvent();
            }

            // Initialize objects:
            parentObject = gameObject;
            viewParentObject = gameObject.transform.Find("DxRView").gameObject;
            marksParentObject = viewParentObject.transform.Find("DxRMarks").gameObject;
            guidesParentObject = viewParentObject.transform.Find("DxRGuides").gameObject;
            interactionsParentObject = gameObject.transform.Find("DxRInteractions").gameObject;

            boxCollider = gameObject.GetComponent<BoxCollider>() != null ? gameObject.GetComponent<BoxCollider>() : gameObject.AddComponent<BoxCollider>();
            rigidbody = gameObject.GetComponent<Rigidbody>() != null ? gameObject.GetComponent<Rigidbody>() : gameObject.AddComponent<Rigidbody>();
            boxCollider.isTrigger = false;
            rigidbody.isKinematic = true;

            if (viewParentObject == null || marksParentObject == null)
            {
                throw new Exception("Unable to load DxRView and/or DxRMarks objects.");
            }

            parser = new Parser();

            // If there is a RuntimeInspectorVisSpecs component attached to this GameObject, then we parse the specifications contained in it instead
            RuntimeInspectorVisSpecs runtimeSpecs = GetComponent<RuntimeInspectorVisSpecs>();
            if (runtimeSpecs != null)
            {
                string visSpecsString = runtimeSpecs.JSONSpecification != null ? runtimeSpecs.JSONSpecification.text : runtimeSpecs.InputSpecification;
                parser.ParseString(visSpecsString, out visSpecs);
            }
            // If not, parse the vis specs URL into the vis specs object.
            else
            {
                parser.Parse(visSpecsURL, out visSpecs);
            }

            InitDataList();
            InitMarksList();

            // Initialize the GUI based on the initial vis specs.
            InitGUI();
            InitTooltip();

            // Update vis based on the vis specs.
            UpdateVis();

            isReady = true;
        }

        private void Update()
        {
            // If there is an active transition, we constantly update the collider such that it matches the Marks, etc.
            if (activeTransitions.Count > 0)
                UpdateCollider();
        }

        /// <summary>
        /// Update the visualization based on the current visSpecs object (whether updated from GUI or text editor).
        /// </summary>
        private void UpdateVis(bool callUpdateEvent = true)
        {
            DeleteInteractions();                                   // Delete old GameObjects that we cannot update
            DeleteLegends();                                        // No longer deletes marks and axes

            UpdateMarkPrefab(visSpecs);                             // Update the mark prefab in case it has changed
            UpdateVisConfig(ref visSpecs);                          // Update the vis spec to include width, height, and depth values
            ExpandVisSpecs(visSpecs, out visSpecsExpanded);         // Expand the vis spec to include in-line data values
            UpdateVisData(ref visSpecsExpanded);                    // Create our data object based on the in-line data values

            InferVisSpecs(visSpecsExpanded, out visSpecsInferred);  // Infer the rest of the vis properties

            ConstructVis(visSpecsInferred);                         // Construct the vis based on the inferred spec
            UpdateVisPose(visSpecsInferred);                        // Update the vis pose based on the inferred spec (position, rotation)
            UpdateCollider();                                       // Update the collider on this vis

            if (callUpdateEvent)                                    // Call update events to listeners
            {
                VisUpdated.Invoke(this, visSpecs);
                VisUpdatedExpanded.Invoke(this, visSpecsExpanded);
                VisUpdatedInferred.Invoke(this, visSpecsInferred);
            }
        }

        private void ConstructVis(JSONNode specs)
        {
            CreateChannelEncodingObjects(specs, ref channelEncodings);  // Create the ChannelEncoding objects that marks use to update themselves
            ConstructAndUpdateMarkInstances();                          // Construct new marks if they don't exist, or update existing ones
            ApplyChannelEncodings();                                    // Apply the channel encodings to these marks
            ConstructInteractions(specs);                               // Construct DxR interactions before axes and legends
            ConstructAndUpdateAxes(specs, ref channelEncodings);        // Construct new axes if they don't exist, or update existing ones
            ConstructLegends(specs);                                    // Construct new legends
        }

        private void InitTooltip()
        {
            GameObject tooltipPrefab = Resources.Load("Tooltip/Tooltip") as GameObject;
            if(tooltipPrefab != null)
            {
                tooltip = Instantiate(tooltipPrefab, parentObject.transform);
                tooltip.SetActive(false);
            }
        }

        public JSONNode GetVisSpecs()
        {
            return visSpecs;
        }

        public JSONNode GetVisSpecsExpanded()
        {
            return visSpecsExpanded;
        }

        public JSONNode GetVisSpecsInferred()
        {
            return visSpecsInferred;
        }

        public int GetNumMarkInstances()
        {
            return markInstances.Count;
        }

        public bool GetIsLinked()
        {
            return IsLinked;
        }

        #region Morph specific functions

        public bool ApplyTransition(string transitionName, JSONNode newInitialVisSpecs, JSONNode newFinalVisSpecs, Func<IObservable<float>> tweeningObservableCreateFunc, Function easingFunction, Dictionary<string, Tuple<float, float>> stages)
        {
            if (activeTransitions.Keys.Contains(transitionName))
            {
                throw new Exception(string.Format("Vis Morphs: The transition {0} is currently active, therefore cannot be applied again.", transitionName));
            }

            // We need to determine the channel encodings which have changed between the initial and final states. These will be used to apply the transition
            // only to these specific channel encodings, (hopefully) allowing for simultaneous transitions to be applied
            List<ChannelEncoding> initialChannelEncodings = new List<ChannelEncoding>();
            JSONNode newInferredInitialVisSpecs;
            InferVisSpecs(newInitialVisSpecs, out newInferredInitialVisSpecs);
            CreateChannelEncodingObjects(newInferredInitialVisSpecs, ref initialChannelEncodings);

            List<ChannelEncoding> finalChannelEncodings = new List<ChannelEncoding>();
            JSONNode newInferredFinalVisSpecs;
            InferVisSpecs(newFinalVisSpecs, out newInferredFinalVisSpecs);
            CreateChannelEncodingObjects(newInferredFinalVisSpecs, ref finalChannelEncodings);

            // For each of the two lists, keep only the channel encodings which have changed between them
            // We also keep all relevant spatial channels if any one of them has changed (e.g., keep x in xoffset is changed)
            ChannelEncoding.FilterUnchangedChannelEncodings(ref initialChannelEncodings, ref finalChannelEncodings, true);

            // Now make a list containing pairwise ChannelEncodings in tuples, using nulls wherever a channel encoding exists in one but not the other
            // The third item in the tupple is the name of the channel (non-null)
            List<Tuple<ChannelEncoding, ChannelEncoding, string>> transitionChannelEncodings = new List<Tuple<ChannelEncoding, ChannelEncoding,string>>();
            while (initialChannelEncodings.Count > 0)
            {
                ChannelEncoding ce1 = initialChannelEncodings[0];
                string channelName = ce1.channel;

                bool found = false;
                for (int i = 0; i < finalChannelEncodings.Count; i++)
                {
                    ChannelEncoding ce2 = finalChannelEncodings[i];
                    if (ce1.channel == ce2.channel)
                    {
                        transitionChannelEncodings.Add(new Tuple<ChannelEncoding, ChannelEncoding, string>(ce1, ce2, channelName));
                        finalChannelEncodings.RemoveAt(i);
                        found = true;
                        break;
                    }
                }

                if (!found)
                    transitionChannelEncodings.Add(new Tuple<ChannelEncoding, ChannelEncoding, string>(ce1, null, channelName));

                initialChannelEncodings.RemoveAt(0);
            }

            while (finalChannelEncodings.Count > 0)
            {
                ChannelEncoding ce2 = finalChannelEncodings[0];
                string channelName = ce2.channel;
                transitionChannelEncodings.Add(new Tuple<ChannelEncoding, ChannelEncoding, string>(null, ce2, channelName));
                finalChannelEncodings.RemoveAt(0);
            }

            // Check to see if this transition can be applied. This is only the case when there are no other active transitions which
            // are affecting the same channel encodings as this new one
            bool isNewTransitionAllowed = true;
            List<string> changedChannelsNames = transitionChannelEncodings.Where(tuple => tuple.Item1 != null).Select(tuple => tuple.Item1.channel)
                                                .Union(transitionChannelEncodings.Where(tuple => tuple.Item2 != null).Select(tuple => tuple.Item2.channel))
                                                .ToList();

            foreach (var activeTransition in activeTransitions.Values)
            {
                // Check if channel names overlap in the either of the two states
                bool initialStateOverlap = activeTransition.ChangedChannelEncodings.Where(tuple => tuple.Item1 != null).Select(tuple => tuple.Item1.channel).Any(ce1 => changedChannelsNames.Contains(ce1));
                bool finalStateOverlap = activeTransition.ChangedChannelEncodings.Where(tuple => tuple.Item2 != null).Select(tuple => tuple.Item2.channel).Any(ce2 => changedChannelsNames.Contains(ce2));

                if (initialStateOverlap || finalStateOverlap)
                {
                    isNewTransitionAllowed = false;
                    break;
                }
            }

            // We also need to check whether this new visualisation updates the visualisation's pose. If there is another
            // transition which is already updating the pose, then this new transition cannot continue
            // Position and rotation changes can coexist, but a single transition may affect both at the same time
            if (isNewTransitionAllowed)
            {
                if (posePositionDisposable != null)
                {
                    if (newInferredInitialVisSpecs["position"] != null || newInferredFinalVisSpecs["position"] != null)
                        isNewTransitionAllowed = false;
                }
                if (poseRotationDisposable != null)
                {
                    if (newInferredInitialVisSpecs["rotation"] != null || newInferredFinalVisSpecs["rotation"] != null)
                        isNewTransitionAllowed = false;
                }
            }

            // If the new transition has passed our checks, we can finally pass this onto the marks/axes/etc.
            if (isNewTransitionAllowed)
            {
                // Only if this transition is allowed to we then create the tweening observable using the Func that was passed as a parameter
                IObservable<float> tweeningObservable = tweeningObservableCreateFunc();

                ActiveTransition newActiveTransition = new ActiveTransition()
                {
                    Name = transitionName,
                    ChangedChannelEncodings = transitionChannelEncodings,
                    InitialVisSpecs = newInitialVisSpecs,
                    FinalVisSpecs = newFinalVisSpecs,
                    InitialInferredVisSpecs = newInferredInitialVisSpecs,
                    FinalInferredVisSpecs = newInferredFinalVisSpecs,
                    TweeningObservable = tweeningObservable,
                    EasingFunction = easingFunction,
                    Stages = stages
                };
                activeTransitions.Add(transitionName, newActiveTransition);

                ApplyTransitionChannelEncodings(newActiveTransition);
                ApplyTransitionAxes(newActiveTransition);
                ApplyTransitionPose(newActiveTransition);

                return true;
            }
            else
            {
                Debug.LogWarning("Vis Morphs: A transition has failed to been applied as it affects a visualisation property which is currently undergoing a transition.");
                return false;
            }
        }

        public void StopTransition(string transitionName, bool goToEnd = true, bool commitVisSpecChanges = true, bool callUpdateEvent = false)
        {
            if (!activeTransitions.ContainsKey(transitionName))
            {
                throw new Exception(string.Format("Vis Morphs: The transition {0} is not active, and therefore cannot be stopped.", transitionName));
            }

            StopTransitionChannelEncodings(transitionName, goToEnd, commitVisSpecChanges);
            StopTransitionAxes(transitionName, goToEnd);
            StopTransitionPose(transitionName, goToEnd);

            UpdateCollider();

            activeTransitions.Remove(transitionName);

            if (callUpdateEvent)
                VisUpdated.Invoke(this, visSpecs);
        }

        private void ApplyTransitionChannelEncodings(ActiveTransition activeTransition)
        {
            // TODO: We need to do something about the rotation changing, not sure how to fix it
            for (int i = 0; i < markInstances.Count; i++)
            {
                markInstances[i].GetComponent<Mark>().InitialiseTransition(activeTransition, i);
            }
        }

        private void StopTransitionChannelEncodings(string transitionName, bool goToEnd, bool commitVisSpecChanges)
        {
            for (int i = 0; i < markInstances.Count; i++)
            {
                markInstances[i].GetComponent<Mark>().StopTransition(transitionName, goToEnd);
            }

            ActiveTransition stoppingTransition = activeTransitions[transitionName];

            // If true, we update the vis specs to essentially "keep" the changes caused by the transition
            // This can be set to false in order to have the transition basically be ephemeral
            if (commitVisSpecChanges)
            {
                List<Tuple<ChannelEncoding, ChannelEncoding, string>> changedChannelEncodings = stoppingTransition.ChangedChannelEncodings;

                // Update the vis specs
                // For each channel encoding change, change the vis specs based on the one stored in the transition tuple
                JSONNode stateVisSpecs = (goToEnd) ? stoppingTransition.FinalVisSpecs : stoppingTransition.InitialVisSpecs;

                foreach (var tuple in changedChannelEncodings)
                {
                    string channelName = (tuple.Item1 != null) ? tuple.Item1.channel : tuple.Item2.channel;
                    ChannelEncoding ceToCheck = goToEnd ? tuple.Item2 : tuple.Item1;

                    // If the target state has a CE (i.e., it exists), copy it from the stateVisSpecs
                    if (ceToCheck != null)
                    {
                        visSpecs["encoding"][channelName] = stateVisSpecs["encoding"][channelName];
                    }
                    // Otherwise, it no longer exists and thus we remove it
                    else
                    {
                        visSpecs["encoding"].Remove(channelName);
                    }
                }

                // Make sure that the offsetpcts are at the very end to prevent issues where they get misordered
                // This code is kinda messy but oh well, can't figure out how else to do it with SimpleJSON
                var xoffsetpct = visSpecs["encoding"]["xoffsetpct"];
                var yoffsetpct = visSpecs["encoding"]["yoffsetpct"];
                var zoffsetpct = visSpecs["encoding"]["zoffsetpct"];
                if (xoffsetpct != null) visSpecs["encoding"].Remove("xoffsetpct");
                if (yoffsetpct != null) visSpecs["encoding"].Remove("yoffsetpct");
                if (zoffsetpct != null) visSpecs["encoding"].Remove("zoffsetpct");
                visSpecs = visSpecs.Clone();
                if (xoffsetpct != null) visSpecs["encoding"].Add("xoffsetpct", xoffsetpct);
                if (yoffsetpct != null) visSpecs["encoding"].Add("yoffsetpct", yoffsetpct);
                if (zoffsetpct != null) visSpecs["encoding"].Add("zoffsetpct", zoffsetpct);

                // Copy over view-level properties. As there's not that many of these, we can be lazy and hard code these in
                // Only for the properties that have changed between initial and final state do we copy
                JSONNode initialSpec = stoppingTransition.InitialInferredVisSpecs;
                JSONNode finalSpec = stoppingTransition.FinalInferredVisSpecs;
                foreach (string property in new string[] { "width", "height", "depth" } )
                {
                    if (initialSpec[property].Value != finalSpec[property].Value && stateVisSpecs[property] != null && !stateVisSpecs[property].IsNull)
                    {
                        if (visSpecs[property] == null)
                        {
                            visSpecs.Add(property, stateVisSpecs[property].Value);
                        }
                        else
                        {
                            visSpecs[property] = stateVisSpecs[property].Value;
                        }
                    }
                }

                // Update our expanded specs
                visSpecsExpanded = visSpecs.Clone();
                visSpecsExpanded["data"] = visSpecsInferred["data"];

                // Call the update function for the updated specs. This is a bit finnicky as the Morphable script relies on VisUpdatedExpanded and not this one
                // While each function looks pretty much identical they are used for different purposes altogether. Code smell I know
                VisUpdated.Invoke(this, visSpecs);
            }
        }

        private void ApplyTransitionAxes(ActiveTransition activeTransition)
        {
            // If there is a facetwrap channel, we will actually need to create more axes
            // This value will be null if there is no facetwrap channel
            var facetWrapEncodingChange = activeTransition.ChangedChannelEncodings.SingleOrDefault(tuple => tuple.Item3 == "facetwrap");

            // For each changed channel encoding, go through and update its axis (if applicable)
            foreach (var encodingChange in activeTransition.ChangedChannelEncodings)
            {
                string channel = encodingChange.Item3;

                // Only certain channel encodings can have axes. Skip this encoding if it is not one of these
                if (channel != "x" && channel != "y" && channel != "z")
                    continue;

                // Get the associated axis specs and scales for both the start and end states
                JSONNode initialAxisSpecs = activeTransition.InitialInferredVisSpecs["encoding"][channel]["axis"];
                JSONNode finalAxisSpecs = activeTransition.FinalInferredVisSpecs["encoding"][channel]["axis"];
                Scale initialScale = encodingChange.Item1 != null ? encodingChange.Item1.scale : null;
                Scale finalScale = encodingChange.Item2 != null ? encodingChange.Item2.scale : null;

                // We need at least one of these to be defined so that we can activate the transition
                if (initialAxisSpecs != null || finalAxisSpecs != null)
                {
                    // Get the axis for this object. We will need to pass it a channel encoding object but the values will get overridden later on anyway
                    Axis axis = null;
                    ChannelEncoding dummyCE = encodingChange.Item1 != null ? encodingChange.Item1 : encodingChange.Item2;
                    JSONNode dummyAxisSpec = initialAxisSpecs != null ? initialAxisSpecs : finalAxisSpecs;
                    ConstructAndUpdateAxisObject(channel, dummyAxisSpec, ref dummyCE, out axis);

                    // Call to initialise transition on this single axis
                    axis.InitialiseTransition(activeTransition, channel, initialAxisSpecs, finalAxisSpecs, initialScale, finalScale);

                    // Now check for facetwrap
                    if (facetWrapEncodingChange != null)
                    {
                        // We need to calculate the translations twice for both the initial and final states
                        FacetWrapChannelEncoding initialFacetWrapCE = null;
                        int initialnumFacets = 0;
                        int initialFacetSize = 1;
                        float initialDeltaFirstDir = 0;
                        float initialDeltaSecondDir = 0;
                        if (facetWrapEncodingChange.Item1 != null)
                        {
                            initialFacetWrapCE = (FacetWrapChannelEncoding)facetWrapEncodingChange.Item1;
                            initialnumFacets = initialFacetWrapCE.numFacets;
                            initialFacetSize = initialFacetWrapCE.size;
                            initialDeltaFirstDir = initialFacetWrapCE.spacing[0];
                            initialDeltaSecondDir = initialFacetWrapCE.spacing[1];
                        }

                        FacetWrapChannelEncoding finalFacetWrapCE = null;
                        int finalNumFacets = 0;
                        int finalFacetSize = 1;
                        float finalDeltaFirstDir = 0;
                        float finalDeltaSecondDir = 0;
                        if (facetWrapEncodingChange.Item2 != null)
                        {
                            finalFacetWrapCE = (FacetWrapChannelEncoding)facetWrapEncodingChange.Item2;
                            finalNumFacets = finalFacetWrapCE.numFacets;
                            finalFacetSize = finalFacetWrapCE.size;
                            finalDeltaFirstDir = finalFacetWrapCE.spacing[0];
                            finalDeltaSecondDir = finalFacetWrapCE.spacing[1];
                        }

                        // Create the axes for our facet
                        List<Axis> facetedAxes = new List<Axis>();
                        ConstructAndUpdateFacetedAxisObjects(channel, dummyAxisSpec, ref dummyCE, ref facetedAxes,
                                                             Mathf.Max(initialnumFacets, finalNumFacets) - 1);    // We need to minus 1 as we don't include the original axes themselves

                        List<Vector3> initialTranslations = Enumerable.Repeat(Vector3.zero, facetedAxes.Count).ToList();
                        List<Vector3> finalTranslations = Enumerable.Repeat(Vector3.zero, facetedAxes.Count).ToList();

                        // Calculate translations for initial
                        for (int facetIdx = 0; facetIdx < initialnumFacets - 1; facetIdx++)
                        {
                            Axis facetAxis = facetedAxes[facetIdx];

                            // Apply translation to these axes
                            int firstDir = initialFacetWrapCE.directions[0];
                            int secondDir = initialFacetWrapCE.directions[1];

                            int idxFirstDir = (facetIdx + 1) % initialFacetSize;
                            int idxSecondDir = Mathf.FloorToInt((facetIdx + 1) / (float)initialFacetSize);

                            Vector3 translation = initialTranslations[facetIdx];
                            translation[firstDir] = initialDeltaFirstDir * idxFirstDir;
                            translation[secondDir] = initialDeltaSecondDir * idxSecondDir;
                            initialTranslations[facetIdx] = translation;
                        }

                        // Calculate translations for final
                        for (int facetIdx = 0; facetIdx < finalNumFacets - 1; facetIdx++)
                        {
                            Axis facetAxis = facetedAxes[facetIdx];

                            // Apply translation to these axes
                            int firstDir = finalFacetWrapCE.directions[0];
                            int secondDir = finalFacetWrapCE.directions[1];

                            int idxFirstDir = (facetIdx + 1) % finalFacetSize;
                            int idxSecondDir = Mathf.FloorToInt((facetIdx + 1) / (float)finalFacetSize);

                            Vector3 translation = finalTranslations[facetIdx];
                            translation[firstDir] = finalDeltaFirstDir * idxFirstDir;
                            translation[secondDir] = finalDeltaSecondDir * idxSecondDir;
                            finalTranslations[facetIdx] = translation;
                        }

                        // Apply the transition
                        for (int i = 0; i < facetedAxes.Count; i++)
                        {
                            facetedAxes[i].InitialiseTransition(activeTransition, channel, initialAxisSpecs, finalAxisSpecs,
                                                                initialScale, finalScale,
                                                                initialTranslations[i], finalTranslations[i]);
                        }
                    }
                }
            }
        }

        private void StopTransitionAxes(string transitionName, bool goToEnd)
        {
            // Stop all regular axes, removing their references if they have been marked for deletion
            foreach (var kvp in axisInstances.ToList())
            {
                bool isDeleting = kvp.Value.StopTransition(transitionName, goToEnd, false);
                if (isDeleting)
                    axisInstances.Remove(kvp.Key);
            }

            // Stop all faceted axes, removing their references if they have been marked for deletion
            foreach (var kvp in facetedAxisInstances.ToList())
            {
                foreach (Axis facetAxis in kvp.Value.ToList())
                {
                    bool isDeleting = facetAxis.StopTransition(transitionName, goToEnd, true);
                    if (isDeleting)
                    {
                        kvp.Value.Remove(facetAxis);
                    }
                }

                // If the given channel no longer has any axes in its list, remove it from the dictionary
                if (kvp.Value.Count == 0)
                {
                    facetedAxisInstances.Remove(kvp.Key);
                }
            }
        }

        private void ApplyTransitionPose(ActiveTransition newActiveTransition)
        {
            ApplyTransitionPosition(newActiveTransition);
            ApplyTransitionRotation(newActiveTransition);

            // If any of the two had activated, we disable the box collider on this Vis so that things like MRTK can no longer move it
            if (posePositionChanges != null || poseRotationChanges != null)
            {
                boxCollider.enabled = false;
            }
        }

        private void ApplyTransitionPosition(ActiveTransition newActiveTransition)
        {
            // Get the initial and final values for the pose
            Vector3? initialPosition = null;
            Vector3? finalPosition = null;

            if (newActiveTransition.InitialInferredVisSpecs["position"] != null)
            {
                JSONNode positionSpecs = newActiveTransition.InitialInferredVisSpecs["position"];
                Vector3 position = transform.position;
                if (positionSpecs["value"] != null)
                {
                    position = new Vector3(positionSpecs["value"][0].AsFloat, positionSpecs["value"][1].AsFloat, positionSpecs["value"][2].AsFloat);
                }
                else
                {
                    if (positionSpecs["x"] != null) position.x = positionSpecs["x"].AsFloat;

                    if (positionSpecs["y"] != null) position.y = positionSpecs["y"].AsFloat;

                    if (positionSpecs["z"] != null) position.z = positionSpecs["z"].AsFloat;
                }
                initialPosition = position;
            }

            if (newActiveTransition.FinalInferredVisSpecs["position"] != null)
            {
                JSONNode positionSpecs = newActiveTransition.FinalInferredVisSpecs["position"];
                Vector3 position = transform.position;
                if (positionSpecs["value"] != null)
                {
                    position = new Vector3(positionSpecs["value"][0].AsFloat, positionSpecs["value"][1].AsFloat, positionSpecs["value"][2].AsFloat);
                }
                else
                {
                    if (positionSpecs["x"] != null) position.x = positionSpecs["x"].AsFloat;

                    if (positionSpecs["y"] != null) position.y = positionSpecs["y"].AsFloat;

                    if (positionSpecs["z"] != null) position.z = positionSpecs["z"].AsFloat;
                }
                finalPosition = position;
            }

            // We only apply a position transformation if the final position is defined
            if (finalPosition != null)
            {
                // If the initial position is not defined, use the current world position of this Vis
                if (initialPosition == null)
                    initialPosition = transform.position;

                // We need to rescale our tweening value from the observable based on any staging that is defined, if any
                // We access these values now and then use them in the observable later
                bool tweenRescaled = false;
                float minTween = 0;
                float maxTween = 1;
                if (newActiveTransition.Stages.TryGetValue("position", out Tuple<float, float> range))
                {
                    tweenRescaled = true;
                    minTween = range.Item1;
                    maxTween = range.Item2;
                }

                // Interpolate
                posePositionDisposable = newActiveTransition.TweeningObservable.Subscribe(t =>
                {
                    // Rescale the tween value if necessary
                    if (tweenRescaled)
                        t = Utils.NormaliseValue(t, minTween, maxTween, 0, 1);

                    // Apply easing if applicable
                    if (newActiveTransition.EasingFunction != null)
                    {
                        // Only do it if t is inside the accepted ranges, otherwise it returns NaN
                        if (0 <= t && t <= 1)
                            t = newActiveTransition.EasingFunction(0, 1, t);
                    }

                    transform.position = Vector3.Lerp(initialPosition.Value, finalPosition.Value, t);
                });

                // Save our start and end values
                posePositionChanges = new Tuple<string, Vector3, Vector3>(newActiveTransition.Name, initialPosition.Value, finalPosition.Value);
            }
        }

        private void ApplyTransitionRotation(ActiveTransition newActiveTransition)
        {
            // Get the initial and final rotation values for the pose
            Quaternion? initialRotation = null;
            Quaternion? finalRotation = null;

            if (newActiveTransition.InitialInferredVisSpecs["rotation"] != null)
            {
                JSONNode rotationSpecs = newActiveTransition.InitialInferredVisSpecs["rotation"];

                if (rotationSpecs["value"] != null)
                {
                    // Euler Angles
                    if (rotationSpecs["value"].Count == 3)
                    {
                        Vector3 eulerAngles = new Vector3(rotationSpecs["value"][0].AsFloat, rotationSpecs["value"][1].AsFloat, rotationSpecs["value"][2].AsFloat);
                        initialRotation = Quaternion.Euler(eulerAngles);
                    }
                    // Quaternion
                    else
                    {
                        Quaternion quaternion = new Quaternion(rotationSpecs["value"][0].AsFloat,rotationSpecs["value"][1].AsFloat, rotationSpecs["value"][2].AsFloat, rotationSpecs["value"][3].AsFloat);
                        initialRotation = quaternion;
                    }
                }
                else
                {
                    Vector3 eulerAngles = transform.eulerAngles;
                    if (rotationSpecs["x"] != null) eulerAngles.x = rotationSpecs["x"].AsFloat;
                    if (rotationSpecs["y"] != null) eulerAngles.y = rotationSpecs["y"].AsFloat;
                    if (rotationSpecs["z"] != null) eulerAngles.z = rotationSpecs["z"].AsFloat;
                    initialRotation = Quaternion.Euler(eulerAngles);
                }
            }

            if (newActiveTransition.FinalInferredVisSpecs["rotation"] != null)
            {
                JSONNode rotationSpecs = newActiveTransition.FinalInferredVisSpecs["rotation"];

                if (rotationSpecs["value"] != null)
                {
                    // Euler Angles
                    if (rotationSpecs["value"].Count == 3)
                    {
                        Vector3 eulerAngles = new Vector3(rotationSpecs["value"][0].AsFloat, rotationSpecs["value"][1].AsFloat, rotationSpecs["value"][2].AsFloat);
                        finalRotation = Quaternion.Euler(eulerAngles);
                    }
                    // Quaternion
                    else
                    {
                        Quaternion quaternion = new Quaternion(rotationSpecs["value"][0].AsFloat,rotationSpecs["value"][1].AsFloat, rotationSpecs["value"][2].AsFloat, rotationSpecs["value"][3].AsFloat);
                        finalRotation = quaternion;
                    }
                }
                else
                {
                    Vector3 eulerAngles = transform.eulerAngles;
                    if (rotationSpecs["x"] != null) eulerAngles.x = rotationSpecs["x"].AsFloat;
                    if (rotationSpecs["y"] != null) eulerAngles.y = rotationSpecs["y"].AsFloat;
                    if (rotationSpecs["z"] != null) eulerAngles.z = rotationSpecs["z"].AsFloat;
                    finalRotation = Quaternion.Euler(eulerAngles);
                }
            }

            // We only apply a rotation transformation if the final rotation is defined
            if (finalRotation != null)
            {
                // If the initial rotation is not defined, use the current world rotation of this Vis
                if (initialRotation == null)
                    initialRotation = transform.rotation;

                // We need to rescale our tweening value from the observable based on any staging that is defined, if any
                // We access these values now and then use them in the observable later
                bool tweenRescaled = false;
                float minTween = 0;
                float maxTween = 1;
                if (newActiveTransition.Stages.TryGetValue("rotation", out Tuple<float, float> range))
                {
                    tweenRescaled = true;
                    minTween = range.Item1;
                    maxTween = range.Item2;
                }

                // Interpolate
                poseRotationDisposable = newActiveTransition.TweeningObservable.Subscribe(t =>
                {
                    // Rescale the tween value if necessary
                    if (tweenRescaled)
                        t = Utils.NormaliseValue(t, minTween, maxTween, 0, 1);

                    // Apply easing if applicable
                    if (newActiveTransition.EasingFunction != null)
                    {
                        // Only do it if t is inside the accepted ranges, otherwise it returns NaN
                        if (0 <= t && t <= 1)
                            t = newActiveTransition.EasingFunction(0, 1, t);
                    }

                    transform.rotation = Quaternion.Lerp(initialRotation.Value, finalRotation.Value, t);
                });

                // Save our start and end values
                poseRotationChanges = new Tuple<string, Quaternion, Quaternion>(newActiveTransition.Name, initialRotation.Value, finalRotation.Value);
            }
        }


        private void StopTransitionPose(string transitionName, bool goToEnd)
        {
            if (posePositionChanges != null && posePositionChanges.Item1 == transitionName)
            {
                Vector3 position = goToEnd ? posePositionChanges.Item3 : posePositionChanges.Item2;
                transform.position = position;

                posePositionChanges = null;
                posePositionDisposable.Dispose();
                posePositionDisposable = null;
            }

            if (poseRotationChanges != null && poseRotationChanges.Item1 == transitionName)
            {
                Quaternion rotation = goToEnd ? poseRotationChanges.Item3 : poseRotationChanges.Item2;
                transform.rotation = rotation;

                poseRotationChanges = null;
                poseRotationDisposable.Dispose();
                poseRotationDisposable = null;
            }

            // If both position and rotation are now no longer transitioning, we re-enable the box collider
            if (posePositionChanges == null && poseRotationChanges == null)
            {
                boxCollider.enabled = true;
            }
        }

        #endregion Morph specific functions

        #region Data loading functions

        private void InitDataList()
        {
            string[] dirs = Directory.GetFiles(Application.dataPath + "/StreamingAssets/DxRData");
            dataList = new List<string>();
            dataList.Add(DxR.Vis.UNDEFINED);
            dataList.Add("inline");
            for (int i = 0; i < dirs.Length; i++)
            {
                if (Path.GetExtension(dirs[i]) != ".meta")
                {
                    dataList.Add(Path.GetFileName(dirs[i]));
                }
            }
        }

        public List<string> GetDataList()
        {
            return dataList;
        }

        private void UpdateVisData(ref JSONNode visSpecs)
        {
            bool dataChanged = false;

            // If we don't have a data object created, we always create one
            if (data == null)
            {
                dataChanged = true;
            }
            // If we have a data object already created, we do a check to make sure that we don't needlessly recreate it over and over if the data is the same
            else if (data != null)
            {
                // Inline data should be fairly easy to load. Always reload the data in this situation
                if (visSpecs["data"]["url"] == "inline")
                {
                    dataChanged = true;
                }
                // For data referenced by url, if the url has changed then we update the data
                else if (visSpecs["data"]["url"] != data.url)
                {
                    dataChanged = true;
                }
            }

            // Only update the vis data when it is either not yet defined, or if it has changed
            if (!dataChanged)
                return;

            JSONNode valuesSpecs = visSpecs["data"]["values"];
            data = new Data();
            data.url = visSpecs["data"]["url"];

            if (verbose) Debug.Log("Data update " + valuesSpecs.ToString());

            // Data gets parsed differently depending if its in a standard or geoJSON format
            if (!IsDataGeoJSON(valuesSpecs))
            {
                PopulateTabularData(valuesSpecs, ref data);
            }
            else
            {
                PopulateGeoJSONData(valuesSpecs, ref data);
            }
        }

        private bool IsDataGeoJSON(JSONNode valuesSpecs)
        {
            if (valuesSpecs["type"] != null)
                return valuesSpecs["type"] == "FeatureCollection";

            return false;
        }

        private void PopulateTabularData(JSONNode valuesSpecs, ref Data data)
        {
            CreateDataFields(valuesSpecs, ref data);

            data.values = new List<Dictionary<string, string>>();

            int numDataFields = data.fieldNames.Count;
            if (verbose) Debug.Log("Counted " + numDataFields.ToString() + " fields in data.");

            // Loop through the values in the specification
            // and insert one Dictionary entry in the values list for each.
            foreach (JSONNode value in valuesSpecs.Children)
            {
                Dictionary<string, string> d = new Dictionary<string, string>();

                bool valueHasNullField = false;
                for (int fieldIndex = 0; fieldIndex < numDataFields; fieldIndex++)
                {
                    string curFieldName = data.fieldNames[fieldIndex];

                    // TODO: Handle null / missing values properly.
                    if (value[curFieldName].IsNull)
                    {
                        valueHasNullField = true;
                        if (verbose) Debug.Log("value null found: ");
                        break;
                    }

                    d.Add(curFieldName, value[curFieldName]);
                }

                if (!valueHasNullField)
                {
                    data.values.Add(d);
                }
            }

            if (visSpecs["data"]["linked"] != null)
            {
                if (visSpecs["data"]["linked"] == "true")
                {
                    IsLinked = true;
                }
            }
            //            SubsampleData(valuesSpecs, 8, "Assets/DxR/Resources/cars_subsampled.json");
        }

        private void PopulateGeoJSONData(JSONNode valuesSpecs, ref Data data)
        {
            data.values = new List<Dictionary<string, string>>();
            data.polygons = new List<List<List<IPosition>>>();
            data.centres = new List<IPosition>();

            // We have to use geoJSON.NET here
            var featureCollection = JsonConvert.DeserializeObject<FeatureCollection>(valuesSpecs.ToString());

            // Create the data fields for the geoJSON feature properties (similar to CreateDataFields())
            data.fieldNames = new List<string>();
            // We also add two here for Longitude and Latitude
            data.fieldNames.Add("Longitude");
            data.fieldNames.Add("Latitude");
            foreach (var kvp in featureCollection.Features.First().Properties)
            {
                data.fieldNames.Add(kvp.Key);
            }

            // Populate our data structures with those from the geoJSON
            foreach (var feature in featureCollection.Features)
            {
                // Add values (i.e., properties)
                Dictionary<string, string> datum = new Dictionary<string, string>();
                foreach (var kvp in feature.Properties)
                {
                    datum.Add(kvp.Key, kvp.Value.ToString());
                }

                // Add polygons
                List<List<IPosition>> featurePolygons = new List<List<IPosition>>();
                switch (feature.Geometry.Type)
                {
                    case GeoJSONObjectType.Polygon:
                        {
                            var polygon = feature.Geometry as Polygon;
                            foreach (LineString lineString in polygon.Coordinates)
                            {
                                List<IPosition> positions = new List<IPosition>();

                                foreach (IPosition position in lineString.Coordinates)
                                {
                                    positions.Add(position);
                                }

                                // If the last position is the same as the first one, remove the last position
                                var firstPos = positions[0];
                                var lastPos = positions[positions.Count - 1];
                                if (firstPos.Latitude == lastPos.Latitude && firstPos.Longitude == lastPos.Longitude)
                                    positions.RemoveAt(positions.Count - 1);

                                featurePolygons.Add(positions);
                            }
                            break;
                        }

                    case GeoJSONObjectType.MultiPolygon:
                        {
                            MultiPolygon multiPolygon = feature.Geometry as MultiPolygon;
                            foreach (Polygon polygon in multiPolygon.Coordinates)
                            {
                                foreach (LineString lineString in polygon.Coordinates)
                                {
                                    List<IPosition> positions = new List<IPosition>();
                                    foreach (IPosition position in lineString.Coordinates)
                                    {
                                        positions.Add(position);
                                    }

                                    // If the last position is the same as the first one, remove the last position
                                    var firstPos = positions[0];
                                    var lastPos = positions[positions.Count - 1];
                                    if (firstPos.Latitude == lastPos.Latitude && firstPos.Longitude == lastPos.Longitude)
                                        positions.RemoveAt(positions.Count - 1);

                                    featurePolygons.Add(positions);
                                }
                            }
                            break;
                        }
                }

                data.polygons.Add(featurePolygons);

                // Find the centre position of these polygons as well
                // From https://stackoverflow.com/questions/6671183/calculate-the-center-point-of-multiple-latitude-longitude-coordinate-pairs
                var flattenedPositions = featurePolygons.SelectMany(x => x);
                double x = 0;
                double y = 0;
                double z = 0;

                foreach (var position in flattenedPositions)
                {
                    var latitude = position.Latitude * Math.PI / 180;
                    var longitude = position.Longitude * Math.PI / 180;

                    x += Math.Cos(latitude) * Math.Cos(longitude);
                    y += Math.Cos(latitude) * Math.Sin(longitude);
                    z += Math.Sin(latitude);
                }

                var total = flattenedPositions.Count();

                x = x / total;
                y = y / total;
                z = z / total;

                var centralLongitude = Math.Atan2(y, x);
                var centralSquareRoot = Math.Sqrt(x * x + y * y);
                var centralLatitude = Math.Atan2(z, centralSquareRoot);

                var centroid = new Position(centralLatitude * 180 / Math.PI, centralLongitude * 180 / Math.PI);
                data.centres.Add(centroid);

                // Create a new data object for the centre points to act as longitude and latitude quantitative/nominal values
                datum.Add("Latitude", centroid.Latitude.ToString());
                datum.Add("Longitude", centroid.Longitude.ToString());

                data.values.Add(datum);
            }
        }

        private void CreateDataFields(JSONNode valuesSpecs, ref Data data)
        {
            data.fieldNames = new List<string>();
            foreach (KeyValuePair<string, JSONNode> kvp in valuesSpecs[0].AsObject)
            {
                data.fieldNames.Add(kvp.Key);

                if (verbose) Debug.Log("Reading data field: " + kvp.Key);
            }
        }

        private void SubsampleData(JSONNode data, int samplingRate, string outputName)
        {
            JSONArray output = new JSONArray();
            int counter = 0;
            foreach (JSONNode value in data.Children)
            {
                if (counter % 8 == 0)
                {
                    output.Add(value);
                }
                counter++;
            }

            System.IO.File.WriteAllText(outputName, output.ToString());
        }

        public string GetDataName()
        {
            return data_name;
        }

        internal List<string> GetDataFieldsListFromURL(string dataURL)
        {
            return parser.GetDataFieldsList(dataURL);
        }

        internal List<string> GetDataFieldsListFromValues(JSONNode valuesSpecs)
        {
            return parser.GetDataFieldsListFromValues(valuesSpecs);
        }


        #endregion Data loading functions

        #region Vis specification functions

        private void ExpandVisSpecs(JSONNode newVisSpecs, out JSONNode newVisSpecsExpanded)
        {
            newVisSpecsExpanded = newVisSpecs.Clone();
            parser.ExpandDataSpecs(ref newVisSpecsExpanded);
        }

        private void InferVisSpecs(JSONNode newVisSpecs, out JSONNode newVisSpecsInferred)
        {
            if (markPrefab != null)
            {
                newVisSpecsInferred = newVisSpecs.Clone();
                markPrefab.GetComponent<Mark>().Infer(data, ref newVisSpecsInferred, visSpecsURL);

                if (enableSpecsExpansion)
                {
                    JSONNode visSpecsToWrite = JSON.Parse(visSpecsInferred.ToString());
                    if (visSpecs["data"]["url"] != null && visSpecs["data"]["url"] != "inline")
                    {
                        visSpecsToWrite["data"].Remove("values");
                    }

                    if (visSpecs["interaction"].AsArray.Count == 0)
                    {
                        visSpecsToWrite.Remove("interaction");
                    }

                    #if UNITY_EDITOR
                    System.IO.File.WriteAllText(Parser.GetFullSpecsPath(visSpecsURL), visSpecsToWrite.ToString(2));
                    #else
                    UnityEngine.Windows.File.WriteAllBytes(Parser.GetFullSpecsPath(visSpecsURL),
                        System.Text.Encoding.UTF8.GetBytes(visSpecsToWrite.ToString(2)));
                    #endif
                }
            }
            else
            {
                throw new Exception("Cannot perform inferrence without mark prefab loaded.");
            }
        }

        private void UpdateVisConfig(ref JSONNode visSpecs)
        {
            if (visSpecs["title"] != null)
            {
                title = visSpecs["title"].Value;
            }

            if (visSpecs["width"] == null)
            {
                visSpecs.Add("width", new JSONNumber(DEFAULT_VIS_DIMS));
                width = visSpecs["width"].AsFloat;
            } else
            {
                width = visSpecs["width"].AsFloat;
            }

            if (visSpecs["height"] == null)
            {
                visSpecs.Add("height", new JSONNumber(DEFAULT_VIS_DIMS));
                height = visSpecs["height"].AsFloat;
            }
            else
            {
                height = visSpecs["height"].AsFloat;
            }

            if (visSpecs["depth"] == null)
            {
                visSpecs.Add("depth", new JSONNumber(DEFAULT_VIS_DIMS));
                depth = visSpecs["depth"].AsFloat;
            }
            else
            {
                depth = visSpecs["depth"].AsFloat;
            }
        }

        #endregion Vis specification functions

        #region Channel Encoding functions

        private void CreateChannelEncodingObjects(JSONNode specs, ref List<ChannelEncoding> newChannelEncodings)
        {
            newChannelEncodings = new List<ChannelEncoding>();

            // Go through each channel and create ChannelEncoding for each one
            foreach (KeyValuePair<string, JSONNode> kvp in specs["encoding"].AsObject)
            {
                // The type of ChannelEncoding object which we create depends on the channel
                ChannelEncoding channelEncoding;
                if (kvp.Key.EndsWith("offset"))
                    channelEncoding = new OffsetChannelEncoding();
                else if (kvp.Key == "facetwrap")
                    channelEncoding = new FacetWrapChannelEncoding();
                else
                    channelEncoding = new ChannelEncoding();

                channelEncoding.channel = kvp.Key;
                JSONNode channelSpecs = kvp.Value;

                // Standard encodings with a value property
                if (channelSpecs["value"] != null)
                {
                    channelEncoding.value = channelSpecs["value"].Value.ToString();

                    if (channelSpecs["type"] != null)
                    {
                        channelEncoding.valueDataType = channelSpecs["type"].Value.ToString();
                    }
                }
                // Standard encodings with a field property
                else
                {
                    channelEncoding.field = channelSpecs["field"];

                    // Check validity of data field
                    if(!data.fieldNames.Contains(channelEncoding.field))
                    {
                        throw new Exception("Cannot find data field " + channelEncoding.field + " in data. Please check your spelling (case sensitive).");
                    }

                    if (channelSpecs["type"] != null)
                    {
                        channelEncoding.fieldDataType = channelSpecs["type"];
                    }
                    else
                    {
                        throw new Exception("Missing type for field in channel " + channelEncoding.channel);
                    }
                }

                // Create the scale object
                JSONNode scaleSpecs = channelSpecs["scale"];
                if (scaleSpecs != null)
                {
                    CreateScaleObject(scaleSpecs, ref channelEncoding.scale);
                }

                // Handle special encodings
                if (channelEncoding.IsFacetWrap())
                {
                    FacetWrapChannelEncoding facetWrapChannelEncoding = (FacetWrapChannelEncoding)channelEncoding;

                    if (facetWrapChannelEncoding.field == null || facetWrapChannelEncoding.fieldDataType == null)
                        throw new Exception("Facet wrap channel must have a field and type.");

                    if (facetWrapChannelEncoding.fieldDataType == "quantitative")
                        throw new NotImplementedException("Quantitative types for facet wrap is not yet supported.");

                    if (channelSpecs["directions"] != null)
                    {
                        JSONArray array = channelSpecs["directions"].AsArray;

                        if (array.Count != 2)
                            throw new Exception("Facet wrap requires two direction values to be provided.");

                        string[] dims = new string[] { "x", "y", "z" };
                        for (int i = 0; i < array.Count; i++)
                        {
                            int idx = Array.IndexOf(dims, (string)array[i]);
                            if (idx == -1)
                                throw new Exception("Facet wrap directions can only be x, y, or z. Direction value " + array[i] + " found instead.");
                            facetWrapChannelEncoding.directions.Add(idx);
                        }

                        if (facetWrapChannelEncoding.directions.Distinct().Count() == 1)
                            throw new Exception("Facet wrap directions must all be unique.");
                    }
                    else
                    {
                        // Default to ["x", "y"]
                        facetWrapChannelEncoding.directions.Add(0);
                        facetWrapChannelEncoding.directions.Add(1);
                    }

                    if (channelSpecs["size"] != null)
                    {
                        facetWrapChannelEncoding.size = int.Parse(channelSpecs["size"]);
                    }
                    else
                    {
                        // Set default
                        facetWrapChannelEncoding.size = Mathf.CeilToInt(Mathf.Sqrt(facetWrapChannelEncoding.scale.domain.Count));
                    }

                    if (channelSpecs["spacing"] != null)
                    {
                        JSONArray array = channelSpecs["spacing"].AsArray;

                        if (array.Count != 2)
                            throw new Exception("Facet wrap requires two spacing values to be provided.");

                        for (int i = 0; i < array.Count; i++)
                        {
                            if (!array[i].IsNumber)
                                throw new Exception("Facet wrap spacing values need to be numbers.");

                            facetWrapChannelEncoding.spacing.Add(float.Parse(array[i]));
                        }
                    }
                    else
                    {
                        // Default to the size of the itself
                        float[] sizes = new float[] { width, height, depth };
                        foreach (int direction in facetWrapChannelEncoding.directions)
                        {
                            facetWrapChannelEncoding.spacing.Add(sizes[direction]);
                        }
                    }

                    if (channelSpecs["padding"] != null)
                    {
                        JSONArray array = channelSpecs["padding"].AsArray;

                        if (array.Count != 2)
                            throw new Exception("Facet wrap requires two padding values to be provided.");

                        for (int i = 0; i < array.Count; i++)
                        {
                            if (!array[i].IsNumber)
                                throw new Exception("Facet wrap padding values need to be numbers.");

                            facetWrapChannelEncoding.spacing[i] += float.Parse(array[i]);
                        }
                    }
                    else
                    {
                        // Default to 150 for all dimensions
                        for (int i = 0; i < facetWrapChannelEncoding.spacing.Count; i++)
                        {
                            // If the spacing provided is negative, use negative padding here
                            if (facetWrapChannelEncoding.spacing[i] > 0)
                                facetWrapChannelEncoding.spacing[i] += 150;
                            else
                                facetWrapChannelEncoding.spacing[i] -= 150;
                        }
                    }
                }

                newChannelEncodings.Add(channelEncoding);
            }

            // Now that we have created all of the channel encodings, we go back and add additional information to special ones
            // For now we check offsets
            PopulateOffsetChannelEncodings(ref newChannelEncodings);

            // Then we check translations applied by faceting
            PopulateFacetEncodings(ref newChannelEncodings);
        }

        private void PopulateOffsetChannelEncodings(ref List<ChannelEncoding> newChannelEncodings)
        {
            foreach (ChannelEncoding channelEncoding in newChannelEncodings)
            {
                if (channelEncoding.IsOffset())
                {
                    OffsetChannelEncoding offsetCE = (OffsetChannelEncoding)channelEncoding;

                    // Skip the offset if it doesn't specify a field
                    if (offsetCE.field == null)
                        return;

                    // Make sure that the data type is categorical
                    if (offsetCE.fieldDataType != "ordinal" && offsetCE.fieldDataType != "nominal")
                    {
                        throw new Exception("An offset channel that uses a field must be either ordinal or nominal type.");
                    }

                    // Get the ChannelEncodings of the spatial dimensions. These are used to determine the groups
                    List<ChannelEncoding> spatialCEs = new List<ChannelEncoding>();
                    foreach (string dimension in new string[] { "x", "y,", "z" })
                    {
                        ChannelEncoding ce = newChannelEncodings.SingleOrDefault(x => x.channel == dimension);
                        if (ce != null)
                        {
                            if (ce.fieldDataType == "ordinal" || ce.fieldDataType == "nominal")
                            {
                                spatialCEs.Add(ce);
                            }
                        }
                    }

                    // Convert the ChannelEncodings to string names
                    List<string> spatialGroupFieldNames = spatialCEs.Select(x => x.field).ToList();

                    // If there is a facet wrap also included, we use its field as another grouping
                    ChannelEncoding facetWrapCE = newChannelEncodings.SingleOrDefault(ch => ch.channel == "facetwrap");
                    if (facetWrapCE != null)
                    {
                        spatialGroupFieldNames.Add(facetWrapCE.field);
                    }

                    // Get the ChannelEncoding of the size dimension associated with the offsetting CE direction
                    string spatialChannelName = channelEncoding.channel[0].ToString();
                    string sizeChannelName = (spatialChannelName == "x" ? "width" : (spatialChannelName == "y" ? "height" : "depth"));
                    ChannelEncoding offsettingSizeCE = newChannelEncodings.Single(ch => ch.channel == sizeChannelName);

                    // Get our data values
                    List<Dictionary<string, string>> dataValues = data.values;

                    // Get float values of these size values
                    List<float> sizeValues = new List<float>();
                    // If it is a value
                    if (offsettingSizeCE.value != "" && float.TryParse(offsettingSizeCE.value, out float f))
                    {
                        sizeValues = Enumerable.Repeat(f, dataValues.Count).ToList();
                    }
                    else if (offsettingSizeCE.field != "")
                    {
                        string sizeFieldName = offsettingSizeCE.field;

                        foreach (var dataValue in dataValues)
                        {
                            sizeValues.Add(float.Parse(dataValue[sizeFieldName]));
                        }
                    }

                    // Get the order of the categories. This order will be used to stagger the offset amounts
                    List<string> offsetOrder = offsetCE.scale.domain;

                    // Initialise our data structure
                    // Dictionary key: The names of the grouping spatial CEs concatenated together
                    // Dictionary value: A list of tuples representing datums in the group
                    //      Tuple value: 1) The index of the datum; 2) The value of the offsetting field (i.e., category); 3) The value of the offsetting size field
                    Dictionary<string, List<Tuple<int, string, float>>> groupedDataValues = new Dictionary<string, List<Tuple<int, string, float>>>();

                    // Populate our data structure
                    for (int index = 0; index < dataValues.Count; index++)
                    {
                        Dictionary<string, string> dataValue = dataValues[index];

                        string key = string.Join(" ", spatialGroupFieldNames.Select(x => dataValue[x]));

                        List<Tuple<int, string, float>> groupedList;
                        if (!groupedDataValues.TryGetValue(key, out groupedList))
                        {
                            groupedList = new List<Tuple<int, string, float>>();
                            groupedDataValues.Add(key, groupedList);
                        }

                        groupedList.Add(new Tuple<int, string, float>(index, dataValue[offsetCE.field], sizeValues[index]));
                    }

                    // Sort the lists in the data structure based on the order of categories defined in the offset
                    foreach (var groupedListKey in groupedDataValues.Keys.ToList())
                    {
                        groupedDataValues[groupedListKey] = groupedDataValues[groupedListKey].OrderBy(tuple => offsetOrder.IndexOf(tuple.Item2)).ToList();
                    }

                    // Create a new list which will store our offset values
                    List<string> offsetValues = Enumerable.Repeat("", dataValues.Count).ToList();

                    // For each (now sorted) grouped list, calculate the amount to offset by and store it in the offsetValues list based on the index stored in the tuple
                    // We don't need to update the dictionary any more
                    foreach (var groupedList in groupedDataValues.Values)
                    {
                        float offset = 0;
                        foreach (var tuple in groupedList)
                        {
                            int index = tuple.Item1;
                            offsetValues[index] = offset.ToString();
                            offset += tuple.Item3;
                        }
                    }

                    // Pass this offsetValues list off to the channel encoding
                    offsetCE.values = offsetValues;

                    // Copy over the scale from the size CE
                    offsetCE.scale = offsettingSizeCE.scale;
                }
            }
        }

        private void PopulateFacetEncodings(ref List<ChannelEncoding> newChannelEncodings)
        {
            var ce = newChannelEncodings.SingleOrDefault(ch => ch.channel == "facetwrap");
            if (ce == null)
                return;

            FacetWrapChannelEncoding facetWrapCE = (FacetWrapChannelEncoding)ce;

            // Get the data dimension of the field to facet by
            List<Dictionary<string, string>> dataValues = data.values;
            List<string> facetingValues = new List<string>();
            string facetingField = facetWrapCE.field;
            foreach (var dataValue in dataValues)
            {
                facetingValues.Add(dataValue[facetingField]);
            }

            // Get the order of the categories. This order will be used to position the facets
            List<string> facetOrder = facetWrapCE.scale.domain;

            // Position the facets by calculating translation offsets per each data value (i.e., mark)
            int facetSize = facetWrapCE.size;

            // Create our data structure
            List<string> xTranslationValues = new List<string>();
            List<string> yTranslationValues = new List<string>();
            List<string> zTranslationValues = new List<string>();
            List<Vector3> translationValues = new List<Vector3>();

            // Get the spacing values between each small multiple
            float deltaFirstDir = facetWrapCE.spacing[0];
            float deltaSecondDir = facetWrapCE.spacing[1];

            // Get the indices (0, 1, 2) of the spatial directions which are spacing towards
            int firstDir = facetWrapCE.directions[0];
            int secondDir = facetWrapCE.directions[1];

            for (int i = 0; i < facetingValues.Count; i++)
            {
                // Calculate the index along the two spatial directions as though it were a 2D grid
                int facetIdx = facetOrder.IndexOf(facetingValues[i]);
                int idxFirstDir = facetIdx % facetSize;
                int idxSecondDir = Mathf.FloorToInt(facetIdx / (float)facetSize);

                // Calculate and store translation values based on the calculated index on the grid and the delta spacing between them
                Vector3 translation = Vector3.zero;
                translation[firstDir] = deltaFirstDir * idxFirstDir;
                translation[secondDir] = deltaSecondDir * idxSecondDir;

                xTranslationValues.Add(translation.x.ToString());
                yTranslationValues.Add(translation.y.ToString());
                zTranslationValues.Add(translation.z.ToString());
                translationValues.Add(translation);
            }

            // Store our data structure
            facetWrapCE.xTranslation = xTranslationValues;
            facetWrapCE.yTranslation = yTranslationValues;
            facetWrapCE.zTranslation = zTranslationValues;
            facetWrapCE.translation = translationValues;
            facetWrapCE.numFacets = facetOrder.Count;
        }

        private void CreateScaleObject(JSONNode scaleSpecs, ref Scale scale)
        {
            switch (scaleSpecs["type"].Value.ToString())
            {
                case "none":
                    scale = new ScaleNone(scaleSpecs);
                    break;

                case "linear":
                case "spatial":
                    scale = new ScaleLinear(scaleSpecs, verbose);
                    break;

                case "band":
                    scale = new ScaleBand(scaleSpecs, verbose);
                    break;

                case "point":
                    scale = new ScalePoint(scaleSpecs, verbose);
                    break;

                case "ordinal":
                    scale = new ScaleOrdinal(scaleSpecs, verbose);
                    break;

                case "sequential":
                    scale = new ScaleSequential(scaleSpecs, verbose);
                    break;

                default:
                    scale = null;
                    break;
            }
        }

        private void ApplyChannelEncodings()
        {
            bool isDirectionChanged = false;
            foreach(ChannelEncoding ch in channelEncodings)
            {
                ApplyChannelEncoding(ch, ref markInstances);

                if(ch.channel == "xdirection" || ch.channel == "ydirection" || ch.channel == "zdirection")
                {
                    isDirectionChanged = true;
                }
            }

            if(isDirectionChanged)
            {
                for (int i = 0; i < markInstances.Count; i++)
                {
                    Mark markComponent = markInstances[i].GetComponent<Mark>();

                    markComponent.SetRotation();
                }
            }
        }

        private void ApplyChannelEncoding(ChannelEncoding channelEncoding,
            ref List<GameObject> markInstances)
        {
            for(int i = 0; i < markInstances.Count; i++)
            {
                Mark markComponent = markInstances[i].GetComponent<Mark>();
                if (markComponent == null)
                {
                    throw new Exception("Mark component not present in mark prefab.");
                }

                markComponent.ApplyChannelEncoding(channelEncoding, i);
            }
        }

        #endregion Channel encoding functions

        #region Mark creation functions

        private void InitMarksList()
        {
            marksList = new List<string>();
            marksList.Add(DxR.Vis.UNDEFINED);

            TextAsset marksListTextAsset = (TextAsset)Resources.Load("Marks/marks", typeof(TextAsset));
            if (marksListTextAsset != null)
            {
                JSONNode marksListObject = JSON.Parse(marksListTextAsset.text);
                for (int i = 0; i < marksListObject["marks"].AsArray.Count; i++)
                {
                    string markNameLowerCase = marksListObject["marks"][i].Value.ToString().ToLower();
                    GameObject markPrefabResult = Resources.Load("Marks/" + markNameLowerCase + "/" + markNameLowerCase) as GameObject;

                    if (markPrefabResult != null)
                    {
                        marksList.Add(markNameLowerCase);
                    }
                }
            }
            else
            {
                throw new System.Exception("Cannot find marks.json file in Assets/DxR/Resources/Marks/ directory");
            }

#if UNITY_EDITOR
            string[] dirs = Directory.GetFiles("Assets/DxR/Resources/Marks");
            for (int i = 0; i < dirs.Length; i++)
            {
                if (Path.GetExtension(dirs[i]) != ".meta" && Path.GetExtension(dirs[i]) != ".json"
                    && !marksList.Contains(Path.GetFileName(dirs[i])))
                {
                    marksList.Add(Path.GetFileName(dirs[i]));
                }
            }
#endif

            if (!marksList.Contains(visSpecs["mark"].Value.ToString()))
            {
                marksList.Add(visSpecs["mark"].Value.ToString());
            }
        }

        public List<string> GetMarksList()
        {
            return marksList;
        }

        private void ConstructMarkInstances()
        {
            markInstances = new List<GameObject>();

            // Create one mark prefab instance for each data point:
            int idx = 0;
            foreach (Dictionary<string, string> dataValue in data.values)
            {
                // Instantiate mark prefab, copying parentObject's transform:
                GameObject markInstance = InstantiateMark(markPrefab, marksParentObject.transform);

                // Copy datum in mark:
                Mark mark = markInstance.GetComponent<Mark>();
                mark.datum = dataValue;

                // Copy over polygons for spatial data if applicable
                if (data.polygons != null)
                {
                    mark.polygons = data.polygons[idx];
                    mark.centre = data.centres[idx];
                }

                // Assign tooltip:
                if(enableTooltip)
                {
                    mark.InitTooltip(ref tooltip);
                }

                markInstances.Add(markInstance);
                idx++;
            }
        }

        /// <summary>
        /// Updates mark instances with new values if they already exist, or creates new ones if they either don't exist or do not match mark names
        ///
        /// This essentially supercedes ConstructMarkInstances and all related functions
        /// </summary>
        private void ConstructAndUpdateMarkInstances(bool resetMarkValues = true)
        {
            if (markInstances == null) markInstances = new List<GameObject>();

            // Create one mark for each data point, creating marks if they do not exist
            for (int i = 0; i < data.values.Count; i++)
            {
                Dictionary<string, string> dataValue = data.values[i];

                GameObject markInstance;

                // If there is a mark instance for this dataValue, use it
                if (i < markInstances.Count)
                {
                    // But, only use it if its mark name is correct
                    // TODO: This is a hacky way of checking mark name, so it might break something later on
                    if (markInstances[i].name.Contains(markPrefab.name))
                    {
                        markInstance = markInstances[i];
                    }
                    else
                    {
                        // Otherwise, destroy the old mark and replace it with a new one
                        GameObject oldMarkInstance = markInstances[i];
                        markInstance = InstantiateMark(markPrefab, marksParentObject.transform);
                        markInstances[i] = markInstance;
                        Destroy(oldMarkInstance);
                    }
                }
                // If there isn't a mark instance for this dataValue, create one
                else
                {
                    markInstance = InstantiateMark(markPrefab, marksParentObject.transform);
                    markInstances.Add(markInstance);
                }


                // Copy datum in mark
                Mark mark = markInstance.GetComponent<Mark>();
                mark.datum = dataValue;

                // Rest mark values to default
                if (resetMarkValues)
                    mark.ResetToDefault();

                // Copy over polygons and centres for spatial data if applicable
                if (data.polygons != null)
                {
                    mark.polygons = data.polygons[i];
                    mark.centre = data.centres[i];
                }

                if (enableTooltip)
                {
                    mark.InitTooltip(ref tooltip);
                }
            }

            // Delete all leftover marks
            while (data.values.Count < markInstances.Count)
            {
                GameObject markInstance = markInstances[markInstances.Count - 1];
                Destroy(markInstance);
                markInstances.RemoveAt(markInstances.Count - 1);
            }
        }

        private GameObject InstantiateMark(GameObject markPrefab, Transform parentTransform)
        {
            GameObject mark = Instantiate(markPrefab, parentTransform.position, parentTransform.rotation, parentTransform);
            mark.tag = "DxRMark";
            return mark;
        }

        private void UpdateMarkPrefab(JSONNode visSpecs)
        {
            string markType = visSpecs["mark"].Value;
            markPrefab = LoadMarkPrefab(markType);
        }

        private GameObject LoadMarkPrefab(string markName)
        {
            string markNameLowerCase = markName.ToLower();
            GameObject markPrefabResult = Resources.Load("Marks/" + markNameLowerCase + "/" + markNameLowerCase) as GameObject;

            if (markPrefabResult == null)
            {
                throw new Exception("Cannot load mark " + markNameLowerCase);
            }
            else if (verbose)
            {
                Debug.Log("Loaded mark " + markNameLowerCase);
            }

            // If the prefab does not have a Mark script attached to it, attach the default base Mark script object, i.e., core mark.
            if (markPrefabResult.GetComponent<Mark>() == null)
            {
                DxR.Mark markComponent = markPrefabResult.AddComponent(typeof(DxR.Mark)) as DxR.Mark;
            }
            markPrefabResult.GetComponent<Mark>().markName = markNameLowerCase;

            return markPrefabResult;
        }

        public List<string> GetChannelsList(string markName)
        {
            GameObject markObject = LoadMarkPrefab(markName);
            return markObject.GetComponent<Mark>().GetChannelsList();
        }

        #endregion Mark creation functions

        #region Axis functions

        private void ConstructAxes(JSONNode specs)
        {
            // If there is a facet wrap channel, we will actually need to create more axes
            FacetWrapChannelEncoding facetWrapChannelEncoding = (FacetWrapChannelEncoding)channelEncodings.SingleOrDefault(ce => ce.IsFacetWrap());

            // Go through each channel and create axis for each spatial / position channel:
            for (int channelIndex = 0; channelIndex < channelEncodings.Count; channelIndex++)
            {
                ChannelEncoding channelEncoding = channelEncodings[channelIndex];
                JSONNode axisSpecs = specs["encoding"][channelEncoding.channel]["axis"];
                if (axisSpecs != null && axisSpecs.Value.ToString() != "none" &&
                    (channelEncoding.channel == "x" || channelEncoding.channel == "y" ||
                    channelEncoding.channel == "z"))
                {
                    if (verbose) Debug.Log("Constructing axis for channel " + channelEncoding.channel);

                    ConstructAxisObject(axisSpecs, ref channelEncoding);

                    // Construct even more axes if there is a facet wrap channel
                    if (facetWrapChannelEncoding != null)
                    {
                        if (verbose) Debug.Log("Constructing faceted axes for channel " + channelEncoding.channel);

                        int facetSize = facetWrapChannelEncoding.size;
                        float deltaFirstDir = facetWrapChannelEncoding.spacing[0];
                        float deltaSecondDir = facetWrapChannelEncoding.spacing[1];

                        // Create numFacets - 1 new axes
                        for (int facetIdx = 1; facetIdx < facetWrapChannelEncoding.numFacets; facetIdx++)
                        {
                            Axis axis = ConstructFacetedAxisObject(axisSpecs, ref channelEncoding, ref facetWrapChannelEncoding);

                            // Apply translation to these axes
                            int firstDir = facetWrapChannelEncoding.directions[0];
                            int secondDir = facetWrapChannelEncoding.directions[1];

                            int idxFirstDir = facetIdx % facetSize;
                            int idxSecondDir = Mathf.FloorToInt(facetIdx / (float)facetSize);

                            axis.SetTranslation(deltaFirstDir * idxFirstDir, firstDir);
                            axis.SetTranslation(deltaSecondDir * idxSecondDir, secondDir);
                        }
                    }
                }
            }
        }

        private void ConstructAxisObject(JSONNode axisSpecs, ref ChannelEncoding channelEncoding)
        {
            GameObject axisPrefab = Resources.Load("Axis/Axis", typeof(GameObject)) as GameObject;
            if (axisPrefab != null)
            {
                channelEncoding.axis = Instantiate(axisPrefab, guidesParentObject.transform);
                channelEncoding.axis.GetComponent<Axis>().Init(interactionsParentObject.GetComponent<Interactions>(),
                    channelEncoding.field);
                channelEncoding.axis.GetComponent<Axis>().UpdateSpecs(axisSpecs, channelEncoding.scale);
            }
            else
            {
                throw new Exception("Cannot find axis prefab.");
            }
        }

        private Axis ConstructFacetedAxisObject(JSONNode axisSpecs, ref ChannelEncoding channelEncoding, ref FacetWrapChannelEncoding facetWrapChannelEncoding)
        {
            GameObject axisPrefab = Resources.Load("Axis/Axis", typeof(GameObject)) as GameObject;
            if (axisPrefab != null)
            {
                GameObject axisGameObject = Instantiate(axisPrefab, guidesParentObject.transform);
                facetWrapChannelEncoding.axes.Add(axisGameObject);
                Axis axis = axisGameObject.GetComponent<Axis>();
                axis.Init(interactionsParentObject.GetComponent<Interactions>(),
                    channelEncoding.field);
                axis.UpdateSpecs(axisSpecs, channelEncoding.scale);
                return axis;
            }
            else
            {
                throw new Exception("Cannot find axis prefab.");
            }
        }

        /// <summary>
        /// Updates axis instances with new values if they already exist, or creates new ones if they don't exist
        ///
        /// This essentially supercedes ConstructAxes and all related functions
        /// </summary>
        private void ConstructAndUpdateAxes(JSONNode specs, ref List<ChannelEncoding> channelEncodings)
        {
            // Create a list that will keep track of the axes that we will want to keep
            // The ones which we haven't visited we will destroy at the end of this function
            List<string> visitedAxisChannels = new List<string>();
            // Also, create an equivalent list for faceting
            List<string> visitedFacetingAxisChannels = new List<string>();

            // If there is a facet wrap channel, we will actually need to create more axes
            FacetWrapChannelEncoding facetWrapChannelEncoding = (FacetWrapChannelEncoding)channelEncodings.SingleOrDefault(ce => ce.IsFacetWrap());

            // Go through each channel and create axis for each spatial / position channel:
            for (int channelIndex = 0; channelIndex < channelEncodings.Count; channelIndex++)
            {
                ChannelEncoding channelEncoding = channelEncodings[channelIndex];
                string channel = channelEncoding.channel;

                // Only certain channel encodings can have axes. Skip this encoding if it is not one of these
                if (channel != "x" && channel != "y" && channel != "z")
                    continue;

                // Get the specs for this axis
                JSONNode axisSpecs = specs["encoding"][channel]["axis"];

                // If no specs were defined for this axis, skip it
                if (!(axisSpecs != null && axisSpecs.Value != "none"))
                    continue;

                if (verbose) Debug.Log("Constructing axis for channel " + channelEncoding.channel);

                ConstructAndUpdateAxisObject(channel, axisSpecs, ref channelEncoding, out Axis a);

                visitedAxisChannels.Add(channel);

                // If there is a facetwrap channel, we need to add more axes
                if (facetWrapChannelEncoding != null)
                {
                    if (verbose) Debug.Log("Constructing faceted axes for channel " + channelEncoding.channel);

                    int numFacets = facetWrapChannelEncoding.numFacets;
                    int facetSize = facetWrapChannelEncoding.size;
                    float deltaFirstDir = facetWrapChannelEncoding.spacing[0];
                    float deltaSecondDir = facetWrapChannelEncoding.spacing[1];

                    // Create the axes for our facet
                    List<Axis> facetedAxes = new List<Axis>();
                    ConstructAndUpdateFacetedAxisObjects(channel, axisSpecs, ref channelEncoding, ref facetedAxes, numFacets - 1);

                    // Arrange the facets
                    for (int facetIdx = 0; facetIdx < facetedAxes.Count; facetIdx++)
                    {
                        Axis axis = facetedAxes[facetIdx];

                        // Apply translation to these axes
                        int firstDir = facetWrapChannelEncoding.directions[0];
                        int secondDir = facetWrapChannelEncoding.directions[1];

                        int idxFirstDir = (facetIdx + 1) % facetSize;
                        int idxSecondDir = Mathf.FloorToInt((facetIdx + 1) / (float)facetSize);

                        axis.SetTranslation(deltaFirstDir * idxFirstDir, firstDir);
                        axis.SetTranslation(deltaSecondDir * idxSecondDir, secondDir);
                    }

                    visitedFacetingAxisChannels.Add(channel);
                }
            }

            // Go through and delete the axes for the channels which we did not visit
            foreach (var kvp in axisInstances)
            {
                if (!visitedAxisChannels.Contains(kvp.Key))
                    Destroy(kvp.Value.gameObject);
            }
            foreach (var kvp in facetedAxisInstances)
            {
                if (!visitedFacetingAxisChannels.Contains(kvp.Key))
                {
                    foreach (Axis axis in kvp.Value)
                        Destroy(axis.gameObject);
                }
            }

            // Clear dictionary references
            foreach (string key in axisInstances.Keys.ToList())
            {
                if (!visitedAxisChannels.Contains(key))
                    axisInstances.Remove(key);
            }
            foreach (string key in facetedAxisInstances.Keys.ToList())
            {
                if (!visitedFacetingAxisChannels.Contains(key))
                    facetedAxisInstances.Remove(key);
            }
        }

        private void ConstructAndUpdateAxisObject(string channel, JSONNode axisSpecs, ref ChannelEncoding channelEncoding, out Axis axis)
        {
            // Get the axis object
            if (!axisInstances.TryGetValue(channel, out axis))
            {
                GameObject axisGameObject = Instantiate(Resources.Load("Axis/Axis", typeof(GameObject)), guidesParentObject.transform) as GameObject;
                axis = axisGameObject.GetComponent<Axis>();
                axisInstances.Add(channel, axis);
            }

            axis.Init(interactionsParentObject.GetComponent<Interactions>(), channelEncoding.field);
            axis.UpdateSpecs(axisSpecs, channelEncoding.scale);
        }

        private void ConstructAndUpdateFacetedAxisObjects(string channel, JSONNode axisSpecs, ref ChannelEncoding channelEncoding, ref List<Axis> axisList, int count)
        {
            // Get the axis list
            if (!facetedAxisInstances.TryGetValue(channel, out axisList))
            {
                axisList = new List<Axis>();
                facetedAxisInstances.Add(channel, axisList);
            }

            // Get the axis object from the list if it already exists, or create a new one
            Axis axis;
            for (int i = 0; i < count; i++)
            {
                if (i < axisList.Count)
                {
                    axis = axisList[i];
                }
                else
                {
                    GameObject axisGameObject = Instantiate(Resources.Load("Axis/Axis", typeof(GameObject)), guidesParentObject.transform) as GameObject;
                    axis = axisGameObject.GetComponent<Axis>();
                    axisList.Add(axis);
                }

                axis.Init(interactionsParentObject.GetComponent<Interactions>(), channelEncoding.field);
                axis.UpdateSpecs(axisSpecs, channelEncoding.scale);
            }

            // Delete all leftover axes
            while (count < axisList.Count)
            {
                axis = axisList[axisList.Count - 1];
                Destroy(axis.gameObject);
                axisList.RemoveAt(axisList.Count - 1);
            }
        }

        #endregion Axis functions

        #region Legend functions

        private void ConstructLegends(JSONNode specs)
        {
            // Go through each channel and create legend for color, shape, or size channels:
            for (int channelIndex = 0; channelIndex < channelEncodings.Count; channelIndex++)
            {
                ChannelEncoding channelEncoding = channelEncodings[channelIndex];
                JSONNode legendSpecs = specs["encoding"][channelEncoding.channel]["legend"];
                if (legendSpecs != null && legendSpecs.Value.ToString() != "none" && channelEncoding.channel == "color")
                {
                    if (verbose) Debug.Log("Constructing legend for channel " + channelEncoding.channel);

                    ConstructLegendObject(legendSpecs, ref channelEncoding);
                }
            }
        }

        private void ConstructLegendObject(JSONNode legendSpecs, ref ChannelEncoding channelEncoding)
        {
            GameObject legendPrefab = Resources.Load("Legend/Legend", typeof(GameObject)) as GameObject;
            if (legendPrefab != null && markPrefab != null)
            {
                channelEncoding.legend = Instantiate(legendPrefab, guidesParentObject.transform);
                channelEncoding.legend.GetComponent<Legend>().Init(interactionsParentObject.GetComponent<Interactions>());
                channelEncoding.legend.GetComponent<Legend>().UpdateSpecs(legendSpecs, ref channelEncoding, markPrefab);
            }
            else
            {
                throw new Exception("Cannot find legend prefab.");
            }
        }

        #endregion Legend functions

        #region GUI and interactions functions

        private void ConstructInteractions(JSONNode specs)
        {
            if (specs["interaction"] == null) return;

            interactionsParentObject.GetComponent<Interactions>().Init(this);

            foreach (JSONObject interactionSpecs in specs["interaction"].AsArray)
            {
                if(interactionSpecs["type"] != null && interactionSpecs["field"] != null && interactionSpecs["domain"] != null)
                {
                    switch(interactionSpecs["type"].Value)
                    {
                        case "thresholdFilter":
                            AddThresholdFilterInteraction(interactionSpecs);
                            break;

                        case "toggleFilter":
                            AddToggleFilterInteraction(interactionSpecs);
                            break;

                        default:
                            return;
                    }

                    if (verbose) Debug.Log("Constructed interaction: " + interactionSpecs["type"].Value +
                        " for data field " + interactionSpecs["field"].Value);
                } else
                {
                    if (verbose) Debug.Log("Make sure interaction object has type, field, and domain specs.");
//                    throw new System.Exception("Make sure interaction object has type, field, and domain specs.");
                }

            }
        }

        private void AddThresholdFilterInteraction(JSONObject interactionSpecs)
        {
            if (interactionsParentObject != null)
            {
                interactionsParentObject.GetComponent<Interactions>().AddThresholdFilter(interactionSpecs);
            }
        }

        private void AddToggleFilterInteraction(JSONObject interactionSpecs)
        {
            if(interactionsParentObject != null)
            {
                interactionsParentObject.GetComponent<Interactions>().AddToggleFilter(interactionSpecs);
            }
        }

        // Update the visibility of each mark according to the filters results:
        internal void FiltersUpdated()
        {
            if(interactionsParentObject != null)
            {
                ShowAllMarks();

                foreach (KeyValuePair<string,List<bool>> filterResult in interactionsParentObject.GetComponent<Interactions>().filterResults)
                {
                    List<bool> visib = filterResult.Value;
                    for (int m = 0; m < markInstances.Count; m++)
                    {
                        markInstances[m].SetActive(visib[m] && markInstances[m].activeSelf);
                    }
                }
            }
        }

        private void InitGUI()
        {
            Transform guiTransform = parentObject.transform.Find("DxRGUI");
            GameObject guiObject = guiTransform.gameObject;
            gui = guiObject.GetComponent<GUI>();
            gui.Init(this, verbose);

            if (!enableGUI && guiObject != null)
            {
                guiObject.SetActive(false);
            }
        }

        private int GetFieldIndexInInteractionSpecs(JSONNode interactionSpecs, string searchFieldName)
        {
            int index = 0;
            foreach (JSONObject interactionObject in interactionSpecs.AsArray)
            {
                string fieldName = interactionObject["field"];
                if (fieldName == searchFieldName)
                {
                    return index;
                }
                index++;
            }
            return -1;
        }

        private bool FieldIsInInteractionSpecs(JSONNode interactionSpecs, string searchFieldName)
        {
            foreach (JSONObject interactionObject in interactionSpecs.AsArray)
            {
                string fieldName = interactionObject["field"];
                if(fieldName == searchFieldName)
                {
                    return true;
                }
            }
            return false;
        }

        #endregion GUI and interactions functions

        #region Vis loading functions

        /// <summary>
        /// Manually calls an update to update the VisSpecs from a given string, rather than from a file URL
        /// </summary>
        public void UpdateVisSpecsFromStringSpecs(string specs)
        {
            JSONNode textSpecs;
            parser.ParseString(specs, out textSpecs);

            visSpecs = textSpecs;

            if (enableGUI)
                gui.UpdateGUISpecsFromVisSpecs();

            UpdateVis();
        }

        public void UpdateVisSpecsFromJSONNode(JSONNode specs, bool updateGuiSpecs = true, bool callUpdateEvent = true)
        {
            visSpecs = specs;

            if (enableGUI && updateGuiSpecs)
                gui.UpdateGUISpecsFromVisSpecs();

            UpdateVis(callUpdateEvent);
        }

        public void UpdateVisSpecsFromTextSpecs()
        {
            // For now, just reset the vis specs to empty and
            // copy the contents of the text to vis specs; starting
            // everything from scratch. Later on, the new specs will have
            // to be compared with the current specs to get a list of what
            // needs to be updated and only this list will be acted on.
            JSONNode textSpecs;
            parser.Parse(visSpecsURL, out textSpecs);

            visSpecs = textSpecs;

            if (enableGUI)
                gui.UpdateGUISpecsFromVisSpecs();

            UpdateVis();
        }

        public void UpdateTextSpecsFromVisSpecs()
        {
            JSONNode visSpecsToWrite = JSON.Parse(visSpecs.ToString());
            if(visSpecs["data"]["url"] != null && visSpecs["data"]["url"] != "inline")
            {
                visSpecsToWrite["data"].Remove("values");
            }

            if(visSpecs["interaction"].AsArray.Count == 0)
            {
                visSpecsToWrite.Remove("interaction");
            }

            #if UNITY_EDITOR
            System.IO.File.WriteAllText(Parser.GetFullSpecsPath(visSpecsURL), visSpecsToWrite.ToString(2));
            #else

            UnityEngine.Windows.File.WriteAllBytes(Parser.GetFullSpecsPath(visSpecsURL),
                System.Text.Encoding.UTF8.GetBytes(visSpecsToWrite.ToString(2)));
            #endif
        }

        private void UpdateGUISpecsFromVisSpecs()
        {
            gui.UpdateGUISpecsFromVisSpecs();
        }

        public void UpdateVisSpecsFromGUISpecs()
        {
            // For now, just reset the vis specs to empty and
            // copy the contents of the text to vis specs; starting
            // everything from scratch. Later on, the new specs will have
            // to be compared with the current specs to get a list of what
            // needs to be updated and only this list will be acted on.

            JSONNode guiSpecs = JSON.Parse(gui.GetGUIVisSpecs().ToString());


            // Remove data values so that parsing can put them again.
            // TODO: Optimize this.
            if (guiSpecs["data"]["url"] != null)
            {
                if(guiSpecs["data"]["url"] != "inline")
                {
                    guiSpecs["data"].Remove("values");
                    visSpecs["data"].Remove("values");

                    visSpecs["data"]["url"] = guiSpecs["data"]["url"];
                }
            }

            visSpecs["mark"] = guiSpecs["mark"];

            if (verbose) Debug.Log("GUI SPECS: " + guiSpecs.ToString());

            // UPDATE CHANNELS:

            // Go through vis specs and UPDATE fields and types of non-value channels
            // that are in the gui specs.
            List<string> channelsToUpdate = new List<string>();
            foreach (KeyValuePair<string, JSONNode> kvp in visSpecs["encoding"].AsObject)
            {
                string channelName = kvp.Key;
                if(visSpecs["encoding"][channelName]["value"] == null && guiSpecs["encoding"][channelName] != null)
                {
                    channelsToUpdate.Add(channelName);
                }
            }

            foreach(string channelName in channelsToUpdate)
            {
                visSpecs["encoding"][channelName]["field"] = guiSpecs["encoding"][channelName]["field"];
                visSpecs["encoding"][channelName]["type"] = guiSpecs["encoding"][channelName]["type"];
            }

            // Go through vis specs and DELETE non-field channels that are not in gui specs.
            List<string> channelsToDelete = new List<string>();
            foreach (KeyValuePair<string, JSONNode> kvp in visSpecs["encoding"].AsObject)
            {
                string channelName = kvp.Key;
                if (visSpecs["encoding"][channelName]["value"] == null && guiSpecs["encoding"][channelName] == null)
                {
                    channelsToDelete.Add(channelName);
                }
            }

            foreach (string channelName in channelsToDelete)
            {
                visSpecs["encoding"].Remove(channelName);
            }

            // Go through gui specs and ADD non-field channels in gui specs that are not in vis specs.
            foreach (KeyValuePair<string, JSONNode> kvp in guiSpecs["encoding"].AsObject)
            {
                string channelName = kvp.Key;
                if (verbose) Debug.Log("Testing channel " + channelName);

                if (guiSpecs["encoding"][channelName]["value"] == null && visSpecs["encoding"][channelName] == null)
                {
                    if (verbose) Debug.Log("Adding channel " + channelName);
                    visSpecs["encoding"].Add(channelName, guiSpecs["encoding"][channelName]);
                }
            }

            // UPDATE INTERACTIONS:
            // Go through vis specs and UPDATE fields and types of interactions
            // that are in the gui specs.
            List<string> fieldsToUpdate = new List<string>();
            foreach(JSONObject interactionSpecs in visSpecs["interaction"].AsArray)
            {
                string fieldName = interactionSpecs["field"];
                // If the field is in gui, it needs update:
                if(FieldIsInInteractionSpecs(guiSpecs["interaction"], fieldName))
                {
                    fieldsToUpdate.Add(fieldName);
                }
            }

            // Do the update:
            foreach (string fieldName in fieldsToUpdate)
            {
                visSpecs["interaction"][GetFieldIndexInInteractionSpecs(visSpecs["interaction"], fieldName)]["type"] =
                    guiSpecs["interaction"][GetFieldIndexInInteractionSpecs(visSpecs["interaction"], fieldName)]["type"];
            }

            // Go through vis specs and DELETE interactions for fields that are not in gui specs.
            List<string> fieldsToDelete = new List<string>();
            foreach (JSONObject interactionSpecs in visSpecs["interaction"].AsArray)
            {
                string fieldName = interactionSpecs["field"];
                if (!FieldIsInInteractionSpecs(guiSpecs["interaction"], fieldName))
                {
                    fieldsToDelete.Add(fieldName);
                }
            }

            foreach (string fieldName in fieldsToDelete)
            {
                visSpecs["interaction"].Remove(GetFieldIndexInInteractionSpecs(visSpecs["interaction"], fieldName));
            }

            // Go through gui specs and ADD interaction for fields in gui specs that are not in vis specs.
            foreach (JSONObject interactionSpecs in guiSpecs["interaction"].AsArray)
            {
                string fieldName = interactionSpecs["field"].Value;

                if (!FieldIsInInteractionSpecs(visSpecs["interaction"], fieldName))
                {
                    if (verbose) Debug.Log("Adding interaction for field " + fieldName);
                    visSpecs["interaction"].Add(guiSpecs["interaction"][GetFieldIndexInInteractionSpecs(guiSpecs["interaction"], fieldName)]);
                }
            }

            UpdateTextSpecsFromVisSpecs();
            UpdateVis();
        }

        #endregion Vis loading functions

        #region Unity GameObject handling functions

        private void UpdateVisPose(JSONNode visSpecsInferred)
        {
            if (visSpecsInferred["position"] != null)
            {
                JSONNode positionSpecs = visSpecsInferred["position"];
                Vector3 position = transform.position;
                if (positionSpecs["value"] != null)
                {
                    position = new Vector3(positionSpecs["value"][0].AsFloat, positionSpecs["value"][1].AsFloat, positionSpecs["value"][2].AsFloat);
                }
                else
                {
                    if (positionSpecs["x"] != null) position.x = positionSpecs["x"].AsFloat;

                    if (positionSpecs["y"] != null) position.y = positionSpecs["y"].AsFloat;

                    if (positionSpecs["z"] != null) position.z = positionSpecs["z"].AsFloat;
                }
                transform.position = position;
            }

            if (visSpecsInferred["rotation"] != null)
            {
                JSONNode rotationSpecs = visSpecsInferred["rotation"];

                if (rotationSpecs["value"] != null)
                {
                    // Euler Angles
                    if (rotationSpecs["value"].Count == 3)
                    {
                        Vector3 eulerAngles = new Vector3(rotationSpecs["value"][0].AsFloat, rotationSpecs["value"][1].AsFloat, rotationSpecs["value"][2].AsFloat);
                        transform.eulerAngles = eulerAngles;
                    }
                    // Quaternion
                    else
                    {
                        Quaternion quaternion = new Quaternion(rotationSpecs["value"][0].AsFloat,rotationSpecs["value"][1].AsFloat, rotationSpecs["value"][2].AsFloat, rotationSpecs["value"][3].AsFloat);
                        transform.rotation = quaternion;
                    }
                }
                else
                {
                    Vector3 eulerAngles = transform.eulerAngles;
                    if (rotationSpecs["x"] != null) eulerAngles.x = rotationSpecs["x"].AsFloat;
                    if (rotationSpecs["y"] != null) eulerAngles.y = rotationSpecs["y"].AsFloat;
                    if (rotationSpecs["z"] != null) eulerAngles.z = rotationSpecs["z"].AsFloat;
                    transform.eulerAngles = eulerAngles;
                }
            }
        }

        private void DeleteAll()
        {
            DeleteMarks();
            DeleteAxes();
            DeleteLegends();
            DeleteInteractions();
        }

        private void DeleteMarks()
        {
            foreach (Transform child in marksParentObject.transform)
            {
                child.gameObject.SetActive(false);
                GameObject.Destroy(child.gameObject);
            }
        }

        private void DeleteLegends()
        {
            foreach (Transform child in guidesParentObject.transform)
            {
                if (child.gameObject.GetComponent<Legend>() != null)
                {
                    child.gameObject.SetActive(false);
                    GameObject.Destroy(child.gameObject);
                }
            }
        }

        private void DeleteAxes()
        {
            foreach (Transform child in guidesParentObject.transform)
            {
                if (child.gameObject.GetComponent<Axis>() != null)
                {
                    child.gameObject.SetActive(false);
                    GameObject.Destroy(child.gameObject);
                }
            }
            axisInstances.Clear();
            facetedAxisInstances.Clear();
        }

        private void DeleteInteractions()
        {
            // TODO: Do not delete, but only update:
            foreach (Transform child in interactionsParentObject.transform)
            {
                child.gameObject.SetActive(false);
                GameObject.Destroy(child.gameObject);
            }
        }

        void ShowAllMarks()
        {
            for (int m = 0; m < markInstances.Count; m++)
            {
                markInstances[m].SetActive(true);
            }
        }
        public void RotateAroundCenter(Vector3 rotationAxis, float angleDegrees)
        {
            Vector3 center = viewParentObject.transform.parent.transform.position +
                new Vector3(width * SIZE_UNIT_SCALE_FACTOR / 2.0f, height * SIZE_UNIT_SCALE_FACTOR / 2.0f,
                depth * SIZE_UNIT_SCALE_FACTOR / 2.0f);
            viewParentObject.transform.RotateAround(center, rotationAxis, angleDegrees);
        }


        public void Rescale(float scaleFactor)
        {
            viewParentObject.transform.localScale = Vector3.Scale(viewParentObject.transform.localScale,
                new Vector3(scaleFactor, scaleFactor, scaleFactor));
        }

        public Vector3 GetVisSize()
        {
            return new Vector3(width, height, depth);
        }

        public void ResetView()
        {
            viewParentObject.transform.localScale = new Vector3(1, 1, 1);
            viewParentObject.transform.localEulerAngles = new Vector3(0, 0, 0);
            viewParentObject.transform.localPosition = new Vector3(0, 0, 0);
        }

        /// <summary>
        /// Updates the BoxCollider on this Vis to be the same size as the bounding box of all of its renderers (marks, axes, text, etc.)
        ///
        /// Code to handle rotation from: https://answers.unity.com/questions/17968/finding-the-bounds-of-a-grouped-model.html?childToView=1147799#answer-1147799
        /// </summary>
        private void UpdateCollider()
        {
            Quaternion currentRotation = transform.rotation;
            transform.rotation = Quaternion.identity;

            // Iterate through all renderers on this Vis gameobject
            Renderer[] renderers = gameObject.GetComponentsInChildren<Renderer>();
            if (renderers.Length > 0)
            {
                Bounds bounds = renderers[0].bounds;

                for (int i = 1; i < renderers.Length; i++)
                {
                    bounds.Encapsulate(renderers[i].bounds);
                }

                Vector3 localCenter = bounds.center - transform.position;
                bounds.center = localCenter;

                boxCollider.center = bounds.center;
                boxCollider.size = bounds.size;
            }
            // If no renderers, then just hide the collider
            else
            {
                boxCollider.size = Vector3.zero;
            }

            transform.rotation = currentRotation;
        }

        #endregion Unity GameObject handling functions
    }

}
