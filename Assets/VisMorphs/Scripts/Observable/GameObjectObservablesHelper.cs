using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UniRx;

namespace DxR.VisMorphs
{
    public class GameObjectObservablesHelper
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

                observable = observable.Replay(1).RefCount();
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
                Vector3 extra = new Vector3(0.01f, 0.01f, 0.01f);
                observable = Observable.EveryFixedUpdate().Select(_ =>
                    {
                        return Physics.OverlapBox(collider.transform.TransformPoint(collider.center),
                                                  Vector3.Scale(go.transform.localScale, collider.size * 0.5f)  + extra,
                                                  collider.transform.rotation);
                    });

                // Force this observable to be a hot observable
                observable = observable.Replay(1).RefCount();
                observable.Subscribe();
                overlapBoxObservables.Add(go, observable);
            }

            return observable;
        }

        public IObservable<RaycastHit[]> GetGameObjectRaycastHitObservable(GameObject go)
        {
            IObservable<RaycastHit[]> observable;

            if (!raycastHitsObservables.TryGetValue(go, out observable))
            {
                observable = Observable.EveryFixedUpdate().Select(_ =>
                    {
                        return Physics.RaycastAll(go.transform.position, go.transform.forward);
                    });

                // Force this observable to be a hot observable
                observable = observable.Replay(1).RefCount();
                observable.Subscribe();
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