using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SimpleJSON;
using UnityEngine;
using UnityEngine.UI;
using UniRx;
using DxR.VisMorphs;
using static DxR.VisMorphs.EasingFunction;

namespace DxR
{
    /// <summary>
    /// Base class for Mark classes (e.g., MarkPoint for point mark).
    /// Contains methods for setting common mark channels such as position and size.
    /// </summary>
    public class Mark : MonoBehaviour
    {
        public string markName = DxR.Vis.UNDEFINED;
        public Dictionary<string, string> datum = null;
        public List<List<GeoJSON.Net.Geometry.IPosition>> polygons = null;
        public GeoJSON.Net.Geometry.IPosition centre;
        GameObject tooltip = null;

        public Vector3 forwardDirection = Vector3.up;
        Vector3 curDirection;

        protected Renderer myRenderer;
        protected GeometricValues defaultGeometricValues;

        protected struct GeometricValues
        {
            public Vector3 localPosition;
            public Quaternion localRotation;
            public Vector3 localScale;
            public Color colour;
        }

        protected class ActiveMarkTransition : ActiveTransition
        {
            public CompositeDisposable Disposable;
            public int MarkIndex;

            public ActiveMarkTransition(ActiveTransition activeTransition, CompositeDisposable disposable, int markIndex)
            {
                this.Name = activeTransition.Name;
                this.ChangedChannelEncodings = activeTransition.ChangedChannelEncodings;
                this.InitialVisSpecs = activeTransition.InitialVisSpecs;
                this.FinalVisSpecs = activeTransition.FinalVisSpecs;
                this.TweeningObservable = activeTransition.TweeningObservable;
                this.EasingFunction = activeTransition.EasingFunction;
                this.Stages = activeTransition.Stages;
                this.Disposable = disposable;
                this.MarkIndex = markIndex;
            }
        }

        protected Dictionary<string, ActiveMarkTransition> activeMarkTransitions = new Dictionary<string, ActiveMarkTransition>();
        protected static readonly string[] spatialChannelNames = new string[] { "x", "xoffset", "xoffsetpct", "y", "yoffset", "yoffsetpct", "z", "zoffset", "zoffsetpct", "facetwrap" };
        protected static readonly string[] idealChannelApplicationOrder = new string[] { "x", "y", "z", "size", "width", "height", "depth", "length",
                                                                                         "color", "opacity",
                                                                                         "xrotation", "yrotation", "zrotation", "xdirection", "ydirection", "zdirection",
                                                                                         "xoffset", "yoffset", "zoffset", "xoffsetpct", "yoffsetpct", "zoffsetpct",
                                                                                         "facetwrap" };

        public Mark()
        {
        }

        public void Awake()
        {
            myRenderer = GetComponent<Renderer>();

            defaultGeometricValues = new GeometricValues
            {
                localPosition = transform.localPosition,
                localRotation = transform.localRotation,
                localScale = transform.localScale,
                colour = myRenderer.material.color
            };
        }

        public void Start()
        {
            curDirection = forwardDirection;
        }

        public virtual List<string> GetChannelsList()
        {
            return new List<string> { "x", "y", "z", "color", "size", "width", "height", "depth", "opacity", "xrotation", "yrotation", "zrotation", "length", "xdirection", "ydirection", "zdirection" };
        }

        public virtual void ResetToDefault()
        {
            transform.localPosition = defaultGeometricValues.localPosition;
            transform.localRotation = defaultGeometricValues.localRotation;
            transform.localScale = defaultGeometricValues.localScale;
            myRenderer.material.color = defaultGeometricValues.colour;
        }

        #region Morphing functions

        public void InitialiseTransition(ActiveTransition newActiveTransition, int markIndex)
        {
            if (activeMarkTransitions.ContainsKey(newActiveTransition.Name))
                throw new Exception(string.Format("Vis Morphs: Mark already contains subscriptions for the transition {0}. This error shouldn't happen", newActiveTransition.Name));

            // Create the disposable which will be used to store and quickly unsubscribe from our tweens
            CompositeDisposable transitionDisposable = new CompositeDisposable();

            // Create our own new mark-specific version of the transition object to make our life a bit easier
            ActiveMarkTransition activeMarkTransition = new ActiveMarkTransition(newActiveTransition, transitionDisposable, markIndex);
            activeMarkTransitions.Add(newActiveTransition.Name, activeMarkTransition);

            // First we initialise the transition channels for spatial channels (x, xoffset, xoffsetpct, etc.)
            // We also always include any faceting channels as these affect all spatial channels
            InitialiseSpatialTransitions(activeMarkTransition);

            foreach (var tuple in activeMarkTransition.ChangedChannelEncodings)
            {
                // Ignore all spatial channels
                if (!spatialChannelNames.Contains(tuple.Item3))
                    InitialiseTransitionChannel(activeMarkTransition, tuple);
            }
        }

        public void StopTransition(string transitionName, bool goToEnd = true)
        {
            if (!activeMarkTransitions.ContainsKey(transitionName))
                throw new Exception(string.Format("Vis Morphs: Mark does not contain subscriptions for the transition {0}. This shouldn't happen", transitionName));

            var stoppingMarkTransition = activeMarkTransitions[transitionName];

            // Dispose all active subscriptions
            stoppingMarkTransition.Disposable.Dispose();

            // We want to ensure that the encodings are applied in the correct order
            var transitionChannelEncodings = stoppingMarkTransition.ChangedChannelEncodings;
            transitionChannelEncodings = transitionChannelEncodings.OrderBy(tuple => Array.IndexOf(idealChannelApplicationOrder, tuple.Item3)).ToList();

            // Since the offset channels apply a translation to the mark, we don't want to apply them again UNLESS an x, y, or z channel is present
            // TODO / NOTE: This might cause more bugs later down the line
            List<string> channelNames = transitionChannelEncodings.Select(tuple => tuple.Item3).ToList();
            List<string> resettedSpatialChannels = new List<string>();

            // For each changed encoding, set the mark's values to either the start or end depending on the goToEnd flag
            foreach (var tuple in transitionChannelEncodings)
            {
                string channel = tuple.Item3;
                ChannelEncoding ce = goToEnd ? tuple.Item2 : tuple.Item1;

                // If this encoding is an offset, we'll need to check whether or not to initially reset its base spatial dimension first
                // We only do so if there is no base spatial dimension even defined
                if (channel.Contains("offset") && !channelNames.Contains(channel.Substring(0, 1)))
                {
                    string spatialChannel = channel.Substring(0, 1);
                    if (!resettedSpatialChannels.Contains(spatialChannel))
                    {
                        // Reset base spatial dimension to default value
                        SetChannelValue(spatialChannel, "0");
                        resettedSpatialChannels.Add(spatialChannel);
                    }

                    // If there isn't actually an offset to apply in the target state, skip
                    if (ce == null)
                        continue;
                }

                if (ce != null)
                {
                    ApplyChannelEncoding(ce, stoppingMarkTransition.MarkIndex);
                }
                else
                {
                    SetChannelValue(channel, GetDefaultValueForChannel(channel));
                }
            }

            activeMarkTransitions.Remove(transitionName);
        }

