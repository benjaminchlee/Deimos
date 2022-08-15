using System;
using System.Collections;
using System.Collections.Generic;
using SimpleJSON;
using UnityEngine;
using DxR.VisMorphs;
using UniRx;
using TMPro;
using static DxR.VisMorphs.EasingFunction;

namespace DxR
{
    public class Axis : MonoBehaviour
    {
        private readonly float meshLength = 2.0f;    // This is the initial length of the cylinder used for the axis.
        private float titleOffset = 0.075f;
        private float tickLabelOffset = 0.03f;

        private Interactions interactionsObject = null;
        private string dataField = "";
        private char facingDirection;

        private GameObject title;
        private GameObject axisLine;
        private GameObject sliderBar;
        private GameObject ticksHolder;
        private GameObject tickPrefab;
        private TextMeshPro titleTextMesh;
        private List<GameObject> ticks = new List<GameObject>();

        // An axis can onlyfeasibly have a single transition applied to it at a time
        private ActiveAxisTransition activeAxisTransition;

        private class ActiveAxisTransition
        {
            public string Name;
            public IObservable<float> TweeningObservable;
            public Function EasingFunction;
            public Dictionary<string, Tuple<float, float>> Stages;
            public string Channel;
            public CompositeDisposable Disposable;
            public JSONNode InitialAxisSpecs;
            public JSONNode FinalAxisSpecs;
            public Scale InitialScale;
            public Scale FinalScale;
            public Vector3 InitialTranslation;
            public Vector3 FinalTranslation;
            public Quaternion InitialRotate;
            public Quaternion FinalRotate;

            public ActiveAxisTransition(ActiveTransition activeTransition, string channel, CompositeDisposable disposable,
                                        JSONNode initialAxisSpecs, JSONNode finalAxisSpecs, Scale initialScale, Scale finalScale,
                                        Vector3 initialTranslation, Vector3 finalTranslation,
                                        Quaternion initialRotate, Quaternion finalRotate)
            {
                this.Name = activeTransition.Name;
                this.TweeningObservable = activeTransition.TweeningObservable;
                this.EasingFunction = activeTransition.EasingFunction;
                this.Stages = activeTransition.Stages;
                this.Channel = channel;
                this.Disposable = disposable;
                this.InitialAxisSpecs = initialAxisSpecs;
                this.FinalAxisSpecs = finalAxisSpecs;
                this.InitialScale = initialScale;
                this.FinalScale = finalScale;
                this.InitialTranslation = initialTranslation;
                this.FinalTranslation = finalTranslation;
                this.InitialTranslation = initialTranslation;
                this.FinalTranslation = finalTranslation;
                this.InitialRotate = initialRotate;
                this.FinalRotate = finalRotate;
            }
        }


        #region Morphing functions

        public void InitialiseTransition(ActiveTransition newActiveTransition, string channel, JSONNode initialAxisSpecs, JSONNode finalAxisSpecs, Scale initialScale, Scale finalScale)
        {
            InitialiseTransition(newActiveTransition, channel, initialAxisSpecs, finalAxisSpecs, initialScale, finalScale, Vector3.zero, Vector3.zero, Quaternion.identity, Quaternion.identity);
        }

        public void InitialiseTransition(ActiveTransition newActiveTransition, string channel, JSONNode initialAxisSpecs, JSONNode finalAxisSpecs, Scale initialScale, Scale finalScale, Vector3 initialTranslation, Vector3 finalTranslation, Quaternion initialRotate, Quaternion finalRotate)
        {
            if (activeAxisTransition != null)
                throw new Exception(string.Format("Vis Morphs: Axis is already undergoing a transition \"{0}\" and cannot apply transition \"{1}\".", activeAxisTransition.Name,  newActiveTransition.Name));

            // Create the disposable which will be used to store and quickly unsubscribe from our tween
            // We will set up separate subscriptions for each tweener
            CompositeDisposable transitionDisposable = new CompositeDisposable();

            activeAxisTransition = new ActiveAxisTransition(newActiveTransition, channel, transitionDisposable, initialAxisSpecs, finalAxisSpecs, initialScale, finalScale, initialTranslation, finalTranslation, initialRotate, finalRotate);

            InitialiseTweeners(activeAxisTransition);
        }

