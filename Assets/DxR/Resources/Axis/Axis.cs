﻿using System;
using System.Collections;
using System.Collections.Generic;
using SimpleJSON;
using UnityEngine;
using DxR.VisMorphs;
using UniRx;

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
        private TextMesh titleTextMesh;
        private List<GameObject> ticks = new List<GameObject>();

        // An axis can onlyfeasibly have a single transition applied to it at a time
        private ActiveAxisTransition activeAxisTransition;

        private class ActiveAxisTransition
        {
            public string Name;
            public IObservable<float> TweeningObservable;
            public bool IsReversed;
            public CompositeDisposable Disposable;
            public JSONNode InitialAxisSpecs;
            public JSONNode FinalAxisSpecs;
            public Scale InitialScale;
            public Scale FinalScale;
            public Vector3 InitialTranslation;
            public Vector3 FinalTranslation;

            public ActiveAxisTransition(ActiveTransition activeTransition, CompositeDisposable disposable,
                                        JSONNode initialAxisSpecs, JSONNode finalAxisSpecs, Scale initialScale, Scale finalScale,
                                        Vector3 initialTranslation, Vector3 finalTranslation)
            {
                this.Name = activeTransition.Name;
                this.TweeningObservable = activeTransition.TweeningObservable;
                this.IsReversed = activeTransition.IsReversed;
                this.Disposable = disposable;
                this.InitialAxisSpecs = initialAxisSpecs;
                this.FinalAxisSpecs = finalAxisSpecs;
                this.InitialScale = initialScale;
                this.FinalScale = finalScale;
                this.InitialTranslation = initialTranslation;
                this.FinalTranslation = finalTranslation;
            }
        }


        #region Morphing functions

        public void InitialiseTransition(ActiveTransition newActiveTransition, JSONNode initialAxisSpecs, JSONNode finalAxisSpecs, Scale initialScale, Scale finalScale)
        {
            InitialiseTransition(newActiveTransition, initialAxisSpecs, finalAxisSpecs, initialScale, finalScale, Vector3.zero, Vector3.zero);
        }

        public void InitialiseTransition(ActiveTransition newActiveTransition, JSONNode initialAxisSpecs, JSONNode finalAxisSpecs, Scale initialScale, Scale finalScale, Vector3 initialTranslation, Vector3 finalTranslation)
        {
            if (activeAxisTransition != null)
                throw new Exception(string.Format("Vis Morphs: Axis is already undergoing a transition \"{0}\" and cannot apply transition \"{1}\".", activeAxisTransition.Name,  newActiveTransition.Name));

            // Create the disposable which will be used to store and quickly unsubscribe from our tween
            // We will set up separate subscriptions for each tweener
            CompositeDisposable transitionDisposable = new CompositeDisposable();

            activeAxisTransition = new ActiveAxisTransition(newActiveTransition, transitionDisposable, initialAxisSpecs, finalAxisSpecs, initialScale, finalScale, initialTranslation, finalTranslation);

            InitialiseTweeners(activeAxisTransition, initialTranslation, finalTranslation);
        }

        /// <summary>
        /// Returns false if the axis is to be deleted at the end of this function
        /// </summary>
        public bool StopTransition(string transitionName, bool goToEnd = true)
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
            if (goToEnd)
            {
                if (activeAxisTransition.FinalAxisSpecs != null)
                {
                    UpdateSpecs(activeAxisTransition.FinalAxisSpecs, activeAxisTransition.FinalScale);
                    // Make sure to set the translation too
                    SetTranslation(activeAxisTransition.FinalTranslation);
                }
                else
                {
                    Destroy(gameObject);
                    return true;
                }
            }
            else
            {
                if (activeAxisTransition.InitialAxisSpecs != null)
                {
                    UpdateSpecs(activeAxisTransition.InitialAxisSpecs, activeAxisTransition.InitialScale);
                    SetTranslation(activeAxisTransition.InitialTranslation);
                }
                else
                {
                    Destroy(gameObject);
                    return true;
                }
            }

            activeAxisTransition = null;
            return false;
        }

        private void InitialiseTweeners(ActiveAxisTransition activeAxisTransition, Vector3 initialTranslation, Vector3 finalTranslation)
        {
            InitialiseTitleTweener(activeAxisTransition);

            float initialLength, finalLength;
            InitialiseLengthTweener(activeAxisTransition, out initialLength, out finalLength);

            InitialisePositionTweener(activeAxisTransition, initialLength, finalLength, initialTranslation, finalTranslation);

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

        private void InitialisePositionTweener(ActiveAxisTransition activeAxisTransition, float initialLength, float finalLength, Vector3 initialTranslation, Vector3 finalTranslation)
        {
            JSONNode initialAxisSpecs = activeAxisTransition.InitialAxisSpecs;
            JSONNode finalAxisSpecs = activeAxisTransition.FinalAxisSpecs;
            Scale initialScale = activeAxisTransition.InitialScale;
            Scale finalScale = activeAxisTransition.FinalScale;

            // Get the initial and final positions (this is based on the lengths calculated in InitialiseLengthTweener())
            Vector3 initialPosition = Vector3.zero;
            Vector3 finalPosition = Vector3.zero;
            if (facingDirection == 'x') {
                initialPosition = new Vector3(initialLength / 2.0f, 0.0f, 0.0f);
                finalPosition = new Vector3(finalLength / 2.0f, 0.0f, 0.0f);
            }
            else if (facingDirection == 'y') {
                initialPosition = new Vector3(0.0f, initialLength / 2.0f, 0.0f);
                finalPosition = new Vector3(0.0f, finalLength / 2.0f, 0.0f);
            }
            else if (facingDirection == 'z') {
                initialPosition = new Vector3(0.0f, 0.0f, initialLength / 2.0f);
                finalPosition = new Vector3(0.0f, 0.0f, finalLength / 2.0f);
            }

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

            if (initialNumTicks == finalNumTicks && isInitialQuantitative && isFinalQuantitative && isInitialShowingTickLabels && isFinalShowingTickLabels)
            {
                // Delay this tween until the end of frame so that all other observables can run first
                activeAxisTransition.TweeningObservable.DelayFrame(0, FrameCountType.EndOfFrame).Subscribe(t =>
                {
                    TweenTicks(initialAxisSpecs, finalAxisSpecs, initialScale, finalScale, initialNumTicks, face, orient, t);
                }).AddTo(activeAxisTransition.Disposable);
            }
            else
            {
                SetTickVisibility(false);
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
            titleTextMesh = gameObject.transform.Find("Title/Text").GetComponent<TextMesh>();
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
            gameObject.transform.localPosition = new Vector3(GetLength() / 2.0f, 0.0f, 0.0f);
            title.transform.localPosition = new Vector3(0, -titleOffset, 0);
            facingDirection = 'x';
        }

        private void OrientAlongPositiveY()
        {
            gameObject.transform.localEulerAngles = new Vector3(0, 0, 90);
            gameObject.transform.localPosition = new Vector3(0.0f, GetLength() / 2.0f, 0.0f);
            title.transform.localPosition = new Vector3(0, titleOffset, 0);
            facingDirection = 'y';
        }

        private void OrientAlongPositiveZ()
        {
            gameObject.transform.localEulerAngles = new Vector3(0, -90, 0);
            gameObject.transform.localPosition = new Vector3(0.0f, 0.0f, GetLength() / 2.0f);
            title.transform.localPosition = new Vector3(0, -titleOffset, 0);
            title.transform.localEulerAngles = new Vector3(0, 180, 0);
            facingDirection = 'z';
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

            tickLabelTransform.GetComponent<TextMesh>().text = label;
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