        /// <summary>
        /// It doesn't really make much sense to handle offset channels separately from positional channels. This function handles all of them together
        ///
        /// We set the values stored in the initial and final state as: (spatial dimension value) + (offset value) * (offsetpct value)
        /// The tweener then inperpolates between the two as per normal
        /// </summary>
        protected virtual void InitialiseSpatialTransitions(ActiveMarkTransition activeMarkTransition)
        {
            bool xVisited, yVisited, zVisited = xVisited = yVisited = false;

            // Get the facetting channel encodings (doesn't matter if they don't exist)
            var facetwrapCE = activeMarkTransition.ChangedChannelEncodings.SingleOrDefault(tuple => tuple.Item3 == "facetwrap");

            foreach (var tuple in activeMarkTransition.ChangedChannelEncodings)
            {
                string channel = tuple.Item3;

                if (spatialChannelNames.Contains(channel))
                {
                    if (channel.StartsWith("x") && !xVisited)
                    {
                        xVisited = true;

                        // Find all tuples relating to this spatial dimension
                        var dimCE = activeMarkTransition.ChangedChannelEncodings.SingleOrDefault(tuple => tuple.Item3 == "x");
                        var offsetCE = activeMarkTransition.ChangedChannelEncodings.SingleOrDefault(tuple => tuple.Item3 == "xoffset");
                        var offsetpctCE = activeMarkTransition.ChangedChannelEncodings.SingleOrDefault(tuple => tuple.Item3 == "xoffsetpct");
                        // We also need the size one so that offsetpct can work properly
                        var sizeCE = activeMarkTransition.ChangedChannelEncodings.SingleOrDefault(tuple => tuple.Item3 == "width");

                        InitialiseSpatialTransitionChannel(activeMarkTransition, "x", dimCE, offsetCE, offsetpctCE, facetwrapCE, sizeCE);
                    }

                    if (channel.StartsWith("y") && !yVisited)
                    {
                        yVisited = true;

                        // Find all tuples relating to this spatial dimension
                        var dimCE = activeMarkTransition.ChangedChannelEncodings.SingleOrDefault(tuple => tuple.Item3 == "y");
                        var offsetCE = activeMarkTransition.ChangedChannelEncodings.SingleOrDefault(tuple => tuple.Item3 == "yoffset");
                        var offsetpctCE = activeMarkTransition.ChangedChannelEncodings.SingleOrDefault(tuple => tuple.Item3 == "yoffsetpct");
                        // We also need the size one so that offsetpct can work properly
                        var sizeCE = activeMarkTransition.ChangedChannelEncodings.SingleOrDefault(tuple => tuple.Item3 == "height");

                        InitialiseSpatialTransitionChannel(activeMarkTransition, "y", dimCE, offsetCE, offsetpctCE, facetwrapCE, sizeCE);
                    }

                    if (channel.StartsWith("z") && !zVisited)
                    {
                        zVisited = true;

                        // Find all tuples relating to this spatial dimension
                        var dimCE = activeMarkTransition.ChangedChannelEncodings.SingleOrDefault(tuple => tuple.Item3 == "z");
                        var offsetCE = activeMarkTransition.ChangedChannelEncodings.SingleOrDefault(tuple => tuple.Item3 == "zoffset");
                        var offsetpctCE = activeMarkTransition.ChangedChannelEncodings.SingleOrDefault(tuple => tuple.Item3 == "zoffsetpct");
                        // We also need the size one so that offsetpct can work properly
                        var sizeCE = activeMarkTransition.ChangedChannelEncodings.SingleOrDefault(tuple => tuple.Item3 == "depth");

                        InitialiseSpatialTransitionChannel(activeMarkTransition, "z", dimCE, offsetCE, offsetpctCE, facetwrapCE, sizeCE);
                    }
                }
            }
        }

        protected virtual void InitialiseSpatialTransitionChannel(ActiveMarkTransition activeMarkTransition, string channel, Tuple<ChannelEncoding, ChannelEncoding, string> baseCE,
                                                             Tuple<ChannelEncoding, ChannelEncoding, string> offsetCE, Tuple<ChannelEncoding, ChannelEncoding, string> offsetpctCE,
                                                             Tuple<ChannelEncoding, ChannelEncoding, string> facetwrapCE, Tuple<ChannelEncoding, ChannelEncoding, string> sizeCE)
        {
            // Create the start and end values for the tweening
            float initialValue = 0;
            float finalValue = 0;
            int dim = channel == "x" ? 0 : channel == "y" ? 1 : 2;
            int markIndex = activeMarkTransition.MarkIndex;

            // Get the base value
            if (baseCE != null)
            {
                if (baseCE.Item1 != null)
                    initialValue = float.Parse(GetValueForChannelEncoding(baseCE.Item1, markIndex));
                if (baseCE.Item2 != null)
                    finalValue = float.Parse(GetValueForChannelEncoding(baseCE.Item2, markIndex));
            }

            // Now add onto this the offset value
            if (offsetCE != null)
            {
                if (offsetCE.Item1 != null)
                    initialValue += float.Parse(GetValueForChannelEncoding(offsetCE.Item1, markIndex));
                if (offsetCE.Item2 != null)
                    finalValue += float.Parse(GetValueForChannelEncoding(offsetCE.Item2, markIndex));
            }

            // Now calculate and add the offsetpct. This is based on the size of the mark
            if (offsetpctCE != null)
            {
                GetComponent<MeshFilter>().mesh.RecalculateBounds();
                if (offsetpctCE.Item1 != null)
                {
                    float size;
                    if (sizeCE != null && sizeCE.Item1 != null)
                        size = float.Parse(GetValueForChannelEncoding(sizeCE.Item1, markIndex));
                    else
                        size = GetComponent<MeshFilter>().mesh.bounds.size[dim] * gameObject.transform.localScale[dim] / DxR.Vis.SIZE_UNIT_SCALE_FACTOR;

                    initialValue += (float.Parse(GetValueForChannelEncoding(offsetpctCE.Item1, markIndex)) * Mathf.Abs(size));
                }
                if (offsetpctCE.Item2 != null)
                {
                    float size;
                    if (sizeCE != null && sizeCE.Item2 != null)
                        size = float.Parse(GetValueForChannelEncoding(sizeCE.Item2, markIndex));
                    else
                        size = GetComponent<MeshFilter>().mesh.bounds.size[dim] * gameObject.transform.localScale[dim] / DxR.Vis.SIZE_UNIT_SCALE_FACTOR;

                    finalValue += (float.Parse(GetValueForChannelEncoding(offsetpctCE.Item2, markIndex)) * Mathf.Abs(size));
                }
            }

            // Now add the facet
            if (facetwrapCE != null)
            {
                if (facetwrapCE.Item1 != null)
                {
                    Vector3 facetOffset = Utils.StringToVector3(GetValueForChannelEncoding(facetwrapCE.Item1, markIndex));
                    initialValue += facetOffset[dim];
                }
                if (facetwrapCE.Item2 != null)
                {
                    Vector3 facetOffset = Utils.StringToVector3(GetValueForChannelEncoding(facetwrapCE.Item2, markIndex));
                    finalValue += facetOffset[dim];
                }
            }

            InitialiseTweeners(activeMarkTransition, channel, initialValue.ToString(), finalValue.ToString());
        }