        /// <summary>
        /// Returns false if the axis is to be deleted at the end of this function
        /// </summary>
        public bool StopTransition(string transitionName, bool goToEnd, bool isFacetAxis)
        {
            if (activeAxisTransition == null)
                return false;
                // throw new Exception("Vis Morphs: Axis cannot stop the transition as there is no transition to begin with.");

            if (activeAxisTransition.Name != transitionName)
                return false;
                // throw new Exception(string.Format("Vis Morphs: Axis is already undergoing transition {0}, and therefore cannot stop transition {1}.", activeAxisTransition.Name, transitionName));

            // Dispose all active subscriptions
            activeAxisTransition.Disposable.Dispose();

            // Move the axis to either the start or the end
            JSONNode targetAxisSpecs = goToEnd ? activeAxisTransition.FinalAxisSpecs : activeAxisTransition.InitialAxisSpecs;
            Scale targetScale = goToEnd ? activeAxisTransition.FinalScale : activeAxisTransition.InitialScale;
            Vector3 targetTranslation = goToEnd ? activeAxisTransition.FinalTranslation : activeAxisTransition.InitialTranslation;
            Quaternion targetRotate = goToEnd ? activeAxisTransition.FinalRotate : activeAxisTransition.InitialRotate;

            // We only keep the axis if the target state actually exists
            // We also make sure to only keep faceted axes if there's still a facetwrap (i.e., a translation)
            if (targetAxisSpecs != null && (!isFacetAxis || (isFacetAxis && targetTranslation != Vector3.zero)))
            {
                UpdateSpecs(targetAxisSpecs, targetScale);

                // Set the position and rotation of the axis based off any given rotation
                SetRotate(targetRotate);
                SetTranslation(targetTranslation);

                activeAxisTransition = null;
                return false;
            }
            else
            {
                Destroy(gameObject);
                return true;
            }
        }

        private void InitialiseTweeners(ActiveAxisTransition activeAxisTransition)
        {
            // We have separate tweeners for each different component of the Axis. However, we might need to rescale the tweening value
            // if this Axis' channel involves staging. Therefore, we calculate it as a new observable and use it as our new tweening observable
            // We first check for staging for this channel, then fall back on the generic "encoding" stage (if it is specified by the user)
            if (activeAxisTransition.Stages.TryGetValue(activeAxisTransition.Channel, out Tuple<float, float> range) || activeAxisTransition.Stages.TryGetValue("encoding", out range))
            {
                float minTween = range.Item1;
                float maxTween = range.Item2;

                activeAxisTransition.TweeningObservable = activeAxisTransition.TweeningObservable.Select(t =>
                {
                    return Utils.NormaliseValue(t, minTween, maxTween, 0, 1);
                });
            }

            // We extend this observable again if there is an easing function defined
            if (activeAxisTransition.EasingFunction != null)
            {
                activeAxisTransition.TweeningObservable = activeAxisTransition.TweeningObservable.Select(t =>
                {
                    // Only do it if t is inside the accepted ranges, otherwise it returns NaN
                    if (0 <= t && t <= 1)
                        return activeAxisTransition.EasingFunction(0, 1, t);
                    return t;
                });
            }

            InitialiseTitleTweener(activeAxisTransition);

            float initialLength, finalLength;
            InitialiseLengthTweener(activeAxisTransition, out initialLength, out finalLength);

            InitialisePositionAndRotationTweener(activeAxisTransition, initialLength, finalLength);

            InitialiseColourTweener(activeAxisTransition);

            InitialiseTicksTweener(activeAxisTransition);
        }

