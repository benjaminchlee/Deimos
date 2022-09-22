using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UniRx;
using Microsoft.MixedReality.Toolkit;
using TMPro;
using Microsoft.MixedReality.Toolkit.Utilities;
using Microsoft.MixedReality.Toolkit.Input;
using UnityEngine.Events;
using Microsoft.MixedReality.Toolkit.UI;

namespace DxR.Deimos
{
    public class MRTKObservablesHelper : IMixedRealitySourceStateHandler, IMixedRealityPointerHandler
    {
        private Dictionary<Handedness, IObservable<IMixedRealityController>> controllerActiveObservables = new Dictionary<Handedness, IObservable<IMixedRealityController>>();
        private Dictionary<Handedness, IObservable<GameObject>> gameObjectObservables = new Dictionary<Handedness, IObservable<GameObject>>();
        private Dictionary<Handedness, IObservable<bool>> selectObservables = new Dictionary<Handedness, IObservable<bool>>();
        private Dictionary<Handedness, IObservable<Collider[]>> touchingGameObjectsObservables = new Dictionary<Handedness, IObservable<Collider[]>>();
        private Dictionary<Handedness, IObservable<RaycastHit[]>> pointingGameObjectsObservables = new Dictionary<Handedness, IObservable<RaycastHit[]>>();
        private Dictionary<Tuple<Handedness, string>, IObservable<GameObject[]>> proximityGameObjectsObservables = new Dictionary<Tuple<Handedness, string>, IObservable<GameObject[]>>();
        private Dictionary<string, IObservable<dynamic>> uiObservables = new Dictionary<string, IObservable<dynamic>>();

        private Dictionary<Handedness, IMixedRealityPointer> pointers = new Dictionary<Handedness, IMixedRealityPointer>();

        // Unity event versions of MRTK pointer events so that it is easier to hook into these with observables
        [Serializable]
        private class MRTKSourceEvent : UnityEvent<SourceStateEventData> { }
        private MRTKSourceEvent MRTKSourceDetected;
        private MRTKSourceEvent MRTKSourceLost;

        private class MRTKPointerEvent : UnityEvent<MixedRealityPointerEventData> { }
        private MRTKPointerEvent MRTKPointerDown;
        private MRTKPointerEvent MRTKPointerUp;
        private MRTKPointerEvent MRTKPointerClicked;
        private MRTKPointerEvent MRTKPointerDragged;

        public MRTKObservablesHelper()
        {
            CoreServices.InputSystem?.RegisterHandler<IMixedRealitySourceStateHandler>(this);
            MRTKSourceDetected = new MRTKSourceEvent();
            MRTKSourceLost = new MRTKSourceEvent();

            CoreServices.InputSystem?.RegisterHandler<IMixedRealityPointerHandler>(this);
            MRTKPointerDown = new MRTKPointerEvent();
            MRTKPointerUp = new MRTKPointerEvent();
            MRTKPointerClicked = new MRTKPointerEvent();
            MRTKPointerDragged = new MRTKPointerEvent();
        }

        public void OnSourceDetected(SourceStateEventData eventData)
        {
            if (eventData.InputSource.SourceType == InputSourceType.Hand || eventData.InputSource.SourceType == InputSourceType.Controller)
            {
                if (eventData.Controller.ControllerHandedness == Handedness.Left || eventData.Controller.ControllerHandedness == Handedness.Right)
                    MRTKSourceDetected.Invoke(eventData);
            }
        }

        public void OnSourceLost(SourceStateEventData eventData)
        {
            if (eventData.InputSource.SourceType == InputSourceType.Hand || eventData.InputSource.SourceType == InputSourceType.Controller)
            {
                if (eventData.Controller.ControllerHandedness == Handedness.Left || eventData.Controller.ControllerHandedness == Handedness.Right)
                    MRTKSourceLost.Invoke(eventData);
            }
        }

        public void OnPointerDown(MixedRealityPointerEventData eventData)
        {
            if (eventData.InputSource.SourceType == InputSourceType.Hand || eventData.InputSource.SourceType == InputSourceType.Controller)
            {
                if (eventData.Handedness == Handedness.Left || eventData.Handedness == Handedness.Right)
                    MRTKPointerDown.Invoke(eventData);
            }
        }

