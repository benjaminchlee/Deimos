using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UniRx;
using Microsoft.MixedReality.Toolkit;
using Microsoft.MixedReality.Toolkit.UX;
using System.Linq;
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
        private Dictionary<string, IObservable<string>> toggleMenuObservables = new Dictionary<string, IObservable<string>>();
        private Dictionary<string, IObservable<bool>> toggleButtonObservables = new Dictionary<string, IObservable<bool>>();

        public MRTKObservablesHelper()
        {
            controllerLookup = GameObject.FindObjectOfType<ControllerLookup>();
        }

        public IObservable<GameObject> GetControllerGameObjectObservable(Handedness handedness)
        {
            if (handedness == Handedness.None)
                throw new Exception();

            IObservable<GameObject> observable;

            if (!gameObjectObservables.TryGetValue(handedness, out observable))
            {
                var controller = handedness == Handedness.Left ? controllerLookup.LeftHandController : controllerLookup.RightHandController;
                observable = Observable.EveryUpdate().Select(_ => controller.gameObject);
                gameObjectObservables.Add(handedness, observable);
            }

            return observable;
        }

        /// <summary>
        /// Returns an observable of the extent to which the select action is performed. For hand input this is the pinch gesture.
        /// </summary>
        public IObservable<float> GetControllerSelectObservable(Handedness handedness)
        {
            if (handedness == Handedness.None)
                throw new Exception();

            IObservable<float> observable;

            if (!selectObservables.TryGetValue(handedness, out observable))
            {
                // Controllers and hands should be treated identically in UnityXR
                var controller = handedness == Handedness.Left ? controllerLookup.LeftHandController : controllerLookup.RightHandController;
                observable = Observable.EveryUpdate().Select(_ => controller.selectInteractionState.value);
                selectObservables.Add(handedness, observable);
            }

            return observable;
        }

        public IObservable<Collider[]> GetControllerTouchingGameObjectsObservable(Handedness handedness)
        {
            if (handedness == Handedness.None)
                throw new Exception();

            IObservable<Collider[]> observable;

            if (!touchingGameObjectsObservables.TryGetValue(handedness, out observable))
            {
                var controller = handedness == Handedness.Left ? controllerLookup.LeftHandController : controllerLookup.RightHandController;

                observable = GetControllerSelectObservable(handedness)
                    .Select(_ =>
                    {
                        return Physics.OverlapSphere(controller.currentControllerState.position, 0.125f);
                    })
                    .StartWith(new Collider[] { });

                touchingGameObjectsObservables.Add(handedness, observable);
            }

            return observable;
        }

        public IObservable<RaycastHit[]> GetControllerPointingGameObjectsObservable(Handedness handedness)
        {
            if (handedness == Handedness.None)
                throw new Exception();

            IObservable<RaycastHit[]> observable;

            if (!pointingGameObjectsObservables.TryGetValue(handedness, out observable))
            {
                var controller = handedness == Handedness.Left ? controllerLookup.LeftHandController : controllerLookup.RightHandController;

                observable = GetControllerSelectObservable(handedness)
                    .Select(_ =>
                    {
                        return Physics.RaycastAll(controller.transform.position, controller.transform.forward);
                    })
                    .StartWith(new RaycastHit[] { });

                pointingGameObjectsObservables.Add(handedness, observable);
            }

            return observable;
        }


        public IObservable<GameObject[]> GetControllerProximityGameObjectsObservable(Handedness handedness, string target)
        {
            if (handedness == Handedness.None)
                throw new Exception();

            Tuple<Handedness, string> key = new Tuple<Handedness, string>(handedness, target);

            IObservable<GameObject[]> observable;

            if (!proximityGameObjectsObservables.TryGetValue(key, out observable))
            {
                var controller = handedness == Handedness.Left ? controllerLookup.LeftHandController : controllerLookup.RightHandController;

                observable = Observable.EveryUpdate().Select(_ =>
                    {
                        return GameObject.FindGameObjectsWithTag(target).OrderBy(go => Vector3.Distance(controller.transform.position, go.transform.position)).ToArray();
                    });

                proximityGameObjectsObservables.Add(key, observable);
            }

            return observable;
        }

        public IObservable<string> GetToggleMenuObservable(string menuName)
        {
            IObservable<string> observable = null;

            if (!toggleMenuObservables.TryGetValue(menuName, out observable))
            {
                ToggleCollection toggleCollection = GameObject.Find(menuName).GetComponentInChildren<ToggleCollection>();

                observable = toggleCollection.OnToggleSelected.AsObservable<int>().Select(idx => toggleCollection.Toggles[idx].GetComponentInChildren<TextMeshPro>().text);

                toggleMenuObservables.Add(menuName, observable);
            }

            return observable;
        }

        public IObservable<bool> GetToggleButtonObservable(string buttonName)
        {
            IObservable<bool> observable = null;

            if (!toggleButtonObservables.TryGetValue(buttonName, out observable))
            {
                PressableButton button = GameObject.Find(buttonName).GetComponentInChildren<PressableButton>();

                IObservable<bool> toggledObservable = button.IsToggled.OnEntered.AsObservable<float>().Select(_ => true);
                IObservable<bool> detoggledObservable = button.IsToggled.OnExited.AsObservable<float>().Select(_ => false);

                observable = toggledObservable.Merge(detoggledObservable);
                toggleButtonObservables.Add(buttonName, observable);
            }

            return observable;
        }
    }
}