        private void InitialiseTitleTweener(ActiveAxisTransition activeAxisTransition)
        {
            JSONNode initialAxisSpecs = activeAxisTransition.InitialAxisSpecs;
            JSONNode finalAxisSpecs = activeAxisTransition.FinalAxisSpecs;
            Scale initialScale = activeAxisTransition.InitialScale;
            Scale finalScale = activeAxisTransition.FinalScale;

            // Get the titles
            string initialTitle = "";
            if (initialAxisSpecs != null)
            {
                initialTitle = initialAxisSpecs["title"];
            }

            string finalTitle = "";
            if (finalAxisSpecs != null)
            {
                finalTitle = finalAxisSpecs["title"];
            }

            // If they are both the same, we just use either one. If they are different, we style it as "initial <-> final"
            string title = "";
            if (initialTitle == finalTitle)
            {
                title = initialTitle;
            }
            else
            {
                if (initialTitle == "" && finalTitle != "")
                    title = finalTitle;
                else if (initialTitle != "" && finalTitle == "")
                    title = initialTitle;
                else
                    title = initialTitle + " <-> " + finalTitle;
            }

            // Set the title immediately
            SetTitle(title);

            // Get the title paddings
            float initialTitlePadding = 0.075f;
            if (initialAxisSpecs != null && initialAxisSpecs["titlePadding"] != null)
            {
                initialTitlePadding = initialAxisSpecs["titlePadding"].AsFloat;
            }

            float finalTitlePadding = 0.075f;
            if (finalAxisSpecs != null && finalAxisSpecs["titlePadding"] != null)
            {
                finalTitlePadding = finalAxisSpecs["titlePadding"].AsFloat;
            }

            // Only tween the title paddings if they have changed
            if (initialTitlePadding != finalTitlePadding)
            {
                activeAxisTransition.TweeningObservable.Subscribe(t =>
                {
                    // Interpolate and set padding
                    float interpolatedTitlePadding = Mathf.Lerp(initialTitlePadding, finalTitlePadding, t);
                    SetTitlePadding(interpolatedTitlePadding);
                    CentreTitle();
                }).AddTo(activeAxisTransition.Disposable);
            }
        }

        private void InitialiseLengthTweener(ActiveAxisTransition activeAxisTransition, out float initialLength, out float finalLength)
        {
            JSONNode initialAxisSpecs = activeAxisTransition.InitialAxisSpecs;
            JSONNode finalAxisSpecs = activeAxisTransition.FinalAxisSpecs;
            Scale initialScale = activeAxisTransition.InitialScale;
            Scale finalScale = activeAxisTransition.FinalScale;

            // Get the inital and final lengths
            initialLength = 0;
            if (initialAxisSpecs != null && initialAxisSpecs["length"] != null)
            {
                initialLength = initialAxisSpecs["length"].AsFloat;
            }

            finalLength = 0;
            if (finalAxisSpecs != null && finalAxisSpecs["length"] != null)
            {
                finalLength = finalAxisSpecs["length"].AsFloat;
            }

            // Tween only if the lengths have changed
            if (initialLength != finalLength)
            {
                float _initialLength = initialLength;
                float _finalLength = finalLength;
                activeAxisTransition.TweeningObservable.Subscribe(t =>
                {
                    // Interpolate and set lengths
                    float interpolatedLength = Mathf.Lerp(_initialLength, _finalLength, t);
                    SetLength(interpolatedLength);
                }).AddTo(activeAxisTransition.Disposable);
            }
        }