        public void OnPointerUp(MixedRealityPointerEventData eventData)
        {
            if (eventData.InputSource.SourceType == InputSourceType.Hand || eventData.InputSource.SourceType == InputSourceType.Controller)
            {
                if (eventData.Handedness == Handedness.Left || eventData.Handedness == Handedness.Right)
                    MRTKPointerUp.Invoke(eventData);
            }
        }

        public void OnPointerClicked(MixedRealityPointerEventData eventData)
        {
            if (eventData.InputSource.SourceType == InputSourceType.Hand || eventData.InputSource.SourceType == InputSourceType.Controller)
            {
                if (eventData.Handedness == Handedness.Left || eventData.Handedness == Handedness.Right)
                    MRTKPointerClicked.Invoke(eventData);
            }
        }

        public void OnPointerDragged(MixedRealityPointerEventData eventData)
        {
            if (eventData.InputSource.SourceType == InputSourceType.Hand || eventData.InputSource.SourceType == InputSourceType.Controller)
            {
                if (eventData.Handedness == Handedness.Left || eventData.Handedness == Handedness.Right)
                    MRTKPointerDragged.Invoke(eventData);
            }
        }

        public IObservable<IMixedRealityController> GetControllerActiveObservable(Handedness handedness)
        {
            IObservable<IMixedRealityController> observable = null;

            if (!controllerActiveObservables.TryGetValue(handedness, out observable))
            {
                IObservable<IMixedRealityController> controllerLostObservable = MRTKSourceLost.AsObservable().Where(eventData => eventData.Controller != null && (eventData.Controller.ControllerHandedness == handedness || handedness == Handedness.Any)).Select(_ => (IMixedRealityController)null);
                observable = MRTKSourceDetected.AsObservable().Where(eventData => eventData.Controller != null && (eventData.Controller.ControllerHandedness == handedness || handedness == Handedness.Any)).Select(eventData => eventData.Controller)
                            .TakeUntil(controllerLostObservable)
                            .Repeat()
                            .Merge(controllerLostObservable);
                observable = Observable.EveryUpdate().CombineLatest(observable, (_, controller) => controller).Where(controller => controller != null);

                // Force this observable to be a hot observable
                observable = observable.Replay(1).RefCount();
                observable.Subscribe();
                controllerActiveObservables.Add(handedness, observable);
            }

            return observable;
        }

        public IObservable<GameObject> GetControllerGameObjectObservable(Handedness handedness)
        {
            IObservable<GameObject> observable = null;

            if (!gameObjectObservables.TryGetValue(handedness, out observable))
            {
                observable = GetControllerActiveObservable(handedness).Select(controller => {
                    // If this controller is a hand, we return the index tip joint position
                    var hand = controller as IMixedRealityHand;
                    if (hand != null)
                    {
                        if (controller.Visualizer != null && controller.Visualizer.GameObjectProxy != null)
                        {
                            Transform indexTipTransform = controller.Visualizer.GameObjectProxy.transform.Find("IndexTip Proxy Transform");
                            return (indexTipTransform != null) ? indexTipTransform.gameObject : null;
                        }
                    }
                    // If this controller is an actual controller, we return its visualiser proxy
                    else
                    {
                        if (controller.Visualizer != null && controller.Visualizer.GameObjectProxy != null)
                            return controller.Visualizer.GameObjectProxy;
                    }

                    return null;
                })
                    .Where(_ => _ != null);

                // Force this observable to be a hot observable
                observable = observable.Replay(1).RefCount();
                observable.Subscribe();
                gameObjectObservables.Add(handedness, observable);
            }

            return observable;
        }

        /// <summary>
        /// Returns a boolean as to whether or not the select action is being performed
        /// </summary>
        public IObservable<bool> GetControllerSelectObservable(Handedness handedness)
        {
            IObservable<bool> observable = null;

            if (!selectObservables.TryGetValue(handedness, out observable))
            {
                IObservable<bool> pointerUpObservable = MRTKPointerUp.AsObservable().Where(eventData => (eventData.Handedness == handedness || handedness == Handedness.Any)).Select(_ => false);
                observable = MRTKPointerDown.AsObservable().Where(eventData => (eventData.Handedness == handedness || handedness == Handedness.Any)).Select(_ => true)
                                                            .TakeUntil(pointerUpObservable)
                                                            .Repeat()
                                                            .Merge(pointerUpObservable);

                observable = observable.Replay(1).RefCount();
                selectObservables.Add(handedness, observable);
            }

            return observable;
        }

