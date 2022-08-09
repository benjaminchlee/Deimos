using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UniRx;
using System.Linq;

namespace DxR.VisMorphs
{
    public class MouseObservablesHelper
    {
        private IObservable<Vector3> positionObservable;
        private Dictionary<string, IObservable<bool>> buttonPressedObservables = new Dictionary<string, IObservable<bool>>();
        private Dictionary<string, IObservable<bool>> buttonClickedObservables = new Dictionary<string, IObservable<bool>>();
        private IObservable<RaycastHit[]> raycastHitsObservable;

        public IObservable<Vector3> GetMousePositionObservable()
        {
            if (positionObservable == null)
                positionObservable = Observable.EveryUpdate().Select(_ => Input.mousePosition);

            return positionObservable;
        }

        public IObservable<bool> GetMouseButtonSelectObservable(string button)
        {
            IObservable<bool> observable;

            if (!buttonPressedObservables.TryGetValue(button, out observable))
            {
                switch (button)
                {
                    case "left":
                        observable = Observable.EveryUpdate().Select(_ => Input.GetMouseButton(0));
                        break;

                    case "right":
                        observable = Observable.EveryUpdate().Select(_ => Input.GetMouseButton(1));
                        break;

                    case "any":
                        observable = Observable.EveryUpdate().Select(_ => Input.GetMouseButton(0) || Input.GetMouseButton(1));
                        break;
                }

                buttonPressedObservables.Add(button, observable);
            }

            return observable;
        }

        public IObservable<bool> GetMouseButtonClickedObservable(string button)
        {
            IObservable<bool> observable;

            if (!buttonClickedObservables.TryGetValue(button, out observable))
            {
                switch (button)
                {
                    case "left":
                        observable = Observable.EveryUpdate().Select(_ => Input.GetMouseButtonDown(0));
                        break;

                    case "right":
                        observable = Observable.EveryUpdate().Select(_ => Input.GetMouseButtonDown(1));
                        break;

                    case "any":
                        observable = Observable.EveryUpdate().Select(_ => Input.GetMouseButtonDown(0) || Input.GetMouseButtonDown(1));
                        break;
                }

                buttonClickedObservables.Add(button, observable);
            }

            return observable;
        }

        public IObservable<RaycastHit[]> GetMouseRaycastHitsObservable()
        {
            if (raycastHitsObservable == null)
            {
                raycastHitsObservable = GetMousePositionObservable().Select(position =>
                    {
                        Ray ray = Camera.main.ScreenPointToRay(position);
                        RaycastHit[] hits = Physics.RaycastAll(ray);
                        return hits;
                    });
            }

            return raycastHitsObservable;
        }
    }
}