        private void InitialisePositionAndRotationTweener(ActiveAxisTransition activeAxisTransition, float initialLength, float finalLength)
        {
            JSONNode initialAxisSpecs = activeAxisTransition.InitialAxisSpecs;
            JSONNode finalAxisSpecs = activeAxisTransition.FinalAxisSpecs;
            Scale initialScale = activeAxisTransition.InitialScale;
            Scale finalScale = activeAxisTransition.FinalScale;

            Vector3 initialTranslation = activeAxisTransition.InitialTranslation;
            Vector3 finalTranslation = activeAxisTransition.FinalTranslation;
            Quaternion initialRotate = activeAxisTransition.InitialRotate;
            Quaternion finalRotate = activeAxisTransition.FinalRotate;

            // Get the initial and final positions (this is based on the lengths calculated in InitialiseLengthTweener())
            Vector3 initialPosition = GetAxisPosition(facingDirection, initialLength);
            Vector3 finalPosition = GetAxisPosition(facingDirection, finalLength);
            Quaternion initialRotation = GetAxisRotation(facingDirection);
            Quaternion finalRotation = initialRotation;

            // Apply rotation around the inital and final positions and rotations
            initialPosition = initialRotate * initialPosition;
            finalPosition = finalRotate * finalPosition;
            initialRotation = initialRotate * initialRotation;
            finalRotation = finalRotate * finalRotation;

            // Add the translations due to facetwraps
            initialPosition += initialTranslation;
            finalPosition += finalTranslation;

            if (initialPosition != finalPosition)
            {
                activeAxisTransition.TweeningObservable.Subscribe(t =>
                {
                    // Interpolate and set local position
                    Vector3 interpolatedLocalPosition = Vector3.Lerp(initialPosition, finalPosition, t);
                    gameObject.transform.localPosition = interpolatedLocalPosition * DxR.Vis.SIZE_UNIT_SCALE_FACTOR;
                    // Interpolate and set local rotation
                    Quaternion interpolatedRotation = Quaternion.Lerp(initialRotation, finalRotation, t);
                    gameObject.transform.localRotation = interpolatedRotation;
                }).AddTo(activeAxisTransition.Disposable);
            }
        }

        private void InitialiseColourTweener(ActiveAxisTransition activeAxisTransition)
        {
            JSONNode initialAxisSpecs = activeAxisTransition.InitialAxisSpecs;
            JSONNode finalAxisSpecs = activeAxisTransition.FinalAxisSpecs;
            Scale initialScale = activeAxisTransition.InitialScale;
            Scale finalScale = activeAxisTransition.FinalScale;

            Color initialColour = Color.white;
            if (initialAxisSpecs != null && initialAxisSpecs["color"] != null)
            {
                ColorUtility.TryParseHtmlString(initialAxisSpecs["color"], out initialColour);
            }

            Color finalColour = Color.white;
            if (finalAxisSpecs != null && finalAxisSpecs["color"] != null)
            {
                ColorUtility.TryParseHtmlString(finalAxisSpecs["color"], out finalColour);
            }

            if (initialColour != finalColour)
            {
                activeAxisTransition.TweeningObservable.Subscribe(t =>
                {
                    // Interpolate and set colour
                    Color interpolatedColor = Color.Lerp(initialColour, finalColour, t);
                    SetColor(interpolatedColor);
                }).AddTo(activeAxisTransition.Disposable);
            }

        }