        protected virtual void InitialiseTransitionChannel(ActiveMarkTransition activeMarkTransition, Tuple<ChannelEncoding, ChannelEncoding, string> transitionChannelEncoding)
        {
            ChannelEncoding initialCE = transitionChannelEncoding.Item1;
            ChannelEncoding finalCE = transitionChannelEncoding.Item2;
            string channel = transitionChannelEncoding.Item3;

            // Get the value associated with the initial and final values
            // Iniitalise default starting values first, depending on the data type expected for that given channel
            string initialValue, finalValue = initialValue = GetDefaultValueForChannel(channel);

            // Fill in the values with the actual ones
            if (initialCE != null)
            {
                initialValue = GetValueForChannelEncoding(initialCE, activeMarkTransition.MarkIndex);
            }
            if (finalCE != null)
            {
                finalValue = GetValueForChannelEncoding(finalCE, activeMarkTransition.MarkIndex);
            }

            InitialiseTweeners(activeMarkTransition, channel, initialValue, finalValue);
        }

        protected virtual void InitialiseTweeners(ActiveMarkTransition activeMarkTransition, string channel, string initialValue, string finalValue)
        {
            // We need to rescale our tweening value from the observable based on any staging that is defined, if any
            // We access these values now and then use them in the observable later
            bool tweenRescaled = false;
            float minTween = 0;
            float maxTween = 1;
            // We first check for staging for this channel, then fall back on the generic "encoding" stage (if it is specified by the user)
            if (activeMarkTransition.Stages.TryGetValue(channel, out Tuple<float, float> range) || activeMarkTransition.Stages.TryGetValue("encoding", out range))
            {
                tweenRescaled = true;
                minTween = range.Item1;
                maxTween = range.Item2;
            }

            activeMarkTransition.TweeningObservable.Subscribe(t =>
            {
                string interpolatedValue = "";

                // Rescale the tween value if necessary
                if (tweenRescaled)
                    t = Utils.NormaliseValue(t, minTween, maxTween, 0, 1);

                // We also need to apply an easing function if one has been given
                if (activeMarkTransition.EasingFunction != null)
                {
                    // Only do it if t is inside the accepted ranges, otherwise it returns NaN
                    if (0 <= t && t <= 1)
                        t = activeMarkTransition.EasingFunction(0, 1, t);
                }

                // We need to do this properly depending on the data type that we expect for the given channel
                switch (channel)
                {
                    // Floats
                    case "x": case "y": case "z":
                    case "width": case "height": case "length": case "depth":
                    case "xoffset": case "yoffset": case "zoffset":
                    case "xoffsetpct": case "yoffsetpct": case "zoffsetpct":
                    case "opacity": case "size":
                    case "xrotation": case "yrotation": case "zrotation":
                    case "xdirection": case "ydirection": case "zdirection":
                        {
                            float start = float.Parse(initialValue);
                            float end = float.Parse(finalValue);
                            interpolatedValue = Mathf.Lerp(start, end, t).ToString();
                            break;
                        }

                    // Colour
                    case "color":
                        {
                            Color start, end;
                            ColorUtility.TryParseHtmlString(initialValue, out start);
                            ColorUtility.TryParseHtmlString(finalValue, out end);
                            interpolatedValue = "#" + ColorUtility.ToHtmlStringRGB(Color.Lerp(start, end, t));
                            break;
                        }

                    // N/A
                    case "offsets":
                    case "facetwrap":
                        {
                            Debug.Log(initialValue);
                            throw new Exception(string.Format("Vis Morphs: Channel {0} cannot be interpolated in a transition independently. These must be done as part of an x, y, or z channel.", channel));
                        }
                }

                SetChannelValue(channel, interpolatedValue);
            }).AddTo(activeMarkTransition.Disposable);
        }

        protected string GetDefaultValueForChannel(string channel)
        {
            switch (channel)
            {
                // Floats
                case "x": case "y": case "z":
                case "width": case "height": case "length": case "depth":
                case "xoffset": case "yoffset": case "zoffset":
                case "xoffsetpct": case "yoffsetpct": case "zoffsetpct":
                case "opacity": case "size":
                case "xrotation": case "yrotation": case "zrotation":
                case "xdirection": case "ydirection": case "zdirection":
                    {
                        return "0";
                    }

                // Colour
                case "color":
                    {
                        return "#" + ColorUtility.ToHtmlStringRGB(Color.white);
                    }

                // Vector3
                case "offsets":
                case "facetwrap":
                    {
                        return Vector3.zero.ToString("F3");
                    }

                default:
                    return "";
            }
        }

        #endregion Morphing functions

        #region Channel value functions

        /// <summary>
        /// This is now moved to the marks themselves in order to give further control over how they are used to the Mark script
        /// </summary>
        public virtual void ApplyChannelEncoding(ChannelEncoding channelEncoding, int markIndex)
        {
            string value = GetValueForChannelEncoding(channelEncoding, markIndex);
            SetChannelValue(channelEncoding.channel, value);
        }

        protected virtual string GetValueForChannelEncoding(ChannelEncoding channelEncoding, int markIndex)
        {
            if (channelEncoding.value != DxR.Vis.UNDEFINED)
            {
                return channelEncoding.value;
            }

            // Special condition for offset encodings with linked offsets (for stacked bar charts, etc.)
            if (channelEncoding.IsOffset() && ((OffsetChannelEncoding)channelEncoding).linkedChannel != null)
            {
                OffsetChannelEncoding offsetChannelEncoding = (OffsetChannelEncoding)channelEncoding;
                if (offsetChannelEncoding.values.Count > 0)
                {
                    string channelValue = offsetChannelEncoding.values[markIndex];
                    if (offsetChannelEncoding.scale != null)
                    {
                        channelValue = offsetChannelEncoding.scale.ApplyScale(offsetChannelEncoding.values[markIndex]);
                    }

                    return channelValue;
                }
            }
            // Special condition for facet wrap
            else if (channelEncoding.IsFacetWrap())
            {
                FacetWrapChannelEncoding facetWrapChannelEncoding = (FacetWrapChannelEncoding)channelEncoding;
                if (facetWrapChannelEncoding.translation.Count > 0)
                {
                    return (facetWrapChannelEncoding.translation[markIndex]).ToString("F3");
                }
            }
            else
            {
                string channelValue = channelEncoding.scale.ApplyScale(datum[channelEncoding.field]);
                return channelValue;
            }

            throw new Exception("???");
        }

        public virtual void SetChannelValue(string channel, string value)
        {
            switch(channel)
            {
                case "x":
                    SetLocalPos(value, 0);
                    break;
                case "y":
                    SetLocalPos(value, 1);
                    break;
                case "z":
                    SetLocalPos(value, 2);
                    break;
                 case "width":
                    SetSize(value, 0);
                    break;
                case "height":
                    SetSize(value, 1);
                    break;
                case "length":
                    SetSize(value, GetMaxSizeDimension(forwardDirection));
                    break;
                case "depth":
                    SetSize(value, 2);
                    break;
                case "xoffset":
                    SetOffset(value, 0);
                    break;
                case "yoffset":
                    SetOffset(value, 1);
                    break;
                case "zoffset":
                    SetOffset(value, 2);
                    break;
                case "offsets":
                case "facetwrap":
                    SetOffsets(value);
                    break;
                case "xoffsetpct":
                    SetOffsetPct(value, 0);
                    break;
                case "yoffsetpct":
                    SetOffsetPct(value, 1);
                    break;
                case "zoffsetpct":
                    SetOffsetPct(value, 2);
                    break;
                case "color":
                    SetColor(value);
                    break;
                case "opacity":
                    SetOpacity(value);
                    break;
                case "size":
                    SetMaxSize(value);
                    break;
                case "xrotation":
                    SetRotation(value, 0);
                    break;
                case "yrotation":
                    SetRotation(value, 1);
                    break;
                case "zrotation":
                    SetRotation(value, 2);
                    break;
                case "xdirection":
                    SetDirectionVector(value, 0);
                    break;
                case "ydirection":
                    SetDirectionVector(value, 1);
                    break;
                case "zdirection":
                    SetDirectionVector(value, 2);
                    break;
                default:
                    throw new System.Exception("Cannot find channel: " + channel);
            }
        }

