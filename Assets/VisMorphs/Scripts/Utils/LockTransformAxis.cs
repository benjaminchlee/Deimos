using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace DxR.VisMorphs
{
    public class LockTransformAxis : MonoBehaviour
    {
        public bool UseLocalPosition = false;
        public bool LockX = false;
        public bool LockY = false;
        public bool LockZ = false;

        private Vector3 InitialPosition;

        private void Awake()
        {
            InitialPosition = UseLocalPosition ? transform.localPosition : transform.position;
        }

        private void Update()
        {
            Vector3 position = UseLocalPosition ? transform.localPosition : transform.position;

            if (LockX) position.x = InitialPosition.x;
            if (LockY) position.y = InitialPosition.y;
            if (LockZ) position.z = InitialPosition.z;

            if (UseLocalPosition)
            {
                transform.localPosition = position;
            }
            else
            {
                transform.position = position;
            }
        }
    }
}