        private void InitialiseTicksTweener(ActiveAxisTransition activeAxisTransition)
        {
            JSONNode initialAxisSpecs = activeAxisTransition.InitialAxisSpecs;
            JSONNode finalAxisSpecs = activeAxisTransition.FinalAxisSpecs;
            Scale initialScale = activeAxisTransition.InitialScale;
            Scale finalScale = activeAxisTransition.FinalScale;

            string face = "";
            string orient = "";

            // We need to check some conditions before we can tween the axis ticks. The conditions we check are:
            // 1. The number of ticks in both states is the same
            // 2. Both states use quantitative values
            // 3. Both are toggled to show tick labels
            // TODO: Loosen these conditions more to allow more different types of tweening
            int initialNumTicks = 0;
            bool isInitialQuantitative = false;
            bool isInitialShowingTickLabels = false;

            if (initialAxisSpecs != null)
            {
                if (initialAxisSpecs["values"] != null)
                {
                    initialNumTicks = initialAxisSpecs["values"].Count;
                    isInitialQuantitative = float.TryParse(initialAxisSpecs["values"][0], out float f);
                }
                if (initialAxisSpecs["labels"] != null)
                {
                    isInitialShowingTickLabels = initialAxisSpecs["labels"].AsBool;
                }

                face = initialAxisSpecs["face"];
                orient = initialAxisSpecs["orient"];
            }

            int finalNumTicks = 0;
            bool isFinalQuantitative = false;
            bool isFinalShowingTickLabels = false;

            if (finalAxisSpecs != null)
            {
                if (finalAxisSpecs["values"] != null)
                {
                    finalNumTicks = finalAxisSpecs["values"].Count;
                    isFinalQuantitative = float.TryParse(finalAxisSpecs["values"][0], out float f);
                }
                if (finalAxisSpecs["labels"] != null)
                {
                    isFinalShowingTickLabels = finalAxisSpecs["labels"].AsBool;
                }

                face = initialAxisSpecs["face"];
                orient = initialAxisSpecs["orient"];
            }

            // Regardless if we actually do tween the ticks or not, we hide them this frame. This is to ensure that the ticks are only shown
            // when they are properly updated by the TweenTicks function, rather than flashing the default tick positions
            SetTickVisibility(false);

            if (initialNumTicks == finalNumTicks && isInitialQuantitative && isFinalQuantitative && isInitialShowingTickLabels && isFinalShowingTickLabels)
            {
                // Delay this tween until the end of frame so that all other observables can run first
                activeAxisTransition.TweeningObservable.DelayFrame(0, FrameCountType.EndOfFrame).Subscribe(t =>
                {
                    TweenTicks(initialAxisSpecs, finalAxisSpecs, initialScale, finalScale, initialNumTicks, face, orient, t);
                }).AddTo(activeAxisTransition.Disposable);
            }
        }

        private void TweenTicks(JSONNode initialAxisSpecs, JSONNode finalAxisSpecs, Scale initialScale, Scale finalScale, int tickCount, string face, string orient, float t)
        {
            for (int i = 0; i < tickCount; i++)
            {
                // TODO: Make this logic a bit more robust to handle more cases
                // Get the initial and final values
                float initialValue = float.Parse(initialAxisSpecs["values"][i].Value);
                float finalValue = float.Parse(finalAxisSpecs["values"][i].Value);
                string averageValue = ((initialValue + finalValue) / 2f).ToString();
                string label = averageValue;
                float position = 0;

                if (initialScale != null)
                {
                    position = float.Parse(initialScale.ApplyScale(initialAxisSpecs["values"][i].Value)) * DxR.Vis.SIZE_UNIT_SCALE_FACTOR;
                }
                if (finalScale != null)
                {
                    position = float.Parse(finalScale.ApplyScale(finalAxisSpecs["values"][i].Value)) * DxR.Vis.SIZE_UNIT_SCALE_FACTOR;
                }

                // If there is a tick already for us to use, take it, otherwise instantiate a new one
                GameObject tick;
                if (i < ticks.Count)
                {
                    tick = ticks[i];
                    if (!tick.activeSelf)
                        tick.SetActive(true);
                }
                else
                {
                    tick = Instantiate(tickPrefab, ticksHolder.transform.position, ticksHolder.transform.rotation, ticksHolder.transform);
                    ticks.Add(tick);
                }

                UpdateTick(tick, position, label, face, orient, GetLength());
            }

            // Hide all leftover ticks
            for (int i = tickCount; i < ticks.Count; i++)
            {
                GameObject tick = ticks[i];
                if (tick.activeSelf)
                {
                    tick.SetActive(false);
                }
            }
        }

        #endregion Morphing functions

        public void Init(Interactions interactions, string field)
        {
            interactionsObject = interactions;
            dataField = field;

            title = gameObject.transform.Find("Title").gameObject;
            axisLine = gameObject.transform.Find("AxisLine").gameObject;
            sliderBar = gameObject.transform.Find("AxisLine/Slider/SliderBar").gameObject;
            ticksHolder = gameObject.transform.Find("Ticks").gameObject;
            tickPrefab = Resources.Load("Axis/Tick") as GameObject;
            titleTextMesh = gameObject.transform.Find("Title/Text").GetComponent<TextMeshPro>();
        }

