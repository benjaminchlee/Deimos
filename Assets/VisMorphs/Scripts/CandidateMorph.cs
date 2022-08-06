using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using SimpleJSON;
using UniRx;
using UnityEngine;

namespace DxR.VisMorphs
{
    public class CandidateMorph
    {
        public Morph Morph;
        public JSONNode CandidateState;
        public List<Tuple<JSONNode, bool>> CandidateTransitions;
        public List<Tuple<JSONNode, CompositeDisposable, bool[], bool>> CandidateTransitionsWithSubscriptions;
        public Dictionary<string, IObservable<dynamic>> LocalSignalObservables;
        public CompositeDisposable Disposables;
        public Dictionary<string, JObject> StoredVisKeyframes;
        public string Name { get => Morph.Name; }

        public CandidateMorph(Morph morph, JSONNode candidateState)
        {
            Morph = morph;
            CandidateState = candidateState;
            CandidateTransitions = new List<Tuple<JSONNode, bool>>();
            CandidateTransitionsWithSubscriptions = new List<Tuple<JSONNode, CompositeDisposable, bool[], bool>>();
            LocalSignalObservables = new Dictionary<string, IObservable<dynamic>>();
            Disposables = new CompositeDisposable();
            StoredVisKeyframes = new Dictionary<string, JObject>();
        }

        public void SaveLocalSignal(string name, IObservable<dynamic> observable)
        {
            // Make the signal a ReplaySubject which returns the most recently emitted item as soon as it is subscribed to
            observable = observable.Replay(1).RefCount().DistinctUntilChanged();

            // Subscribe to both force the signal to behave as a hot observable, and also for debugging purposes
            observable.Subscribe(_ =>
            {
                if (MorphManager.Instance.DebugSignals)
                    Debug.Log("Local Signal " + name + ": " + _);
            }).AddTo(Disposables);

            if (!LocalSignalObservables.ContainsKey(name))
            {
                LocalSignalObservables.Add(name, observable);
            }
            else
            {
                Debug.LogWarning(string.Format("Vis Morphs: A local signal with the name {0} already exists and therefore has not been overwritten. Is this intentional?", name));
            }
        }

        public IObservable<dynamic> GetLocalSignal(string name)
        {
            if (LocalSignalObservables.TryGetValue(name, out IObservable<dynamic> observable))
            {
                return observable;
            }

            return null;
        }

        public void DisposeTransitionSubscriptions(string transitionName)
        {
            CandidateTransitionsWithSubscriptions.Single(cts => cts.Item1["name"] == transitionName).Item2.Dispose();
        }

        public void ClearLocalSignals()
        {
            foreach (var candidateTransition in CandidateTransitionsWithSubscriptions)
            {
                candidateTransition.Item2.Dispose();
            }

            Disposables.Clear();
        }
    }
}