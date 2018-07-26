﻿using System;

namespace Mahjong.Yakus
{
    public class DuanYao : Yaku
    {
        public override string Name
        {
            get { return "断幺"; }
        }

        public override int Value
        {
            get { return 1; }
        }

        public override bool Test(MianziSet hand, Tile rong, GameStatus status, params YakuOption[] options)
        {
            foreach (Mianzi mianzi in hand)
            {
                if (mianzi.HasYaojiu) return false;
            }

            return true;
        }
    }
}