        public void UpdateSpecs(JSONNode axisSpecs, DxR.Scale scale)
        {
            if (axisSpecs["title"] != null)
            {
                SetTitle(axisSpecs["title"].Value);
            }

            if (axisSpecs["titlePadding"] != null)
            {
                SetTitlePadding(axisSpecs["titlePadding"].AsFloat);
            }
            else
            {
                titleOffset = 0.075f;
            }

            float axisLength = 0.0f;
            if (axisSpecs["length"] != null)
            {
                axisLength = axisSpecs["length"].AsFloat;
                SetLength(axisLength);
            }

            if (axisSpecs["orient"] != null && axisSpecs["face"] != null)
            {
                SetOrientation(axisSpecs["orient"].Value, axisSpecs["face"].Value);
            }

            if (axisSpecs["ticks"].AsBool && axisSpecs["values"] != null)
            {
                ConstructOrUpdateTicks(axisSpecs, scale);
            }

            if (axisSpecs["color"] != null)
            {
                SetColor(axisSpecs["color"].Value);
            }

            if (axisSpecs["filter"] != null)
            {
                if (axisSpecs["filter"].AsBool)
                {
                    EnableThresholdFilter(axisSpecs, scale);
                }
            }
        }

        private void SetTitle(string title)
        {
            titleTextMesh.text = title;
        }

        private void SetTitlePadding(float titlePadding)
        {
            titleOffset = 0.075f + titlePadding * DxR.Vis.SIZE_UNIT_SCALE_FACTOR;
        }

        // TODO: Create ticks marks and tick labels using mark and channel metaphor,
        // i.e., create them using the tick values as data and set orientation channels
        // according to orient and face params.
        private void SetOrientation(string orient, string face)
        {
            if (orient == "bottom" && face == "front")
            {
                OrientAlongPositiveX();
            }
            else if (orient == "left" && face == "front")
            {
                OrientAlongPositiveY();
            }
            else if (orient == "bottom" && face == "left")
            {
                OrientAlongPositiveZ();
            }
        }

        private void OrientAlongPositiveX()
        {
            facingDirection = 'x';
            gameObject.transform.localPosition = GetAxisPosition(facingDirection, GetLength());
            title.transform.localPosition = new Vector3(0, -titleOffset, 0);
        }

        private void OrientAlongPositiveY()
        {
            facingDirection = 'y';
            gameObject.transform.localPosition = GetAxisPosition(facingDirection, GetLength());
            gameObject.transform.localRotation = GetAxisRotation(facingDirection);
            title.transform.localPosition = new Vector3(0, titleOffset, 0);
        }

        private void OrientAlongPositiveZ()
        {
            facingDirection = 'z';
            gameObject.transform.localPosition = GetAxisPosition(facingDirection, GetLength());
            gameObject.transform.localRotation = GetAxisRotation(facingDirection);
            title.transform.localPosition = new Vector3(0, -titleOffset, 0);
            title.transform.localEulerAngles = new Vector3(0, 180, 0);
        }


        private Vector3 GetAxisPosition(char dim, float length)
        {
            switch (dim)
            {
                default:
                case 'x':
                    return new Vector3(length / 2.0f, 0.0f, 0.0f);
                case 'y':
                    return new Vector3(0.0f, length / 2.0f, 0.0f);
                case 'z':
                    return new Vector3(0.0f, 0.0f, length / 2.0f);
            }
        }

        private Quaternion GetAxisRotation(char dim)
        {
            switch (dim)
            {
                default:
                case 'x':
                    return Quaternion.identity;
                case 'y':
                    return Quaternion.Euler(0, 0, 90);
                case 'z':
                    return Quaternion.Euler(0, -90, 0);
            }
        }

