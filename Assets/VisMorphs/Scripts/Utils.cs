using System;
using System.Collections;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Reflection;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UniRx;
using UnityEngine;

namespace DxR.VisMorphs
{
    public static class Utils
    {
		/// <summary>
		/// Normalises a value from an input range into an output range.
		/// </summary>
		/// <param name="value">The value to normalise.</param>
		/// <param name="i0">The minimum input range.</param>
		/// <param name="i1">The maximum input range.</param>
		/// <param name="j0">The minimum output range.</param>
		/// <param name="j1">The maximum output range.</param>
		/// <returns>The normalised value.</returns>
    	public static float NormaliseValue(float value, float i0, float i1, float j0 = 0, float j1 = 1)
    	{
    		float L = (j0 - j1) / (i0 - i1);
    		return (j0 - (L * i0) + (L * value));
    	}

		public static TValue GetValueOrDefault<TKey,TValue>(this IDictionary<TKey, TValue> dictionary, TKey key)
		{
			TValue ret;
			// Ignore return value
			dictionary.TryGetValue(key, out ret);
			return ret;
		}

        public static System.Object GetPropValue(this System.Object obj, String name) {
            foreach (String part in name.Split('.')) {
                if (obj == null) { return null; }

                Type type = obj.GetType();
                PropertyInfo pInfo = type.GetProperty(part);
                if (pInfo != null) {
                    obj = pInfo.GetValue(obj, null);
                    continue;
                }

                FieldInfo fInfo = type.GetField(part);
                if (fInfo != null) {
                    obj = fInfo.GetValue(obj);
                    continue;
                }

                return null;
            }
            return obj;
        }

        public static T GetPropValue<T>(this System.Object obj, String name) {
            System.Object retval = GetPropValue(obj, name);
            if (retval == null) { return default(T); }

            // throws InvalidCastException if types are incompatible
            return (T) retval;
        }

		/// <summary>
        /// Creates an anonymous observable which only emits the provided value once
        /// </summary>
        /// <param name="value">The item to emit once by the observable</param>
        /// <returns></returns>
		public static IObservable<dynamic> CreateAnonymousObservable(dynamic value)
		{
			var observable = Observable.Create<dynamic>(observer =>
            {
                observer.OnNext(value);
                observer.OnCompleted();
                return Disposable.Empty;
            });

            return observable;
        }
    }
}