        private int GetMaxSizeDimension(Vector3 direction)
        {
            if( Math.Abs(direction.x) > Math.Abs(direction.y) &&
                Math.Abs(direction.x) > Math.Abs(direction.z) )
            {
                return 0;

            } else if(  Math.Abs(direction.y) > Math.Abs(direction.x) &&
                        Math.Abs(direction.y) > Math.Abs(direction.z))
            {
                return 1;
            }

            return 2;
        }

        private void SetLocalPos(string value, int dim)
        {
            // TODO: Do this more robustly.
            float pos = float.Parse(value) * DxR.Vis.SIZE_UNIT_SCALE_FACTOR;

            Vector3 localPos = gameObject.transform.localPosition;
            localPos[dim] = pos;
            gameObject.transform.localPosition = localPos;
        }

        private void SetSize(string value, int dim)
        {
            float size = float.Parse(value) * DxR.Vis.SIZE_UNIT_SCALE_FACTOR;

            Vector3 initPos = transform.localPosition;

            Vector3 curScale = transform.localScale;

            GetComponent<MeshFilter>().mesh.RecalculateBounds();
            Vector3 origMeshSize = GetComponent<MeshFilter>().mesh.bounds.size;
            curScale[dim] = size / (origMeshSize[dim]);
            transform.localScale = curScale;

            transform.localPosition = initPos;  // This handles models that get translated with scaling.
        }

        private void SetOffset(string value, int dim)
        {
            float offset = float.Parse(value) * DxR.Vis.SIZE_UNIT_SCALE_FACTOR;
            Vector3 translateBy = transform.localPosition;
            translateBy[dim] = offset + translateBy[dim];
            transform.localPosition = translateBy;
        }

        private void SetOffsets(string value)
        {
            if (!(value.StartsWith ("(") && value.EndsWith (")")))
                return;

            // Remove the parentheses
            value = value.Substring(1, value.Length-2);

            // Split the items
            string[] sArray = value.Split(',');

            SetOffset(sArray[0], 0);
            SetOffset(sArray[1], 1);
            SetOffset(sArray[2], 2);
        }

        private void SetOffsetPct(string value, int dim)
        {
            GetComponent<MeshFilter>().mesh.RecalculateBounds();
            float offset = float.Parse(value) * GetComponent<MeshFilter>().mesh.bounds.size[dim] *
                gameObject.transform.localScale[dim];
            Vector3 translateBy = transform.localPosition;
            translateBy[dim] = offset + translateBy[dim];
            transform.localPosition = translateBy;
        }

        private void SetRotation(string value, int dim)
        {
            Vector3 rot = transform.localEulerAngles;
            rot[dim] = float.Parse(value);
            transform.localEulerAngles = rot;
        }

        public void SetMaxSize(string value)
        {
            float size = float.Parse(value) * DxR.Vis.SIZE_UNIT_SCALE_FACTOR;

            Vector3 renderSize = myRenderer.bounds.size;
            Vector3 localScale = gameObject.transform.localScale;

            int maxIndex = 0;
            float maxSize = renderSize[maxIndex];
            for(int i = 1; i < 3; i++)
            {
                if(maxSize < renderSize[i])
                {
                    maxSize = renderSize[i];
                    maxIndex = i;
                }
            }

            float origMaxSize = renderSize[maxIndex] / localScale[maxIndex];
            float newLocalScale = (size / origMaxSize);

            gameObject.transform.localScale = new Vector3(newLocalScale,
                newLocalScale, newLocalScale);
        }

        private void ScaleToMaxDim(string value, int maxDim)
        {
            float size = float.Parse(value) * DxR.Vis.SIZE_UNIT_SCALE_FACTOR;

            Vector3 renderSize = gameObject.transform.GetComponent<Renderer>().bounds.size;
            Vector3 localScale = gameObject.transform.localScale;

            float origMaxSize = renderSize[maxDim] / localScale[maxDim];
            float newLocalScale = (size / origMaxSize);

            gameObject.transform.localScale = new Vector3(newLocalScale,
                newLocalScale, newLocalScale);
        }

        private void SetColor(string value)
        {
            Color color;
            bool colorParsed = ColorUtility.TryParseHtmlString(value, out color);
            if (!colorParsed) return;

            if(myRenderer != null)
            {
                myRenderer.material.color = color;
            } else
            {
                Debug.Log("Cannot set color of mark without renderer object.");
            }
        }

        private void SetOpacity(string value)
        {
            if (myRenderer != null)
            {
                SetRenderModeToTransparent(myRenderer.material);
                Color color = myRenderer.material.color;
                color.a = float.Parse(value);
                myRenderer.material.color = color;
            }
            else
            {
                Debug.Log("Cannot set opacity of mark without renderer object.");
            }
        }

        private void SetRenderModeToTransparent(Material m)
        {
            m.SetFloat("_Mode", 2);
            m.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
            m.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            m.SetInt("_ZWrite", 0);
            m.DisableKeyword("_ALPHATEST_ON");
            m.EnableKeyword("_ALPHABLEND_ON");
            m.DisableKeyword("_ALPHAPREMULTIPLY_ON");
            m.renderQueue = 3000;
        }

        public void SetRotation()
        {
            Quaternion rotation = Quaternion.FromToRotation(forwardDirection, curDirection);
            transform.rotation = rotation;

        }

        /// <summary>
        /// vectorIndex = 0 for x, 1 for y, 2 for z
        /// </summary>
        private void SetDirectionVector(string value, int vectorIndex)
        {
            // Set target direction dim to normalized size.
            Vector3 targetOrient = Vector3.zero;
            targetOrient[vectorIndex] = float.Parse(value);
            //targetOrient.Normalize();

            // Copy coordinate to current orientation and normalize.
            curDirection[vectorIndex] = targetOrient[vectorIndex];
            //curDirection.Normalize();

            // Quaternion rotation = Quaternion.FromToRotation(forwardDirection, curDirection);
            // transform.rotation = rotation;
        }

        #endregion Channel value functions

        #region Inference functions