        private void CentreTitle()
        {
            switch (facingDirection)
            {
                case 'x':
                    title.transform.localPosition = new Vector3(0, -titleOffset, 0);
                    return;

                case 'y':
                    title.transform.localPosition = new Vector3(0, titleOffset, 0);
                    return;

                case 'z':
                    title.transform.localPosition = new Vector3(0, -titleOffset, 0);
                    title.transform.localEulerAngles = new Vector3(0, 180, 0);
                    return;
            }
        }

        /// <summary>
        /// Translates the axes along a spatial direction. To be used AFTER the orient functions
        /// </summary>
        public void SetTranslation(float value, int dim)
        {
            float offset = value * DxR.Vis.SIZE_UNIT_SCALE_FACTOR;
            Vector3 translateBy = transform.localPosition;
            translateBy[dim] = offset + translateBy[dim];
            transform.localPosition = translateBy;
        }

        public void SetTranslation(Vector3 translation)
        {
            translation *= DxR.Vis.SIZE_UNIT_SCALE_FACTOR;
            Vector3 translateBy = transform.localPosition;
            translateBy += translation;
            transform.localPosition = translateBy;
        }

        public void SetRotate(Quaternion rotate)
        {
            Vector3 targetPosition = rotate * GetAxisPosition(facingDirection, GetLength());
            Quaternion targetRotation = rotate * GetAxisRotation(facingDirection);
            transform.localPosition = targetPosition;
            transform.localRotation = targetRotation;
        }

        private void SetLength(float length)
        {
            length = length * DxR.Vis.SIZE_UNIT_SCALE_FACTOR;
            float newLocalScale = length / meshLength;
            axisLine.transform.localScale = new Vector3(axisLine.transform.localScale.x, newLocalScale, axisLine.transform.localScale.z);
        }

        private float GetLength()
        {
            Vector3 scale = axisLine.transform.localScale;

            // If any of the dimensions in the scale Vector3 are negative, we assume that the size of the View is negative as well,
            // meaning this axis should also move towards the negative direction
            if (scale.x < 0 || scale.y < 0 || scale.z < 0)
            {
                return meshLength * Mathf.Min(scale.x, scale.y, scale.z);
            }
            else
            {
                return meshLength * Mathf.Max(scale.x, scale.y, scale.z);
            }
        }

        private void SetColor(string colorString)
        {
            if (ColorUtility.TryParseHtmlString(colorString, out Color color))
            {
                axisLine.GetComponent<Renderer>().material.color = color;
            }
        }

        private void SetColor(Color color)
        {
            axisLine.GetComponent<Renderer>().material.color = color;
        }

        /// <summary>
        /// Updates the ticks along these axes with new values, creating new ticks if necessary. Will automatically hide unneeded ticks
        /// </summary>
        private void ConstructOrUpdateTicks(JSONNode axisSpecs, DxR.Scale scale)
        {
            bool showTickLabels = axisSpecs["labels"] != null ? showTickLabels = axisSpecs["labels"].AsBool : false;
            int tickCount = axisSpecs["values"].Count;

            for (int i = 0; i < tickCount; i++)
            {
                string domainValue = axisSpecs["values"][i].Value;
                float position = float.Parse(scale.ApplyScale(domainValue)) * DxR.Vis.SIZE_UNIT_SCALE_FACTOR;
                string label = showTickLabels ? domainValue : "";
                string face = axisSpecs["face"];
                string orient = axisSpecs["orient"];

                // If there is a tick already for us to use, take it, otherwise instantiate a new one
                GameObject tick;
                if (i < ticks.Count)
                {
                    tick = ticks[i];
                    if (!tick.activeSelf)
                        tick.SetActive(true);
                }
                else
                {
                    tick = Instantiate(tickPrefab, ticksHolder.transform.position, ticksHolder.transform.rotation, ticksHolder.transform);
                    ticks.Add(tick);
                }

                UpdateTick(tick, position, label, face, orient, GetLength());
            }

            // Hide all leftover ticks
            for (int i = tickCount; i < ticks.Count; i++)
            {
                GameObject tick = ticks[i];
                if (tick.activeSelf)
                {
                    tick.SetActive(false);
                }
            }
        }

