using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Newtonsoft.Json.Linq;
using System;
using System.Linq;
using UniRx;
using DynamicExpresso;
using Microsoft.MixedReality.Toolkit.Input;
using Microsoft.MixedReality.Toolkit.Utilities;
using Microsoft.MixedReality.Toolkit;

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
                if (signalJToken.SelectToken("expression") == null) continue;

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

            // Set types
            interpreter = interpreter.Reference(typeof(Vector3));

            // Set functions

            // Clamp
            Func<double, double, double, double> clamp = (input, min, max) => Math.Clamp(input, min, max);
            interpreter.SetFunction("clamp", clamp);

            // Normalise
            Func<double, double, double, double, double, double> normalise1 = (input, i0, i1, j0, j1) => Utils.NormaliseValue(input, i0, i1, j0, j1);
            Func<double, double, double, double> normalise2 = (input, i0, i1) => Utils.NormaliseValue(input, i0, i1);
            interpreter.SetFunction("normalise", normalise1);
            interpreter.SetFunction("normalise", normalise2);

            // Vector
            Func<double, double, double, Vector3> vector3 = (x, y, z) => new Vector3((float)x, (float)y, (float)z);
            interpreter.SetFunction("vector3", vector3);

            // Angle
            Func<Vector3, Vector3, Vector3, float> signedAngle = (from, to, axis) => Vector3.SignedAngle(from, to, axis);
            interpreter.SetFunction("signedangle", signedAngle);

            Func<Vector3, Vector3, float> angle = (from, to) => Vector3.Angle(from, to);
            interpreter.SetFunction("angle", angle);

            // Distance
            Func<Vector3, Vector3, float> distance = (a, b) => Vector3.Distance(a, b);
            interpreter.SetFunction("distance", distance);
        }

        /// <summary>
        /// Creates an observable object from a source object that is defined in the specs
        /// </summary>
        private IObservable<dynamic> GenerateObservableFromSource(JToken sourceJToken)
        {
            string sourceType = sourceJToken.SelectToken("type").ToString();        // What type of source is this observable?
            string reference = sourceJToken.SelectToken("ref").ToString();          // What is the name of the actual entity to get the value from?
            string selector = sourceJToken.SelectToken("select")?.ToString() ?? "";   // What specific value from this entity do we then extract?

            IObservable<dynamic> observable = null;

            switch (sourceType)
            {
                case "event":
                    {
                        observable = GenerateObservableFromEvent(reference, selector);
                        break;
                    }

                // TODO: Make sure this works for both hands and controllers
                case "hands":
                    {
                        observable = GenerateObservableFromHands(reference, selector);
                        break;
                    }

                case "gameobject":
                    {
                        observable = GenerateObservableFromGameObject(reference, selector);
                        break;
                    }
            }

            return observable;
        }

        private IObservable<dynamic> GenerateObservableFromEvent(string reference, string selector)
        {
            switch (reference.ToLower())
            {
                case "mousedown":
                    return Observable.EveryUpdate()
                        .Select(_ => Input.GetMouseButton(0))
                        .DistinctUntilChanged()
                        .Select(_ => (dynamic)_);

                // TODO: Improve the syntax of these events
                case "controller":
                    var mrtkInputDown = new ObservableMRTKInputDown(selector, Handedness.Any);
                    return mrtkInputDown.OnMRTKInputDownAsObservable()
                                .DistinctUntilChanged()
                                .Select(_ => (dynamic)_);

                case "righthandpinch":
                    return Observable.EveryUpdate()
                        .Where(_ => HandJointUtils.FindHand(Handedness.Right) != null)
                        .Select(_ =>
                        {
                            return (dynamic)HandPoseUtils.CalculateIndexPinch(Handedness.Right);
                        })
                        .DistinctUntilChanged();

                default:
                    throw new Exception("Event of ref \"" + reference + "\" does not exist.");
            }
        }

        private IObservable<dynamic> GenerateObservableFromHands(string reference, string selector)
        {
            Handedness handedness = reference.ToLower() == "left" ? Handedness.Left : Handedness.Right;

            return Observable.EveryUpdate()
                .Select(_ =>
                {
                    foreach (var controller in CoreServices.InputSystem.DetectedControllers)
                    {
                        if (controller.ControllerHandedness == Handedness.Right && controller.Visualizer != null)
                        {
                            return (controller.InputSource.Pointers[0] as MonoBehaviour).gameObject;
                        }
                    }

                    return null;
                })
                .Where(_ => _ != null)
                .StartWith(gameObject)
                .Select(x => (dynamic)x.GetPropValue(selector));
        }

        private IObservable<dynamic> GenerateObservableFromGameObject(string reference, string selector)
        {
            if (selector == "")
                throw new Exception("Signal of type gameobject needs a select expression.");

            // Find the referenced gameobject and emit values whenever the selected property has changed
            return GameObject.Find(reference)
                .ObserveEveryValueChanged(x => (dynamic)x.GetPropValue(selector));
        }

        private IObservable<dynamic> GenerateObservableFromExpression(string expression)
        {
            // 1. Find all observables that this expression references
            // 2. When any of these emits:
            //      a. Update the variable on the interpreter
            //      b. Evaluate the interpreter expression
            //      c. Emit a new value

            // Iterate through all Signals that this expression references
            List<IObservable<dynamic>> signalObservables = new List<IObservable<dynamic>>();
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
                    newObservable.Subscribe();  // We need to have this subscribe here otherwise later selects don't work, for some reason

                    signalObservables.Add(newObservable);
                }
            }

            if (signalObservables.Count > 0)
            {
                var expressionObservable = signalObservables[0];

                for (int i = 1; i < signalObservables.Count; i++)
                {
                    expressionObservable = expressionObservable.Merge(signalObservables[i]);
                }

                return expressionObservable.Select(_ => {
                    return interpreter.Eval(expression);
                });
            }
            else
            {
                return Utils.CreateAnonymousObservable(interpreter.Eval(expression));
            }
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
            // WORKAROUND: To force the observable to behave like a hot observable, we would typically use a dummy subscription here
            if (!DebugSignals)
                observable.Subscribe();
            else
                observable.Subscribe(_ => Debug.Log("Signal " + name + ": " + _));

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