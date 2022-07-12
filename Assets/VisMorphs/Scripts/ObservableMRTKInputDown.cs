using System;
using System.Collections;
using System.Collections.Generic;
using Microsoft.MixedReality.Toolkit;
using Microsoft.MixedReality.Toolkit.Input;
using Microsoft.MixedReality.Toolkit.Utilities;
using UniRx;
using UniRx.Triggers;
using UnityEngine;

namespace DxR.VisMorphs
{
    public class ObservableMRTKInputDown : ObservableTriggerBase, IMixedRealityInputHandler
    {
        Subject<bool> onMRTKInputDown;

        private string inputName;
        private Handedness handedness;

        public ObservableMRTKInputDown(string input, Handedness handedness = Handedness.Any)
        {
            this.inputName = input.ToLower();
            this.handedness = handedness;

            CoreServices.InputSystem?.RegisterHandler<IMixedRealityInputHandler>(this);
        }

        public void OnInputDown(InputEventData eventData)
        {
            if (eventData.Handedness == handedness && eventData.MixedRealityInputAction.Description.ToLower() == inputName)
            {
                if (onMRTKInputDown != null)
                    onMRTKInputDown.OnNext(true);
            }
        }

        public void OnInputUp(InputEventData eventData)
        {
            if (eventData.Handedness == handedness && eventData.MixedRealityInputAction.Description.ToLower() == inputName)
            {
                if (onMRTKInputDown != null)
                    onMRTKInputDown.OnNext(false);
            }
        }

        public IObservable<bool> OnMRTKInputDownAsObservable()
        {
            return onMRTKInputDown ?? (onMRTKInputDown = new Subject<bool>());
        }

        protected override void RaiseOnCompletedOnDestroy()
        {
            if (onMRTKInputDown != null)
            {
                onMRTKInputDown.OnCompleted();
            }
        }
    }
}