        public void Infer(Data data, JSONNode specsOrig, out JSONNode specs, string specsFilename)
        {
            specs = specsOrig.Clone();

            // Go through each channel and infer the missing specs.
            foreach (KeyValuePair<string, JSONNode> kvp in specs["encoding"].AsObject)
            {
                ChannelEncoding channelEncoding = new ChannelEncoding();

                // Get minimum required values:
                channelEncoding.channel = kvp.Key;

                // Check validity of channel
                JSONNode channelSpecs = kvp.Value;

                // 1. The channel needs either a value or field specified
                if (channelSpecs["value"] == null && channelSpecs["field"] == null)
                {
                    // Offsets don't need either of these, but they need a "channel" property
                    if (channelEncoding.channel.EndsWith("offset"))
                    {
                        if (channelSpecs["channel"] == null)
                            throw new Exception("Missing channel relation in offset " + channelEncoding.channel);
                    }
                    else
                    {
                        throw new Exception("Missing field in channel " + channelEncoding.channel);
                    }
                }
                // 2. The channel shouldn't specify both a value and field. If so, the field takes precedent and we remove the value
                else if (channelSpecs["value"] != null && channelSpecs["field"] != null)
                {
                    channelSpecs.Remove("value");
                }

                // Additional validity checking and inferencing that is necessary only for fields
                if (channelSpecs["field"] != null)
                {
                    channelEncoding.field = channelSpecs["field"];

                    // 3. The specified field needs to actually be in the data
                    if (!data.fieldNames.Contains(channelEncoding.field))
                    {
                        throw new Exception("Cannot find data field " + channelEncoding.field + " in data. Please check your spelling (case sensitive).");
                    }

                    // 4. There needs to be a type specified for this field
                    if (channelSpecs["type"] == null)
                    {
                        throw new Exception("Missing field data type in channel " + channelEncoding.channel);
                    }

                    channelEncoding.fieldDataType = channelSpecs["type"];

                    // Infer scale specification for this channel where necessary
                    InferScaleSpecsForChannel(ref channelEncoding, ref specs, data);

                    // For spatial channels, infer the specs required for the axes
                    if (channelEncoding.channel == "x" || channelEncoding.channel == "y" || channelEncoding.channel == "z")
                    {
                        InferAxisSpecsForChannel(ref channelEncoding, ref specs, data);
                    }

                    // For these other channels, infer the specs required for the legends
                    if (channelEncoding.channel == "color" || channelEncoding.channel == "size")
                    {
                        InferLegendSpecsForChannel(ref channelEncoding, ref specs);
                    }
                }
            }

            for(int n = 0; n < specs["interaction"].AsArray.Count; n++)
            {
                JSONObject node = specs["interaction"].AsArray[n].AsObject;
                if (node["type"] == null || node["field"] == null)
                {
                    continue;
                    //throw new Exception("Missing type and/or field for interaction specs.");
                } else
                {
                    if(node["domain"] == null)
                    {
                        ChannelEncoding ch = new ChannelEncoding();
                        ch.field = node["field"].Value;

                        // Check validity of data field
                        if (!data.fieldNames.Contains(ch.field))
                        {
                            throw new Exception("Cannot find data field " + ch.field + " in data (check your interaction specs). Please check your spelling (case sensitive).");
                        }

                        ch.channel = "color";

                        switch (node["type"].Value)
                        {
                            case "toggleFilter":
                                ch.fieldDataType = "nominal";
                                break;
                            case "thresholdFilter":
                            case "rangeFilter":
                                ch.fieldDataType = "quantitative";
                                break;
                            default:
                                break;
                        }

                        JSONNode temp = null;
                        InferDomain(ch, temp, ref node, data);
                    }
                }
            }
        }

        private void InferLegendSpecsForChannel(ref ChannelEncoding channelEncoding, ref JSONNode specs)
        {
            string channel = channelEncoding.channel;
            JSONNode channelSpecs = specs["encoding"][channel];
            JSONNode legendSpecs = channelSpecs["legend"];
            if (legendSpecs != null && legendSpecs.Value.ToString() == "none") return;

            JSONObject legendSpecsObj = (legendSpecs == null) ? new JSONObject() : legendSpecs.AsObject;

            if(legendSpecsObj["type"] == null)
            {
                string fieldDataType = channelSpecs["type"].Value.ToString();
                if (fieldDataType == "quantitative" || fieldDataType == "temporal")
                {
                    legendSpecsObj.Add("type", new JSONString("gradient"));
                } else
                {
                    legendSpecsObj.Add("type", new JSONString("symbol"));
                }
            }

            if (legendSpecsObj["filter"] == null)
            {
                legendSpecsObj.Add("filter", new JSONBool(false));
            }

            // TODO: Add proper inference.
            // HACK: For now, always use hard coded options.
            if (legendSpecsObj["gradientWidth"] == null)
            {
                legendSpecsObj.Add("gradientWidth", new JSONNumber(200));
            }

            if (legendSpecsObj["gradientHeight"] == null)
            {
                legendSpecsObj.Add("gradientHeight", new JSONNumber(50));
            }

            if (legendSpecsObj["face"] == null)
            {
                legendSpecsObj.Add("face", new JSONString("front"));
            }

            if (legendSpecsObj["orient"] == null)
            {
                legendSpecsObj.Add("orient", new JSONString("right"));
            }

            if (legendSpecsObj["face"] == null)
            {
                legendSpecsObj.Add("face", new JSONString("front"));
            }

            if (legendSpecsObj["x"] == null)
            {
                legendSpecsObj.Add("x", new JSONNumber(float.Parse(specs["width"].Value.ToString())));
            }

            if (legendSpecsObj["y"] == null)
            {
                legendSpecsObj.Add("y", new JSONNumber(float.Parse(specs["height"].Value.ToString())));
            }

            if (legendSpecsObj["z"] == null)
            {
                legendSpecsObj.Add("z", new JSONNumber(0));
            }

            if (legendSpecsObj["title"] == null)
            {
                legendSpecsObj.Add("title", new JSONString("Legend: " + channelSpecs["field"]));
            }

            specs["encoding"][channelEncoding.channel].Add("legend", legendSpecsObj);
        }

        int GetNumDecimalPlaces(float val)
        {
            return val.ToString().Length - val.ToString().IndexOf(".") - 1;
        }

