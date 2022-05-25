using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using System.Linq;
using System;

namespace DxR.VisMorphs
{
    using UniRx;

    public class UniRxTests : MonoBehaviour
    {
        private void Start()
        {
            Invoke("CreateTimer", 1f);
        }

        private void CreateTimer()
        {
            float startTime = Time.time;
            float duration = 1;

            var cancellationObservable = Observable.Timer(TimeSpan.FromSeconds(duration));
            var timerObservable = Observable.EveryUpdate().Select(_ =>
            {
                float timer = Time.time - startTime;
                return Mathf.Clamp(timer / duration, 0, 1);
            })
                .TakeUntil(cancellationObservable);

            timerObservable.Subscribe(_ => Debug.Log(_));
        }
    }
}