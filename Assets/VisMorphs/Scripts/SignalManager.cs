using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Newtonsoft.Json.Linq;
using System;
using System.Linq;
using UniRx;
using DynamicExpresso;

namespace DxR.VisMorphs
{
    /// <summary>
    /// Generates and stores all observable streams for the Signals defined in the Morphing spec
    /// </summary>
    public class SignalManager : MonoBehaviour
    {
        public static SignalManager Instance { get; private set; }

        public Dictionary<string, IObservable<dynamic>> Signals = new Dictionary<string, IObservable<dynamic>>();
        public bool DebugSignals = false;

        private Interpreter interpreter;

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

        /// <summary>
        /// Generates the observables for all Signals as defined in a given JToken
        /// </summary>
        public bool GenerateSignals(JToken signalsJToken)
        {
            if (!signalsJToken.HasValues)
                return false;

            InitialiseInterpreter();

            // First, we create observables for the Signals that are defined as Sources (i.e., have a source property)
            foreach (var signalJToken in signalsJToken)
            {
                JToken sourceJToken = signalJToken.SelectToken("source");
                if (sourceJToken == null) continue;

                string observableName = signalJToken.SelectToken("name").ToString();

                // Create the starting point for our observable based on what is defined in its "source"
                IObservable<dynamic> signalObservable = GenerateObservableFromSource(sourceJToken);

                // Save the observable so that other scripts can reference this later
                SaveObservable(observableName, signalObservable);
            }

            // Next, we create observables for the Signals that are created using expressions (i.e., do not have a source property)
            foreach (var signalJToken in signalsJToken)
            {
                if (signalJToken.SelectToken("source") != null) continue;

                string observableName = signalJToken.SelectToken("name").ToString();
                string expression = signalJToken.SelectToken("expression").ToString();

                IObservable<dynamic> signalObservable = GenerateObservableFromExpression(expression);

                // Save the observable so that other scripts can reference this later
                SaveObservable(observableName, signalObservable);
            }

            return true;
        }

        private void InitialiseInterpreter()
        {
            interpreter = new Interpreter();

            // Set functions

            // Clamp
            Func<double, double, double, double> clamp = (input, min, max) => Math.Clamp(input, min, max);
            interpreter.SetFunction("clamp", clamp);

            // Normalise
            Func<double, double, double, double, double, double> normalise1 = (input, i0, i1, j0, j1) => Utils.NormaliseValue(input, i0, i1, j0, j1);
            Func<double, double, double, double> normalise2 = (input, i0, i1) => Utils.NormaliseValue(input, i0, i1);
            interpreter.SetFunction("normalise", normalise1);
            interpreter.SetFunction("normalise", normalise2);
        }

        /// <summary>
        /// Creates an observable object from a source object that is defined in the specs
        /// </summary>
        private IObservable<dynamic> GenerateObservableFromSource(JToken sourceJToken)
        {
            string sourceType = sourceJToken.SelectToken("type").ToString();        // What type of source is this observable?
            string reference = sourceJToken.SelectToken("ref").ToString();          // What is the name of the actual entity to get the value from?
            string select = sourceJToken.SelectToken("select")?.ToString() ?? "";   // What specific value from this entity do we then extract?

            IObservable<dynamic> observable = null;

            switch (sourceType)
            {
                case "event":
                    {
                        observable = GenerateObservableFromEvent(reference);

                        if (select != "")
                            observable.Select(x => x.GetPropValue(select));
                        break;
                    }

                case "gameobject":
                    {
                        if (select == "")
                            throw new Exception("Signal of type gameobject needs a select expression.");

                        // Find the referenced gameobject and emit values whenever the selected property has changed
                        observable = GameObject.Find(reference).ObserveEveryValueChanged(x => (dynamic)x.GetPropValue(select));
                        break;
                    }
            }

            return observable;
        }

        private IObservable<dynamic> GenerateObservableFromEvent(string eventName)
        {
            switch (eventName.ToString())
            {
                case "mousedown":
                    return Observable.EveryUpdate()
                        .Select(_ => Input.GetMouseButton(0))
                        .DistinctUntilChanged()
                        .Select(_ => (dynamic)_);
                default:
                    throw new Exception("Event of ref \"" + eventName + "\" does not exist.");
            }
        }