        private void InferAxisSpecsForChannel(ref ChannelEncoding channelEncoding, ref JSONNode specs, Data data)
        {
            string channel = channelEncoding.channel;
            JSONNode channelSpecs = specs["encoding"][channel];
            JSONNode axisSpecs = channelSpecs["axis"];
            if (axisSpecs != null && axisSpecs.Value.ToString() == "none") return;

            JSONObject axisSpecsObj = (axisSpecs == null) ? new JSONObject() : axisSpecs.AsObject;

            if (axisSpecsObj["filter"] == null)
            {
                axisSpecsObj.Add("filter", new JSONBool(false));
            }

            if (axisSpecsObj["face"] == null)
            {
                if(channel == "x" || channel == "y")
                {
                    axisSpecsObj.Add("face", new JSONString("front"));
                } else if(channel == "z")
                {
                    axisSpecsObj.Add("face", new JSONString("left"));
                }
            }

            if (axisSpecsObj["orient"] == null)
            {
                if (channel == "x" || channel == "z")
                {
                    axisSpecsObj.Add("orient", new JSONString("bottom"));
                }
                else if (channel == "y")
                {
                    axisSpecsObj.Add("orient", new JSONString("left"));
                }
            }

            if(axisSpecsObj["title"] == null)
            {
                axisSpecsObj.Add("title", new JSONString(channelEncoding.field));
            }

            if(axisSpecsObj["length"] == null)
            {
                float axisLength = 0.0f;
                switch (channelEncoding.channel)
                {
                    case "x":
                    //case "width":
                        axisLength = specs["width"].AsFloat;
                        break;
                    case "y":
                    //case "height":
                        axisLength = specs["height"].AsFloat;
                        break;
                    case "z":
                    //case "depth":
                        axisLength = specs["depth"].AsFloat;
                        break;
                    default:
                        axisLength = 0.0f;
                        break;
                }

                axisSpecsObj.Add("length", new JSONNumber(axisLength));
            }

            if (axisSpecs["color"] == null)
            {
                axisSpecsObj.Add("color", new JSONString("#bebebe"));
            }
                /*
                if(axisSpecs["color"] == null)
                {
                    string color = "";
                    switch (channelEncoding.channel)
                    {
                        case "x":
                            color = "#ff0000";
                            break;
                        case "y":
                            color = "#00ff00";
                            break;
                        case "z":
                            color = "#0000ff";
                            break;
                        default:
                            break;
                    }

                    axisSpecsObj.Add("color", new JSONString(color));
                }
                */

                if (axisSpecsObj["grid"] == null)
            {
                axisSpecsObj.Add("grid", new JSONBool(false));
            }

            if(axisSpecs["ticks"] == null)
            {
                axisSpecsObj.Add("ticks", new JSONBool(true));
            }

            if(axisSpecsObj["values"] == null)
            {
                JSONArray tickValues = new JSONArray();
                JSONNode domain = specs["encoding"][channelEncoding.channel]["scale"]["domain"];
                JSONNode values = channelEncoding.fieldDataType == "quantitative" ? new JSONArray() : domain;

                if (channelEncoding.fieldDataType == "quantitative" &&
                    (channel == "x" || channel == "y" || channel == "z"))
                {
                    // Round domain into a nice number.
                    //float maxDomain = RoundNice(domain.AsArray[1].AsFloat - domain.AsArray[0].AsFloat);

                    int numDecimals = Math.Max(GetNumDecimalPlaces(domain.AsArray[0].AsFloat), GetNumDecimalPlaces(domain.AsArray[1].AsFloat));
                    //Debug.Log("NUM DEC " + numDecimals);
                    // Add number of ticks.
                    int defaultNumTicks = 6;
                    int numTicks = axisSpecsObj["tickCount"] == null ? defaultNumTicks : axisSpecsObj["tickCount"].AsInt;
                    float intervals = Math.Abs(domain.AsArray[1].AsFloat - domain.AsArray[0].AsFloat) / (numTicks - 1.0f);


                    for (int i = 0; i < numTicks; i++)
                    {
                        float tickVal = (float)Math.Round(domain.AsArray[0].AsFloat + (intervals * (float)(i)), numDecimals);
                        //Debug.Log(tickVal);
                        values.Add(new JSONString(tickVal.ToString()));
                    }
                }

                axisSpecsObj.Add("values", values.AsArray);
            }

            if (axisSpecsObj["tickCount"] == null)
            {
                axisSpecsObj.Add("tickCount", new JSONNumber(axisSpecsObj["values"].Count));
            }

            if(axisSpecsObj["labels"] == null)
            {
                axisSpecsObj.Add("labels", new JSONBool(true));
            }

            specs["encoding"][channelEncoding.channel].Add("axis", axisSpecsObj);
        }

        private float RoundNice(float num)
        {
            float[] roundNumbersArray = { 0.5f, 5.0f, 50.0f };
            List<float> roundNumbers = new List<float>(roundNumbersArray);

            float multiplier = 1.0f;

            while(true)
            {
                for (int i = 0; i < roundNumbers.Count; i++)
                {
                    if (roundNumbers[i] * multiplier >= num)
                    {
                        return roundNumbers[i] * multiplier;
                    }
                }

                multiplier = multiplier + 1.0f;
            }
        }


        // TODO: Expose this so it is very easy to add mark-specific rules.
        private void InferMarkSpecificSpecs(ref JSONNode specs)
        {
            if(markName == "bar" || markName == "rect")
            {
                // Set size of bar or rect along dimension for type band or point.


                if (specs["encoding"]["x"] != null && specs["encoding"]["width"] == null &&
                    specs["encoding"]["x"]["scale"]["type"] == "band")
                {
                    float bandwidth = ScaleBand.ComputeBandSize(specs["encoding"]["x"]["scale"]);
                    JSONObject forceSizeValueObj = new JSONObject();
                    forceSizeValueObj.Add("value", new JSONNumber(bandwidth.ToString()));
                    specs["encoding"].Add("width", forceSizeValueObj);
                }

                if (specs["encoding"]["y"] != null && specs["encoding"]["height"] == null &&
                    specs["encoding"]["y"]["scale"]["type"] == "band")
                {
                    float bandwidth = ScaleBand.ComputeBandSize(specs["encoding"]["y"]["scale"]);
                    JSONObject forceSizeValueObj = new JSONObject();
                    forceSizeValueObj.Add("value", new JSONNumber(bandwidth.ToString()));
                    specs["encoding"].Add("height", forceSizeValueObj);
                }

                if (specs["encoding"]["z"] != null && specs["encoding"]["depth"] == null &&
                    specs["encoding"]["z"]["scale"]["type"] == "band")
                {
                    float bandwidth = ScaleBand.ComputeBandSize(specs["encoding"]["z"]["scale"]);
                    JSONObject forceSizeValueObj = new JSONObject();
                    forceSizeValueObj.Add("value", new JSONNumber(bandwidth.ToString()));
                    specs["encoding"].Add("depth", forceSizeValueObj);
                }

                if (specs["encoding"]["width"] != null && specs["encoding"]["width"]["value"] == null &&
                    specs["encoding"]["width"]["type"] == "quantitative" && specs["encoding"]["xoffsetpct"] == null)
                {
                    JSONObject forceSizeValueObj = new JSONObject();
                    forceSizeValueObj.Add("value", new JSONNumber("0.5"));
                    specs["encoding"].Add("xoffsetpct", forceSizeValueObj);
                }

                if (specs["encoding"]["height"] != null && specs["encoding"]["height"]["value"] == null &&
                    specs["encoding"]["height"]["type"] == "quantitative" && specs["encoding"]["yoffsetpct"] == null)
                {
                    JSONObject forceSizeValueObj = new JSONObject();
                    forceSizeValueObj.Add("value", new JSONNumber("0.5"));
                    specs["encoding"].Add("yoffsetpct", forceSizeValueObj);
                }

                if (specs["encoding"]["depth"] != null && specs["encoding"]["depth"]["value"] == null &&
                   specs["encoding"]["depth"]["type"] == "quantitative" && specs["encoding"]["zoffsetpct"] == null)
                {
                    JSONObject forceSizeValueObj = new JSONObject();
                    forceSizeValueObj.Add("value", new JSONNumber("0.5"));
                    specs["encoding"].Add("zoffsetpct", forceSizeValueObj);
                }
            }
        }

