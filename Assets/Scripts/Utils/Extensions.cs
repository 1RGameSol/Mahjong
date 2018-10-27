﻿using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Assertions;
using UnityEngine.Events;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace Utils
{
    public static class Extensions
    {
        public static void Shuffle<T>(this List<T> list)
        {
            int n = list.Count;
            while (n > 1)
            {
                var k = UnityEngine.Random.Range(0, n--);
                var value = list[k];
                list[k] = list[n];
                list[n] = value;
            }
        }

        public static int FindIndex<T>(this List<T> list, T item, IEqualityComparer<T> comparer)
        {
            for (int i = 0; i < list.Count; i++)
            {
                if (comparer.Equals(list[i], item)) return i;
            }

            return -1;
        }

        public static T RemoveLast<T>(this List<T> list)
        {
            var last = list[list.Count - 1];
            list.RemoveAt(list.Count - 1);
            return last;
        }

        public static void Print(this Text text, string content, bool append = false)
        {
            if (append)
                text.text += "\n" + content;
            else
                text.text = content;
        }

        public static void Subtract<T>(this List<T> list, T[] subList, T except)
        {
            int index = Array.FindIndex(subList, item => item.Equals(except));
            for (int i = 0; i < subList.Length; i++)
            {
                if (i == index) continue;
                list.Remove(subList[i]);
            }
        }

        public static void Subtract<T>(this List<T> list, IEnumerable<T> subList)
        {
            foreach (var item in subList)
            {
                list.Remove(item);
            }
        }

        public static void SetNumber(this Image[] images, int number, Func<int, Sprite> func)
        {
            for (int i = 0; i < images.Length; i++)
            {
                if (number == 0 && i > 0) images[i].gameObject.SetActive(false);
                else
                {
                    images[i].gameObject.SetActive(true);
                    images[i].sprite = func.Invoke(number % 10);
                }

                number /= 10;
            }
            Assert.AreEqual(number, 0, "The display overflows.");
        }

        public static void DestroyAllChild(this Transform transform)
        {
            for (int i = transform.childCount - 1; i >= 0; i--)
            {
                Object.Destroy(transform.GetChild(i).gameObject);
            }
        }
    }
}