        public IObservable<Collider[]> GetControllerTouchingGameObjectsObservable(Handedness handedness)
        {
            IObservable<Collider[]> observable = null;

            if (!touchingGameObjectsObservables.TryGetValue(handedness, out observable))
            {
                IObservable<GameObject> gameObjectObservable = GetControllerGameObjectObservable(handedness);
                observable = GetControllerGameObjectObservable(handedness)
                                .Select(controller => Physics.OverlapSphere(controller.transform.position, 0.125f));

                // Force this observable to be a hot observable
                observable = observable.Replay(1).RefCount();
                observable.Subscribe();
                touchingGameObjectsObservables.Add(handedness, observable);
            }

            return observable;
        }

        public IObservable<RaycastHit[]> GetControllerPointingGameObjectsObservable(Handedness handedness)
        {
            IObservable<RaycastHit[]> observable = null;

            if (!pointingGameObjectsObservables.TryGetValue(handedness, out observable))
            {
                observable = GetControllerActiveObservable(handedness).Select(controller =>
                {
                    foreach (var pointer in controller.InputSource.Pointers)
                    {
                        if (pointer.SceneQueryType == Microsoft.MixedReality.Toolkit.Physics.SceneQueryType.SimpleRaycast)
                        {
                            Ray ray = new Ray(pointer.Rays[0].Origin, pointer.Rays[0].Direction);
                            return Physics.RaycastAll(ray, 100);
                        }
                    }
                    return null;
                })
                    .StartWith(new RaycastHit[] { })
                    .Where(_ => _ != null);

                // Force this observable to be a hot observable
                observable = observable.Replay(1).RefCount();
                observable.Subscribe();
                pointingGameObjectsObservables.Add(handedness, observable);
            }

            return observable;
        }


        public IObservable<GameObject[]> GetControllerProximityGameObjectsObservable(Handedness handedness, string target)
        {
            IObservable<GameObject[]> observable = null;
            Tuple<Handedness, string> key = new Tuple<Handedness, string>(handedness, target);

            if (!proximityGameObjectsObservables.TryGetValue(key, out observable))
            {
                observable = GetControllerGameObjectObservable(handedness).Select(controller =>
                {
                    return GameObject.FindGameObjectsWithTag(target).OrderBy(go => Vector3.Distance(controller.transform.position, go.transform.position)).ToArray();
                })
                    .StartWith(new GameObject[] { });

                // Force this observable to be a hot observable
                observable = observable.Replay(1).RefCount();
                observable.Subscribe();
                proximityGameObjectsObservables.Add(key, observable);
            }

            return observable;
        }

        public IObservable<dynamic> GetUIObservable(string uiName)
        {
            IObservable<dynamic> observable = null;

            if (!uiObservables.TryGetValue(uiName, out observable))
            {
                GameObject uiGameObject = GameObject.Find(uiName);

                InteractableToggleCollection toggleCollection = uiGameObject.GetComponentInChildren<InteractableToggleCollection>();
                Interactable interactable = uiGameObject.GetComponentInChildren<Interactable>();

                if (toggleCollection != null)
                {
                    observable = toggleCollection.OnSelectionEvents.AsObservable().Select(_ => toggleCollection.ToggleList[toggleCollection.CurrentIndex].GetComponentInChildren<TextMesh>().text);
                }
                else if (interactable != null)
                {
                    if (interactable.ButtonMode == SelectionModes.Toggle)
                    {
                        observable = interactable.OnClick.AsObservable().Select(_ => (dynamic)interactable.IsToggled);
                    }
                    // TODO: Other types of buttons
                    else
                    {
                        throw new NotImplementedException("Buttons other than toggles not supported");
                    }
                }
                else
                {
                    throw new Exception(string.Format("Deimos: The UI GameObject {0} does not have a supported UI script on it. Currently supported are ToggleCollection and PressableButton.", uiName));
                }

                // Force this observable to be a hot observable
                observable = observable.Replay(1).RefCount();
                observable.Subscribe();
                uiObservables.Add(uiName, observable);
            }

            return observable;
        }
    }
}