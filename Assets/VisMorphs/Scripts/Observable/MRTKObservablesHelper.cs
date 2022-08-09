using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UniRx;
using Microsoft.MixedReality.Toolkit;
using Microsoft.MixedReality.Toolkit.UX;
using TMPro;

namespace DxR.VisMorphs
{
    public class MRTKObservablesHelper
    {
        // The script that MRTK provides to give us access to the controllers (or hands)
        private ControllerLookup controllerLookup;

        private Dictionary<Handedness, IObservable<GameObject>> gameObjectObservables = new Dictionary<Handedness, IObservable<GameObject>>();
        private Dictionary<Handedness, IObservable<float>> selectObservables = new Dictionary<Handedness, IObservable<float>>();
        private Dictionary<Handedness, IObservable<Collider[]>> touchingGameObjectsObservables = new Dictionary<Handedness, IObservable<Collider[]>>();
        private Dictionary<Handedness, IObservable<RaycastHit[]>> pointingGameObjectsObservables = new Dictionary<Handedness, IObservable<RaycastHit[]>>();
        private Dictionary<Tuple<Handedness, string>, IObservable<GameObject[]>> proximityGameObjectsObservables = new Dictionary<Tuple<Handedness, string>, IObservable<GameObject[]>>();
        private Dictionary<string, IObservable<dynamic>> uiObservables = new Dictionary<string, IObservable<dynamic>>();

        public MRTKObservablesHelper()
        {
            controllerLookup = GameObject.FindObjectOfType<ControllerLookup>();
        }

        public IObservable<GameObject> GetControllerGameObjectObservable(Handedness handedness)
        {
            IObservable<GameObject> observable = null;

            if (!gameObjectObservables.TryGetValue(handedness, out observable))
            {
                // We use "none" to represent "any", i.e., both hands
                if (handedness == Handedness.None)
                {
                    IObservable<GameObject> leftHandObservable = GetControllerGameObjectObservable(Handedness.Left);
                    IObservable<GameObject> rightHandObservable = GetControllerGameObjectObservable(Handedness.Right);
                    observable = leftHandObservable.Merge(rightHandObservable);
                }
                else
                {
                    var controller = handedness == Handedness.Left ? controllerLookup.LeftHandController : controllerLookup.RightHandController;
                    observable = Observable.EveryUpdate().Where(_ => controller.currentControllerState.inputTrackingState != UnityEngine.XR.InputTrackingState.None).Select(_ => controller.gameObject);
                }

                gameObjectObservables.Add(handedness, observable);
            }

            return observable;
        }

        /// <summary>
        /// Returns an observable of the extent to which the select action is performed. For hand input this is the pinch gesture.
        /// </summary>
        public IObservable<float> GetControllerSelectObservable(Handedness handedness)
        {
            IObservable<float> observable = null;

            if (!selectObservables.TryGetValue(handedness, out observable))
            {
                // We use "none" to represent "any", i.e., both hands
                if (handedness == Handedness.None)
                {
                    IObservable<float> leftHandObservable = GetControllerSelectObservable(Handedness.Left);
                    IObservable<float> rightHandObservable = GetControllerSelectObservable(Handedness.Right);
                    observable = leftHandObservable.Merge(rightHandObservable);
                }
                else
                {
                    // Controllers and hands should be treated identically in UnityXR
                    var controller = handedness == Handedness.Left ? controllerLookup.LeftHandController : controllerLookup.RightHandController;
                    observable = Observable.EveryUpdate().Where(_ => controller.currentControllerState.inputTrackingState != UnityEngine.XR.InputTrackingState.None).Select(_ => controller.selectInteractionState.value);
                }

                selectObservables.Add(handedness, observable);
            }

            return observable;
        }

