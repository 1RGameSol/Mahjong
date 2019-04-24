﻿using System.Collections;
using System.Collections.Generic;
using Single.MahjongDataType;
using Single.UI.Controller;
using UnityEngine;
using UnityEngine.UI;

namespace Single.UI.SubManagers
{
    public class YakuItem : MonoBehaviour
    {
        public Image Normal;
        public Image YakuMan;
        public NumberPanelController FanController;
        public Text YakuName;
        public YakuSettings YakuSettings;

        public void SetYakuItem(YakuValue yaku, bool 青天井 = false)
        {
            Debug.Assert(YakuName != null, nameof(YakuName) + " != null");
            YakuName.text = yaku.Name;
            if (yaku.Type == YakuType.Normal || 青天井)
            {
                // use normal display
                Normal.gameObject.SetActive(true);
                YakuMan.gameObject.SetActive(false);
                int value = yaku.Type == YakuType.Normal ? yaku.Value : yaku.Value * YakuSettings.YakumanBaseFan;
                Debug.Log($"{yaku.Name}: {value}");
                FanController.SetNumber(value);
            }
            else
            {
                Debug.Log($"{yaku.Name}: YakuMan");
                // use yaku man display
                Normal.gameObject.SetActive(false);
                YakuMan.gameObject.SetActive(true);
            }
        }
    }
}