        private void InferScaleSpecsForChannel(ref ChannelEncoding channelEncoding, ref JSONNode specs, Data data)
        {
            JSONNode channelSpecs = specs["encoding"][channelEncoding.channel];
            JSONNode scaleSpecs = channelSpecs["scale"];
            JSONObject scaleSpecsObj = (scaleSpecs == null) ? new JSONObject() : scaleSpecs.AsObject;

            if (scaleSpecs["type"] == null)
            {
                InferScaleType(channelEncoding.channel, channelEncoding.fieldDataType, ref scaleSpecsObj);
            }

            if (!(scaleSpecsObj["type"].Value.ToString() == "none"))
            {
                if (scaleSpecs["domain"] == null)
                {
                    InferDomain(channelEncoding, specs, ref scaleSpecsObj, data);
                }

                if (scaleSpecs["padding"] != null)
                {
                    scaleSpecsObj.Add("paddingInner", scaleSpecs["padding"]);
                    scaleSpecsObj.Add("paddingOuter", scaleSpecs["padding"]);
                }
                else
                {
                    scaleSpecsObj.Add("padding", new JSONString(ScalePoint.PADDING_DEFAULT.ToString()));
                }

                if (scaleSpecs["range"] == null)
                {
                    InferRange(channelEncoding, specs, ref scaleSpecsObj);
                }

                if (channelEncoding.channel == "color" && !scaleSpecsObj["range"].IsArray && scaleSpecsObj["scheme"] == null)
                {
                    InferColorScheme(channelEncoding, ref scaleSpecsObj);
                }

                // HACKY WORKAROUND: Even though facetwrap doesn't actually need colour scheme, we do this anyway to prevent errors
                if (channelEncoding.channel == "facetwrap" && !scaleSpecsObj["range"].IsArray && scaleSpecsObj["scheme"] == null)
                {
                    InferColorScheme(channelEncoding, ref scaleSpecsObj);
                }
            }

            specs["encoding"][channelEncoding.channel].Add("scale", scaleSpecsObj);
        }

        private void InferColorScheme(ChannelEncoding channelEncoding, ref JSONObject scaleSpecsObj)
        {
            string range = scaleSpecsObj["range"].Value.ToString();
            string scheme = "";
            if (range == "category")
            {
                if(scaleSpecsObj["domain"].AsArray.Count <= 10)
                {
                    scheme = "tableau10";
                } else
                {
                    scheme = "tableau20";
                }
            } else if(range == "ordinal" || range == "ramp")
            {
                //scheme = "blues";
                scheme = "ramp";
            } else if(range == "heatmap")
            {
                scheme = "viridis";
            } else
            {
                throw new Exception("Cannot infer color scheme for range " + range);
            }

            scaleSpecsObj.Add("scheme", new JSONString(scheme));
        }

        // TODO: Fix range computation to consider paddingOUter!!!
        // TODO: Fix range size.
        private void InferRange(ChannelEncoding channelEncoding, JSONNode specs, ref JSONObject scaleSpecsObj)
        {
            JSONArray range = new JSONArray();

            string channel = channelEncoding.channel;
            if (channel == "x" || channel == "width")
            {
                range.Add(new JSONString("0"));

                if (scaleSpecsObj["rangeStep"] == null)
                {
                    range.Add(new JSONString(specs["width"]));
                } else
                {
                    float rangeSize = float.Parse(scaleSpecsObj["rangeStep"]) * (float)scaleSpecsObj["domain"].Count;
                    range.Add(new JSONString(rangeSize.ToString()));
                    specs["width"] = rangeSize;
                }

            } else if (channel == "y" || channel == "height")
            {
                range.Add(new JSONString("0"));
                if (scaleSpecsObj["rangeStep"] == null)
                {
                    range.Add(new JSONString(specs["height"]));
                }
                else
                {
                    float rangeSize = float.Parse(scaleSpecsObj["rangeStep"]) * (float)scaleSpecsObj["domain"].Count;
                    range.Add(new JSONString(rangeSize.ToString()));
                    specs["height"] = rangeSize;
                }
            } else if (channel == "z" || channel == "depth")
            {
                range.Add(new JSONString("0"));
                if (scaleSpecsObj["rangeStep"] == null)
                {
                    range.Add(new JSONString(specs["depth"]));
                }
                else
                {
                    float rangeSize = float.Parse(scaleSpecsObj["rangeStep"]) * (float)scaleSpecsObj["domain"].Count;
                    range.Add(new JSONString(rangeSize.ToString()));
                    specs["depth"] = rangeSize;
                }
            } else if (channel == "opacity")
            {
                range.Add(new JSONString("0"));
                range.Add(new JSONString("1"));
            } else if (channel == "size" || channel == "length")
            {
                range.Add(new JSONString("0"));
                string maxDimSize = Math.Max(Math.Max(specs["width"].AsFloat, specs["height"].AsFloat),
                    specs["depth"].AsFloat).ToString();

                range.Add(new JSONString(maxDimSize));

            } else if(channel == "color")
            {
                if(channelEncoding.fieldDataType == "nominal")
                {
                    scaleSpecsObj.Add("range", new JSONString("category"));
                }
                else if (channelEncoding.fieldDataType == "ordinal")
                {
                    scaleSpecsObj.Add("range", new JSONString("ordinal"));
                }
                else if (channelEncoding.fieldDataType == "quantitative" ||
                    channelEncoding.fieldDataType == "temporal")
                {
                    scaleSpecsObj.Add("range", new JSONString("ramp"));
                }

            } else if(channel == "shape")
            {
                range.Add(new JSONString("symbol"));
                throw new Exception("Not implemented yet.");
            } else if(channel == "xrotation" || channel == "yrotation" || channel == "zrotation")
            {
                range.Add(new JSONString("0"));
                range.Add(new JSONString("360"));
            }
            else if (channel == "xdirection" || channel == "ydirection" || channel == "zdirection")
            {
                range.Add(new JSONString("0"));
                range.Add(new JSONString("1"));
            }
            else if (channel == "facetwrap")
            {
                if(channelEncoding.fieldDataType == "nominal")
                {
                    scaleSpecsObj.Add("range", new JSONString("category"));
                }
                else if (channelEncoding.fieldDataType == "ordinal")
                {
                    scaleSpecsObj.Add("range", new JSONString("ordinal"));
                }
                else if (channelEncoding.fieldDataType == "quantitative" ||
                    channelEncoding.fieldDataType == "temporal")
                {
                    scaleSpecsObj.Add("range", new JSONString("ramp"));
                }
            }

            if (range.Count > 0)
            {
                scaleSpecsObj.Add("range", range);
            }
        }

