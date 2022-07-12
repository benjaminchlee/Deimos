using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Newtonsoft.Json.Linq;
using System;
using DynamicExpresso;

namespace DxR.VisMorphs.Deprecated
{
    using UniRx;

    public class PredicateManager : MonoBehaviour
    {
        public static PredicateManager Instance { get; private set; }

        public Dictionary<string, IObservable<bool>> Predicates = new Dictionary<string, IObservable<bool>>();

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

        public bool GeneratePredicates(JToken predicatesJToken)
        {
            if (!predicatesJToken.HasValues)
                return false;

            foreach (var predicateJToken in predicatesJToken)
            {
                GenerateObservablesFromJson(predicateJToken);
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

            string expression = signalJToken.SelectToken("expression").ToString();

            IObservable<bool> predicateObservable = Observable.Create<bool>(observer =>
            {
                observer.OnNext(false);
                observer.OnCompleted();
                return Disposable.Empty;
            });

            Interpreter interpreter = new Interpreter();

            // Find all operands in the expression that match Signals
            foreach (var operand in expression.Split(' '))
            {
                IObservable<dynamic> observable = SignalManager.Instance.GetObservable(operand);

                if (observable != null)
                {
                    predicateObservable = observable.Select(x =>
                    {
                        interpreter.SetVariable(operand, x);
                        return interpreter.Eval<bool>(expression);
                    })
                        .Merge(predicateObservable);
                }
            }

            SaveObservable(observableName, predicateObservable);
        }

        /// <summary>
        /// Saves a given observable such that it can be accessed later on
        /// </summary>
        /// <param name="name"></param>
        /// <param name="observable"></param>
        private void SaveObservable(string name, IObservable<bool> observable)
        {
            // Make the observable a ReplaySubject which returns the most recently emitted item as soon as it is subscribed to
            observable = observable.Replay(1).RefCount();
            // WORKAROUND: To force the observable to behave like a hot observable, just use a dummy subscribe here
            observable.Subscribe();

            if (!Predicates.ContainsKey(name))
            {
                Predicates.Add(name, observable);
                return;
            }
            Debug.LogError("Cannot save signal " + name + " as one already exists with the same name");
        }

        /// <summary>
        /// Gets the saved observable with the given name. Returns null if no observable exists with that name
        /// </summary>
        /// <param name="name"></param>
        /// <returns></returns>
        public IObservable<bool> GetObservable(string name)
        {
            if (Predicates.TryGetValue(name, out IObservable<bool> observable))
            {
                return observable;
            }

            return null;
        }

        public void ResetObservables()
        {
            Predicates.Clear();
        }
    }
}