        private IObservable<dynamic> GenerateObservableFromExpression(string expression)
        {
            // 1. Find all observables that this expression references
            // 2. When any of these emits:
            //      a. Update the variable on the interpreter
            //      b. Evaluate the interpreter expression
            //      c. Emit a new value

            // Iterate through all Signals that this expression references
            IObservable<dynamic> mergedObservable = null;
            foreach (KeyValuePair<string, IObservable<dynamic>> kvp in Signals)
            {
                string signalName = kvp.Key;
                IObservable<dynamic> observable = kvp.Value;

                if (expression.Contains(signalName))
                {
                    // When this Signal emits, we want to first ensure its variable is updated in the interpreter
                    var newObservable = observable.Select(x =>
                    {
                        // This will be repeated when multiple expressions reference the same Signal,
                        // but it should be okay performance wise I think
                        interpreter.SetVariable(signalName, x);
                        return x;
                    });

                    if (mergedObservable == null)
                    {
                        mergedObservable = newObservable;
                    }
                    else
                    {
                        mergedObservable = mergedObservable.Merge(newObservable);
                    }
                }
            }

            // If there was no merged observable created (i.e., no Signals were used)
            if (mergedObservable != null)
            {
                var expressionObservable = mergedObservable.Select(_ =>
                {
                    return interpreter.Eval(expression);
                });

                return expressionObservable;
            }
            else
            {
                return Utils.CreateAnonymousObservable(interpreter.Eval(expression));
            }
        }

        private void ReevaluateExpressionObservables()
        {

        }

        // private IObservable<dynamic> ApplyTransformToObservable(IObservable<dynamic> observable, string transformType, JToken value)
        // {
        //     switch (transformType)
        //     {
        //         case "clamp":
        //             {
        //                 float min = (float)value[0];
        //                 float max = (float)value[1];
        //                 observable = observable.Select(x => (dynamic)Mathf.Clamp(x, min, max));
        //                 break;
        //             }

        //         case "normalise":
        //             {
        //                 float i0 = (float)value[0];
        //                 float i1 = (float)value[1];
        //                 float j0 = value[2] != null ? (float)value[2] : 0;
        //                 float j1 = value[3] != null ? (float)value[3] : 0;
        //                 observable = observable.Select(x => (dynamic)Utils.NormaliseValue(x, i0, i1, j0, j1));
        //                 break;
        //             }

        //         case "add":
        //             {
        //                 if (value.Type == JTokenType.String)
        //                 {
        //                     string name = value.ToString();
        //                     var otherObservable = GetObservable(name);
        //                     observable = observable.CombineLatest(otherObservable, (x, y) =>
        //                     {
        //                         return x + y;
        //                     });
        //                 }
        //                 else if (value.Type == JTokenType.String)
        //                 {
        //                     float n = (float)value;
        //                     observable = observable.Select(x => x + n);
        //                 }
        //                 break;
        //             }

        //         case "conditional":
        //             {
        //                 string expression = value.ToString();
        //                 DynamicExpresso.Interpreter interpreter = new DynamicExpresso.Interpreter();

        //                 observable = observable.Select(x =>
        //                 {
        //                     interpreter.SetVariable("x", x);
        //                     return interpreter.Eval<dynamic>(expression);
        //                 });
        //                 break;
        //             }
        //     }

        //     return observable;
        // }

        /// <summary>
        /// Saves a given observable such that it can be accessed later on
        /// </summary>
        /// <param name="name"></param>
        /// <param name="observable"></param>
        private void SaveObservable(string name, IObservable<dynamic> observable)
        {
            // Make the observable a ReplaySubject which returns the most recently emitted item as soon as it is subscribed to
            observable = observable.Replay(1).RefCount();
            // WORKAROUND: To force the observable to behave like a hot observable, we would typically use a dummy subscription here
            if (!DebugSignals)
                observable.Subscribe();
            else
                observable.Subscribe(_ => Debug.Log(_));

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