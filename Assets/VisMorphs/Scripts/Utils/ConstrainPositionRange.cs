using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace DxR.VisMorphs
{
    public class ConstrainPositionRange : MonoBehaviour
    {
        public bool UseLocalPosition = true;

        [Header("X")]
        public bool ConstrainX = false;
        public float MinX = 0;
        public float MaxX = 0;

        [Header("Y")]
        public bool ConstrainY = false;
        public float MinY = 0;
        public float MaxY = 0;

        [Header("Z")]
        public bool ConstrainZ = false;
        public float MinZ = 0;
        public float MaxZ = 0;

        private void Update()
        {
            Vector3 position = UseLocalPosition ? transform.localPosition : transform.position;

            if (ConstrainX)
            {
                position.x = Mathf.Clamp(position.x, MinX, MaxX);
            }
            if (ConstrainY)
            {
                position.y = Mathf.Clamp(position.y, MinY, MaxY);
            }
            if (ConstrainZ)
            {
                position.z = Mathf.Clamp(position.z, MinZ, MaxZ);
            }

            if (UseLocalPosition)
                transform.localPosition = position;
            else
                transform.position = position;
        }
    }
}