        private void SetTickVisibility(bool visible)
        {
            foreach (GameObject tick in ticks)
            {
                tick.SetActive(visible);
            }
        }

        private void UpdateTick(GameObject tick, float position, string label, string face, string orient, float axisLength)
        {
            tick.transform.localPosition = new Vector3(position - (axisLength / 2f), 0, 0);

            // Adjust label
            Transform tickLabelTransform = tick.transform.Find("TickLabel");

            float yoffset = 0.0f;
            float xoffset = 0.0f;
            float zrot = 0;
            float yrot = 0;
            float xrot = 0;

            // Adjust label
            // TODO: Adjust label angle.
            if (face == "front" && orient == "bottom")
            {
                float labelAngle = 0.0f;
                zrot = zrot + labelAngle + 90;
                yoffset = -tickLabelOffset;
            }
            else if (face == "front" && orient == "left")
            {
                tick.transform.localRotation = Quaternion.Euler(0, 0, 180.0f);
                float labelAngle = 0.0f;
                yoffset = -tickLabelOffset;
                zrot = zrot + labelAngle + 90.0f;
            }
            else if (face == "left" && orient == "bottom")
            {
                float labelAngle = 0.0f;
                yoffset = -tickLabelOffset;
                zrot = zrot + labelAngle - 90.0f;
                xrot = xrot + 180.0f;
            }

            tickLabelTransform.localPosition = new Vector3(xoffset, yoffset, 0);
            tickLabelTransform.localEulerAngles = new Vector3(xrot, yrot, zrot);

            tickLabelTransform.GetComponent<TextMeshPro>().text = label;
        }

        private void EnableThresholdFilter(JSONNode axisSpecs, DxR.Scale scale)
        {
            Transform slider = gameObject.transform.Find("AxisLine/Slider");
            slider.gameObject.SetActive(true);

            SetFilterLength(axisSpecs["length"].AsFloat);

            // DxR.SliderGestureControlBothSide sliderControl =
            //         slider.GetComponent<DxR.SliderGestureControlBothSide>();
            // if (sliderControl == null) return;

            float domainMin = float.Parse(scale.domain[0]);
            float domainMax = float.Parse(scale.domain[1]);

            // // TODO: Check validity of specs.
            // sliderControl.SetSpan(domainMin, domainMax);
            // sliderControl.SetSliderValue1(domainMin);
            // sliderControl.SetSliderValue2(domainMax);

            // slider.gameObject.name = dataField;

            // interactionsObject.EnableAxisThresholdFilter(dataField);

            // if (interactionsObject != null)
            // {
            //     sliderControl.OnUpdateEvent.AddListener(interactionsObject.ThresholdFilterUpdated);
            // }
        }

        private void SetFilterLength(float length)
        {
            length = length * DxR.Vis.SIZE_UNIT_SCALE_FACTOR;

            Debug.Log("Setting filter length");

            Transform knob1 = sliderBar.transform.Find("SliderKnob1");
            Transform knob2 = sliderBar.transform.Find("SliderKnob2");
            // Vector3 knobOrigScale1 = knob1.localScale;
            // Vector3 knobOrigScale2 = knob2.localScale;

            float newLocalScale = 0.5f / 0.2127f;
            // float newLocalScale = length / 0.2127f; // sliderBar.GetComponent<MeshFilter>().mesh.bounds.size.x;
            sliderBar.transform.localScale = new Vector3(newLocalScale, sliderBar.transform.localScale.y, sliderBar.transform.localScale.z);

            if (knob1 != null)
            {
                knob1.transform.localScale = new Vector3(0.4f, 2.0f, 1.5f);
            }
            if (knob2 != null)
            {
                knob2.transform.localScale = new Vector3(0.4f, 2.0f, 1.5f);
            }
        }
    }
}