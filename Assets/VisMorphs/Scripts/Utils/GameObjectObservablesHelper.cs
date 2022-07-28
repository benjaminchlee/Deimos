using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UniRx;
using System.Linq;

namespace DxR.VisMorphs
{
    public class GameObjectObservablesHelper : MonoBehaviour
    {
        private Dictionary<GameObject, IObservable<GameObject>> gameObjectObservables = new Dictionary<GameObject, IObservable<GameObject>>();
        private Dictionary<GameObject, IObservable<Collider[]>> overlapBoxObservables = new Dictionary<GameObject, IObservable<Collider[]>>();
        private Dictionary<GameObject, IObservable<RaycastHit[]>> raycastHitsObservables = new Dictionary<GameObject, IObservable<RaycastHit[]>>();

        public IObservable<GameObject> GetGameObjectObservable(GameObject go)
        {
            IObservable<GameObject> observable;

            if (!gameObjectObservables.TryGetValue(go, out observable))
            {
                observable = Observable.EveryUpdate().Select(_ => go);

                gameObjectObservables.Add(go, observable);
            }

            return observable;
        }

        public IObservable<Collider[]> GetGameObjectOverlapBoxObservable(GameObject go)
        {
            IObservable<Collider[]> observable;

            if (!overlapBoxObservables.TryGetValue(go, out observable))
            {
                // Find the collider that is on the source gameobject. This assumes that the collider is at the root object
                // TODO: For now we always assume that the collider is a box, make this support other types
                BoxCollider collider = go.GetComponent<BoxCollider>();
                observable = Observable.EveryUpdate().Select(_ =>
                    {
                        return Physics.OverlapBox(go.transform.TransformPoint(collider.center),
                                                  go.transform.TransformVector(collider.size) / 2f,
                                                  go.transform.rotation);
                    });

                overlapBoxObservables.Add(go, observable);
            }

            return observable;
        }

        public IObservable<RaycastHit[]> GetGameObjectRaycastHitObservable(GameObject go)
        {
            IObservable<RaycastHit[]> observable;

            if (!raycastHitsObservables.TryGetValue(go, out observable))
            {
                observable = Observable.EveryUpdate().Select(_ =>
                    {
                        return Physics.RaycastAll(go.transform.position, go.transform.forward);
                    });

                raycastHitsObservables.Add(go, observable);
            }

            return observable;
        }

        public void ClearGameObjectObservables()
        {
            overlapBoxObservables.Clear();
            raycastHitsObservables.Clear();
        }
    }
}