        private void InferDomain(ChannelEncoding channelEncoding, JSONNode specs, ref JSONObject scaleSpecsObj, Data data)
        {
            string sortType = "ascending";
            if(specs != null && specs["encoding"][channelEncoding.channel]["sort"] != null)
            {
                sortType = specs["encoding"][channelEncoding.channel]["sort"].Value.ToString();
            }

            string channel = channelEncoding.channel;
            JSONArray domain = new JSONArray();
            if (channelEncoding.fieldDataType == "quantitative" &&
                (channel == "x" || channel == "y" || channel == "z" ||
                channel == "width" || channel == "height" || channel == "depth" || channel == "length" ||
                channel == "color" || channel == "xrotation" || channel == "yrotation"
                || channel == "zrotation" || channel == "size" || channel == "xdirection")
                || channel == "ydirection" || channel == "zdirection" || channel == "opacity")
            {
                List<float> minMax = new List<float>();
                GetExtent(data, channelEncoding.field, ref minMax);

                /*
                // For positive minimum values, set the baseline to zero.
                // TODO: Handle logarithmic scale with undefined 0 value.
                if(minMax[0] >= 0)
                {
                    minMax[0] = 0;
                }

                float roundedMaxDomain = RoundNice(minMax[1] - minMax[0]);
                */

                if (sortType == "none" || sortType == "ascending")
                {
                    //domain.Add(new JSONString(minMax[0].ToString()));
                    domain.Add(new JSONString("0"));
                    domain.Add(new JSONString(minMax[1].ToString()));
                } else
                {
                    domain.Add(new JSONString(minMax[1].ToString()));
                    domain.Add(new JSONString("0"));
                    //domain.Add(new JSONString(minMax[0].ToString()));
                }
            }
            else if (channelEncoding.fieldDataType == "spatial")
            {
                string field = channelEncoding.field.ToLower();

                if (field == "longitude")
                {
                    var flattened = data.polygons.SelectMany(x => x).SelectMany(x => x).Select(x => x.Longitude);
                    domain.Add(new JSONString(flattened.Min().ToString()));
                    domain.Add(new JSONString(flattened.Max().ToString()));
                }
                else if (field == "latitude")
                {
                    var flattened = data.polygons.SelectMany(x => x).SelectMany(x => x).Select(x => x.Latitude);
                    domain.Add(new JSONString(flattened.Min().ToString()));
                    domain.Add(new JSONString(flattened.Max().ToString()));
                }
            }
            else
            {
                List<string> uniqueValues = new List<string>();
                GetUniqueValues(data, channelEncoding.field, ref uniqueValues);

                if (sortType == "ascending")
                {
                    uniqueValues.Sort();
                }
                else if(sortType == "descending")
                {
                    uniqueValues.Sort();
                    uniqueValues.Reverse();
                }

                foreach (string val in uniqueValues)
                {
                    domain.Add(val);
                }
            }

            scaleSpecsObj.Add("domain", domain);
        }

        private void GetUniqueValues(Data data, string field, ref List<string> uniqueValues)
        {
            foreach (Dictionary<string, string> dataValue in data.values)
            {
                string val = dataValue[field];
                if (!uniqueValues.Contains(val))
                {
                    uniqueValues.Add(val);
                }
            }
        }

        private void GetExtent(Data data, string field, ref List<float> extent)
        {
            float min = float.Parse(data.values[0][field]);
            float max = min;
            foreach (Dictionary<string, string> dataValue in data.values)
            {
                float val = float.Parse(dataValue[field]);
                if(val < min)
                {
                    min = val;
                }

                if(val > max)
                {
                    max = val;
                }
            }

            extent.Add(min);
            extent.Add(max);
        }

        private void InferScaleType(string channel, string fieldDataType, ref JSONObject scaleSpecsObj)
        {
            string type = "";
            if (channel == "x" || channel == "y" || channel == "z" ||
                channel == "size" || channel == "opacity")
            {
                if (fieldDataType == "nominal" || fieldDataType == "ordinal")
                {
                    type = "point";
                }
                else if (fieldDataType == "quantitative")
                {
                    type = "linear";
                }
                else if (fieldDataType == "temporal")
                {
                    type = "time";
                }
                else if (fieldDataType == "spatial")
                {
                    type = "spatial";
                }
                else
                {
                    throw new Exception("Invalid field data type: " + fieldDataType);
                }
            } else if (channel == "width" || channel == "height" || channel == "depth" || channel == "length")
            {
                if (fieldDataType == "nominal" || fieldDataType == "ordinal")
                {
                    type = "point";
                }
                else if (fieldDataType == "quantitative")
                {
                    type = "linear";
                }
                else if (fieldDataType == "temporal")
                {
                    type = "time";
                }
                else if (fieldDataType == "spatial")
                {
                    type = "spatial";
                }
                else
                {
                    throw new Exception("Invalid field data type: " + fieldDataType);
                }
            } else if (channel == "xrotation" || channel == "yrotation" || channel == "zrotation"
                    || channel == "xdirection" || channel == "ydirection" || channel == "zdirection")
            {
                if (fieldDataType == "nominal" || fieldDataType == "ordinal")
                {
                    type = "point";
                }
                else if (fieldDataType == "quantitative")
                {
                    type = "linear";
                }
                else if (fieldDataType == "temporal")
                {
                    type = "time";
                }
                else
                {
                    throw new Exception("Invalid field data type: " + fieldDataType);
                }
            } else if (channel == "color")
            {
                if (fieldDataType == "nominal" || fieldDataType == "ordinal")
                {
                    type = "ordinal";
                }
                else if (fieldDataType == "quantitative" || fieldDataType == "temporal")
                {
                    type = "sequential";
                }
                else
                {
                    throw new Exception("Invalid field data type: " + fieldDataType);
                }
            } else if (channel == "shape")
            {
                if (fieldDataType == "nominal" || fieldDataType == "ordinal")
                {
                    type = "ordinal";
                }
                else
                {
                    throw new Exception("Invalid field data type: " + fieldDataType + " for shape channel.");
                }
            } else if (channel == "facetwrap")
            {
                if (fieldDataType == "nominal" || fieldDataType == "ordinal")
                {
                    type = "ordinal";
                }
                else if (fieldDataType == "quantitative" || fieldDataType == "temporal")
                {
                    type = "sequential";
                }
                else
                {
                    throw new Exception("Invalid field data type: " + fieldDataType + " for shape channel.");
                }
            } else
            {
                type = "none";
            }

            scaleSpecsObj.Add("type", new JSONString(type));
        }

        private void WriteStringToFile(string str, string outputName)
        {
            System.IO.File.WriteAllText(outputName, str);
        }

        #endregion Inference functions

        #region Interaction functions

        public void InitTooltip(ref GameObject tooltipObject)
        {
            if (myRenderer != null)
            {
                DxR.GazeResponder sc = gameObject.AddComponent(typeof(DxR.GazeResponder)) as DxR.GazeResponder;
                tooltip = tooltipObject;
            }
        }

        public void SetTooltipField(string dataField)
        {
            //tooltipDataField = dataField;
        }

        public void OnFocusEnter()
        {
            if(tooltip != null)
            {
                tooltip.SetActive(true);

                Vector3 markPos = gameObject.transform.localPosition;

                string datumTooltipString = BuildTooltipString();
                float tooltipXOffset = 0.05f;
                float tooltipZOffset = -0.05f;
                tooltip.GetComponent<Tooltip>().SetText(datumTooltipString);
                tooltip.GetComponent<Tooltip>().SetLocalPos(markPos.x + tooltipXOffset, 0);
                tooltip.GetComponent<Tooltip>().SetLocalPos(markPos.y, 1);
                tooltip.GetComponent<Tooltip>().SetLocalPos(markPos.z + tooltipZOffset, 2);
            }
        }

        private string BuildTooltipString()
        {
            string output = "";

            foreach (KeyValuePair<string, string> entry in datum)
            {
                // do something with entry.Value or entry.Key
                output = output + entry.Key + ": " + entry.Value + "\n";
            }

            return output;
        }

        public void OnFocusExit()
        {
            if (tooltip != null)
            {
                tooltip.SetActive(false);
            }
        }

        #endregion Interaction functions
    }
}
