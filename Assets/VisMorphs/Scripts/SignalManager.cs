using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Newtonsoft.Json.Linq;
using System;

namespace DxR.VisMorphs
{
    using UniRx;

    public class SignalManager : MonoBehaviour
    {
        public static SignalManager Instance { get; private set; }

        public Dictionary<string, IObservable<dynamic>> Signals = new Dictionary<string, IObservable<dynamic>>();

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(this);
            }
            else
            {
                Instance = this;
            }
        }

        public bool GenerateSignals(JToken signalsJToken)
        {
            if (!signalsJToken.HasValues)
                return false;

            foreach (var signalJToken in signalsJToken)
            {
                GenerateObservablesFromJson(signalJToken);
            }

            return true;
        }

        /// <summary>
        /// Reads in a token for a specific signal and generates an observable based on the given parameters
        /// </summary>
        /// <param name="signalJToken"></param>
        private void GenerateObservablesFromJson(JToken signalJToken)
        {
            string observableName = signalJToken.SelectToken("name").ToString();

            JToken sourceJToken = signalJToken.SelectToken("source");
            IObservable<dynamic> signalObservable = GenerateObservableFromSource(sourceJToken);

            JToken transformJToken = signalJToken.SelectToken("transform");
            if (transformJToken != null)
            {
                foreach (var kvp in (JObject)transformJToken)
                {
                    signalObservable = ApplyTransformToObservable(signalObservable, kvp.Key, kvp.Value);
                }
            }

            SaveObservable(observableName, signalObservable);
        }

        private IObservable<dynamic> GenerateObservableFromSource(JToken sourceJToken)
        {
            string sourceType = sourceJToken.SelectToken("type").ToString();
            string reference = sourceJToken.SelectToken("ref").ToString();
            string select = sourceJToken.SelectToken("select")?.ToString() ?? "";

            IObservable<dynamic> observable = null;

            switch (sourceType)
            {
                case "event":
                    {
                        observable = GenerateEventObservable(reference);

                        if (select != "")
                            observable.Select(x => x.GetPropValue(select));
                        break;
                    }

                case "gameobject":
                    {
                        if (select == "")
                            throw new Exception("Signal of type gameobject needs a select expression");

                        observable = GameObject.Find(reference).ObserveEveryValueChanged(x => (dynamic)x.GetPropValue(select));
                        break;
                    }

                case "signal":
                    {
                        observable = GetObservable(reference);
                        break;
                    }
            }

            return observable;
        }

        private IObservable<dynamic> GenerateEventObservable(string reference)
        {
            return null;
        }

        private IObservable<dynamic> ApplyTransformToObservable(IObservable<dynamic> observable, string transformType, JToken value)
        {
            switch (transformType)
            {
                case "clamp":
                    {
                        float min = (float)value[0];
                        float max = (float)value[1];
                        observable = observable.Select(x => (dynamic)Mathf.Clamp(x, min, max));
                        break;
                    }

                case "normalise":
                    {
                        float i0 = (float)value[0];
                        float i1 = (float)value[1];
                        float j0 = value[2] != null ? (float)value[2] : 0;
                        float j1 = value[3] != null ? (float)value[3] : 0;

                        observable = observable.Select(x => (dynamic)Utils.NormaliseValue(x, i0, i1, j0, j1));
                        break;
                    }

                case "add":
                    {
                        float n = (float)value;
                        observable = observable.Select(x => x + n);
                        break;
                    }
            }

            return observable;
        }

        /// <summary>
        /// Saves a given observable such that it can be accessed later on
        /// </summary>
        /// <param name="name"></param>
        /// <param name="observable"></param>
        private void SaveObservable(string name, IObservable<dynamic> observable)
        {
            // Make the observable a ReplaySubject which returns the most recently emitted item as soon as it is subscribed to
            observable = observable.Replay(1).RefCount();
            // WORKAROUND: To force the observable to behave like a hot observable, just use a dummy subscribe here
            observable.Subscribe();

            if (!Signals.ContainsKey(name))
            {
                Signals.Add(name, observable);
                return;
            }
            Debug.LogError("Cannot save signal " + name + " as one already exists with the same name");
        }

        /// <summary>
        /// Gets the saved observable with the given name. Returns null if no observable exists with that name
        /// </summary>
        /// <param name="name"></param>
        /// <returns></returns>
        public IObservable<dynamic> GetObservable(string name)
        {
            if (Signals.TryGetValue(name, out IObservable<dynamic> observable))
            {
                return observable;
            }

            return null;
        }

        public void ResetObservables()
        {
            Signals.Clear();
        }
    }
}