        public IObservable<Collider[]> GetControllerTouchingGameObjectsObservable(Handedness handedness)
        {
            IObservable<Collider[]> observable = null;

            if (!touchingGameObjectsObservables.TryGetValue(handedness, out observable))
            {
                // We use "none" to represent "any", i.e., both hands
                if (handedness == Handedness.None)
                {
                    IObservable<Collider[]> leftHandObservable = GetControllerTouchingGameObjectsObservable(Handedness.Left);
                    IObservable<Collider[]> rightHandObservable = GetControllerTouchingGameObjectsObservable(Handedness.Right);
                    observable = leftHandObservable.Merge(rightHandObservable);
                }
                else
                {
                    var controller = handedness == Handedness.Left ? controllerLookup.LeftHandController : controllerLookup.RightHandController;
                    observable = GetControllerSelectObservable(handedness).Select(_ => Physics.OverlapSphere(controller.currentControllerState.position, 0.125f))
                        .StartWith(new Collider[] { });
                }

                touchingGameObjectsObservables.Add(handedness, observable);
            }

            return observable;
        }

        public IObservable<RaycastHit[]> GetControllerPointingGameObjectsObservable(Handedness handedness)
        {
            IObservable<RaycastHit[]> observable = null;

            if (!pointingGameObjectsObservables.TryGetValue(handedness, out observable))
            {
                // We use "none" to represent "any", i.e., both hands
                if (handedness == Handedness.None)
                {
                    IObservable<RaycastHit[]> leftHandObservable = GetControllerPointingGameObjectsObservable(Handedness.Left);
                    IObservable<RaycastHit[]> rightHandObservable = GetControllerPointingGameObjectsObservable(Handedness.Right);
                    observable = leftHandObservable.Merge(rightHandObservable);
                }
                else
                {
                    var controller = handedness == Handedness.Left ? controllerLookup.LeftHandController : controllerLookup.RightHandController;
                    observable = GetControllerSelectObservable(handedness).Select(_ => Physics.RaycastAll(controller.transform.position, controller.transform.forward))
                        .StartWith(new RaycastHit[] { });
                }

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
                // We use "none" to represent "any", i.e., both hands
                if (handedness == Handedness.None)
                {
                    IObservable<GameObject[]> leftHandObservable = GetControllerProximityGameObjectsObservable(Handedness.Left, target);
                    IObservable<GameObject[]> rightHandObservable = GetControllerProximityGameObjectsObservable(Handedness.Right, target);
                    observable = leftHandObservable.Merge(rightHandObservable);
                }
                else
                {
                    var controller = handedness == Handedness.Left ? controllerLookup.LeftHandController : controllerLookup.RightHandController;
                    observable = Observable.EveryUpdate().Where(_ => controller.currentControllerState.inputTrackingState != UnityEngine.XR.InputTrackingState.None)
                                                         .Select(_ => GameObject.FindGameObjectsWithTag(target)
                                                         .OrderBy(go => Vector3.Distance(controller.transform.position, go.transform.position)).ToArray());
                }

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

                ToggleCollection toggleCollection = uiGameObject.GetComponentInChildren<ToggleCollection>();
                PressableButton button = uiGameObject.GetComponentInChildren<PressableButton>();

                if (toggleCollection != null)
                {
                    observable = toggleCollection.OnToggleSelected.AsObservable<int>().Select(idx => toggleCollection.Toggles[idx].GetComponentInChildren<TextMeshPro>().text);
                }
                else if (button != null)
                {
                    IObservable<bool> toggledObservable = button.IsToggled.OnEntered.AsObservable<float>().Select(_ => true);
                    IObservable<bool> detoggledObservable = button.IsToggled.OnExited.AsObservable<float>().Select(_ => false);
                    observable = toggledObservable.Merge(detoggledObservable).Select(_ => (dynamic)_);
                }
                else
                {
                    throw new Exception(string.Format("Vis Morphs: The UI GameObject {0} does not have a supported UI script on it. Currently supported are ToggleCollection and PressableButton.", uiName));
                }

                uiObservables.Add(uiName, observable);
            }

            return observable;
        }
    }
}