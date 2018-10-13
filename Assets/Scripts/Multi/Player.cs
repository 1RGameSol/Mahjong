﻿using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Multi.Messages;
using Prototype.NetworkLobby;
using Single;
using Single.MahjongDataType;
using Single.Yakus;
using UnityEngine;
using UnityEngine.Assertions;
using UnityEngine.Events;
using UnityEngine.Networking;
using UnityEngine.UI;
using Utils;

namespace Multi
{
    public class Player : NetworkBehaviour
    {
        [Header("UI Elements")] public PlayerHandPanel PlayerHandPanel;
//        public GameObject PlayerOpenPanel;

        [Header("Game Status")] [SyncVar] public int TotalPlayers;

        [SyncVar]
        public int PlayerIndex = -1; // Round order index -- 0: East, 1: South, 2: West, 3: North (This does not change)

        [SyncVar] public int BonusTurnTime = 20;
        // todo -- add more game status here, such as prevailing wind

        [Header("Player Public Data")] [SyncVar]
        public string PlayerName = "";

        [SyncVar(hook = nameof(OnPoints))] public int Points;
        [SyncVar] public bool Richi = false;
        [SyncVar] public bool WRichi = false;
        [SyncVar] public bool OneShot = false;
        [SyncVar] public int HandTilesCount = 0;
        public List<Meld> OpenMelds;

        [Header("Player Private Data")] public List<Tile> HandTiles;
        public Tile LastDraw;

        private bool isRichiing = false;
        private bool discardMessageSent = false;
        private Coroutine outTurnOperationWaiting;

        public override void OnStartClient()
        {
            Debug.Log($"Player [{netId}] [name: {PlayerName}] OnStartClient is called");
            LobbyManager.Instance.AddPlayer(this);
        }

        public override void OnStartLocalPlayer()
        {
            Debug.Log($"Player [{netId}] [name: {PlayerName}] OnStartLocalPlayer is called");
            LobbyManager.Instance.LocalPlayer = this;
            RegisterHandlers();
        }

        public override void OnNetworkDestroy()
        {
            LobbyManager.Instance.RemovePlayer(this);
        }

        [Client]
        private void RegisterHandlers()
        {
            LobbyManager.Instance.client.RegisterHandler(MessageConstants.DrawTileMessageId, OnDrawTileMessageReceived);
            LobbyManager.Instance.client.RegisterHandler(MessageConstants.InitialDrawingMessageId,
                OnInitialDrawingMessageReceived);
        }

        [Client]
        private void OnInitialDrawingMessageReceived(NetworkMessage message)
        {
            var content = message.ReadMessage<InitialDrawingMessage>();
            HandTiles = new List<Tile>(content.Tiles);
            // todo -- throwing dice visual effect
            MahjongManager.Instance.InGameInfoText.Print(
                $"Dice rolls to {content.Dice} with open index of {content.MountainOpenIndex}. Total players: {content.TotalPlayers}");
            ClientTurnDoraTiles(content.DoraIndicators, content.DoraIndicatorIndices);
            ClientDrawInitialTiles(content.MountainOpenIndex, content.TotalPlayers);
        }

        private void OnPoints(int amount)
        {
            Debug.Log("Player OnPoints is called");
            Points = amount;
            // todo -- change ui text
        }

        [Server]
        internal void ServerDrawTiles(params Tile[] tiles)
        {
            HandTiles.AddRange(tiles);
            HandTilesCount += tiles.Length;
        }

        [ClientRpc]
        internal void RpcYourTurnToDraw(int nextIndex)
        {
            ClientYourTurnToDraw(nextIndex);
        }

        [ClientRpc]
        internal void RpcDiscardTile(Tile tile, bool discardLastDraw, InTurnOperation operation)
        {
            ClientYourTurnToDiscard(tile, discardLastDraw, operation);
        }

        [Client]
        private void ClientYourTurnToDiscard(Tile tile, bool discardLastDraw, InTurnOperation operation)
        {
            // todo -- add effect for richi
            Debug.Log($"Player {PlayerIndex}'s turn, tile {tile} is discarded. DiscardLastDraw: {discardLastDraw}");
            MahjongManager.Instance.MahjongSelector.DiscardTile(tile, discardLastDraw, PlayerIndex,
                operation.HasFlag(InTurnOperation.Richi));
            StartCoroutine(ClientUpdateTilesForSeconds(GameSettings.PlayerHandTilesSortDelay));
            if (!isLocalPlayer) return;
            if (discardLastDraw)
            {
                PlayerHandPanel.DiscardTile();
            }
            else
            {
                int index = HandTiles.FindIndex(tile, Tile.TileConsiderColorEqualityComparer);
                HandTiles.RemoveAt(index);
                PlayerHandPanel.DiscardTile(index);
                HandTiles.Add(LastDraw);
                HandTiles.Sort();
                ClientUpdateTiles();
            }

            DisableInTurnPanel();
        }

        [ClientRpc]
        internal void RpcOutTurnOperation(Tile discardTile)
        {
            ClientHandleOutTurnOperation(discardTile);
        }

        [Client]
        private void ClientHandleOutTurnOperation(Tile discardTile)
        {
            if (!isLocalPlayer) return;
            var can = PlayerIndex == MahjongManager.Instance.NextPlayerIndex; // todo -- more game setting variations
            var chows = can ? MahjongLogic.GetChows(HandTiles, discardTile) : new HashSet<Meld>();
            var pongs = MahjongLogic.GetPongs(HandTiles, discardTile);
            var kongs = MahjongLogic.GetKongs(HandTiles, discardTile);
            var decomposes = MahjongLogic.Decompose(HandTiles, OpenMelds, discardTile);
            var handStatus = GetCurrentHandStatus();
            var roundStatus = GetCurrentRoundState();
            var pointInfo = YakuManager.Instance.GetPointInfo(decomposes, discardTile, handStatus, roundStatus);
            Debug.Log($"OutTurnOperation: {pointInfo}");
            // after richi, only response to RONG
            if (Richi)
            {
                chows.Clear();
                pongs.Clear();
                kongs.Clear();
            }

            // dora will be counted when this round ends
            if (chows.Count == 0 && pongs.Count == 0 && kongs.Count == 0 && pointInfo.BasePoint == 0)
            {
                SendOutTurnOperationMessage(new OutTurnOperationMessage
                {
                    PlayerIndex = PlayerIndex,
                    Operation = OutTurnOperation.Skip,
                    BonusTurnTime = BonusTurnTime
                });
                return;
            }

            EnableOutTurnPanel(chows, pongs, kongs, pointInfo.BasePoint > 0, discardTile);
            // wait for operation or time expires
            MahjongManager.Instance.TimerController.StartCountDown(GameSettings.BaseTurnTime, BonusTurnTime,
                () =>
                {
                    DisableOutTurnPanel();
                    SendOutTurnOperationMessage(new OutTurnOperationMessage
                    {
                        PlayerIndex = PlayerIndex,
                        Operation = OutTurnOperation.Skip,
                        BonusTurnTime = 0
                    });
                });
        }

        [Client]
        private HandStatus GetCurrentHandStatus()
        {
            var handStatus = HandStatus.Nothing;
            if (OpenMelds.Count == 0) handStatus |= HandStatus.Menqing;
            if (MahjongManager.Instance.FirstTurn) handStatus |= HandStatus.FirstRound;
            if (MahjongManager.Instance.LastDraw) handStatus |= HandStatus.LastDraw;
            if (Richi) handStatus |= HandStatus.Richi;
            if (WRichi) handStatus |= HandStatus.WRichi;
            if (OneShot) handStatus |= HandStatus.OneShot;
            return handStatus;
        }

        [Client]
        private RoundStatus GetCurrentRoundState()
        {
            return new RoundStatus
            {
                TotalPlayer = TotalPlayers,
                PlayerIndex = PlayerIndex,
                RoundCount = MahjongManager.Instance.RoundCount,
                FieldCount = MahjongManager.Instance.FieldCount,
            };
        }

        [Client]
        internal void SendOutTurnOperationMessage(OutTurnOperationMessage message)
        {
            connectionToServer.Send(MessageConstants.OutTurnOperationMessageId, message);
        }

        [Client]
        private void ClientDrawInitialTiles(int openIndex, int totalPlayers)
        {
            Debug.Log($"Player {PlayerIndex} ClientDrawInitialTiles is called");
            ClearHandTiles();
            StartCoroutine(MahjongManager.Instance.MahjongSelector.DrawInitialCoroutine(this, openIndex, totalPlayers));
        }

        [Client] // todo -- maybe need to move this method to MahjongSelector
        private void ClientTurnDoraTiles(Tile[] tiles, int[] indices)
        {
            if (!isLocalPlayer) return;
            Debug.Log($"Player {PlayerIndex} ClientTurnDoraTiles is called");
            Assert.AreEqual(tiles.Length, indices.Length, "Something is very wrong in method ClientTurnDoraTiles");
            var mahjongSelector = MahjongManager.Instance.MahjongSelector;
            for (int i = 0; i < tiles.Length; i++)
            {
                mahjongSelector.RevealTileAt(indices[i], tiles[i]);
            }
        }

        [Client]
        internal void ClientYourTurnToDraw(int nextIndex)
        {
            Debug.Log("Draw a tile from the mahjong set.");
            discardMessageSent = false;
            var mahjongSelector = MahjongManager.Instance.MahjongSelector;
            mahjongSelector.DrawToPlayer(nextIndex, PlayerIndex);
        }

        [Client]
        private void OnDrawTileMessageReceived(NetworkMessage message)
        {
            var content = message.ReadMessage<DrawTileMessage>();
            var tile = content.Tile;
            Debug.Log($"Tile {tile} is drawn");
            LastDraw = tile;
            var operation = GetInTurnOperation(HandTiles, OpenMelds, tile);
            isRichiing = false;
            EnableInTurnPanel(operation, tile);
            PlayerHandPanel.DrawTile(this, tile);
            // todo -- handle auto discard after richi
            if (Richi && !operation.HasFlag(InTurnOperation.Tsumo) && !operation.HasFlag(InTurnOperation.ConcealedKong))
            {
                StartCoroutine(RichiAutoDiscard(tile));
                return;
            }

            MahjongManager.Instance.TimerController.StartCountDown(GameSettings.BaseTurnTime, BonusTurnTime,
                () =>
                {
                    connectionToServer.Send(MessageConstants.DiscardTileMessageId, new DiscardTileMessage
                    {
                        DiscardTile = tile,
                        Operation = InTurnOperation.Discard,
                        PlayerIndex = PlayerIndex,
                        DiscardLastDraw = true
                    });
                });
        }

        [Client]
        public InTurnOperation GetInTurnOperation(List<Tile> handTiles, List<Meld> openMelds, Tile tile)
        {
            var operation = InTurnOperation.Discard;
            if (openMelds.Count == 0 && !Richi) operation |= InTurnOperation.Richi;

            // test for tsumo
            var handStatus = GetCurrentHandStatus();
            var roundStatus = GetCurrentRoundState();
            var pointInfo = YakuManager.Instance.GetPointInfo(handTiles, openMelds, tile, handStatus, roundStatus);
            if (pointInfo.BasePoint > 0) operation |= InTurnOperation.Tsumo;
            // test for kong
            int count = 0;
            foreach (var handTile in handTiles)
            {
                if (handTile.EqualsIgnoreColor(tile)) count++;
            }

            if (count == 3) operation |= InTurnOperation.ConcealedKong;
            Assert.IsTrue(count <= 3, "More than four identical tiles exists, this should not happen!");
            foreach (var meld in openMelds)
            {
                if (meld.Type == MeldType.Triplet && meld.First.EqualsIgnoreColor(tile))
                    operation |= InTurnOperation.AddedKong;
            }

            return operation;
        }

        [Client]
        private IEnumerator RichiAutoDiscard(Tile tile)
        {
            yield return new WaitForSeconds(GameSettings.AutoDiscardDelayAfterRichi);
            connectionToServer.Send(MessageConstants.DiscardTileMessageId, new DiscardTileMessage
            {
                DiscardTile = tile,
                Operation = InTurnOperation.Discard,
                PlayerIndex = PlayerIndex,
                DiscardLastDraw = true
            });
        }

        [Client]
        internal void ClientAddTiles(List<Tile> tiles)
        {
            PlayerHandPanel.AddTiles(this, tiles);
        }

        [Client]
        private void ClearHandTiles()
        {
            PlayerHandPanel.Clear();
        }

        /// <summary>
        /// This method is only called on local player
        /// </summary>
        [Client]
        internal void ClientUpdateTiles()
        {
            if (!isLocalPlayer)
            {
                Debug.LogError("This method can only be called on local player!");
                return;
            }

            PlayerHandPanel.Refresh(this, HandTiles);
            var mahjongSelector = MahjongManager.Instance.MahjongSelector;
            mahjongSelector.Refresh(HandTiles, PlayerIndex);
        }

        [Client]
        private IEnumerator ClientUpdateTilesForSeconds(float delay)
        {
            yield return new WaitForSeconds(delay);
            var mahjongSelector = MahjongManager.Instance.MahjongSelector;
            mahjongSelector.Refresh(HandTilesCount, PlayerIndex);
        }

        [Client]
        internal void InitialDrawComplete(bool complete)
        {
            Debug.Log($"Player {PlayerIndex} drawing complete, sending message!");
            connectionToServer.Send(MessageConstants.ReadinessMessageId, new ReadinessMessage
            {
                PlayerIndex = PlayerIndex
            });
        }

        [Client]
        internal void ClientDiscardTile(Tile tile, bool discardLastDraw)
        {
            if (discardMessageSent) return;
            discardMessageSent = true;
            Debug.Log($"Client attempts to discard tile {tile}");
            var operation = InTurnOperation.Discard;
            if (isRichiing) operation |= InTurnOperation.Richi;
            int bonusTimeLeft = MahjongManager.Instance.TimerController.StopCountDown();
            connectionToServer.Send(MessageConstants.DiscardTileMessageId, new DiscardTileMessage
            {
                DiscardTile = tile,
                Operation = operation,
                PlayerIndex = PlayerIndex,
                DiscardLastDraw = discardLastDraw,
                BonusTurnTime = bonusTimeLeft
            });
        }

        [Client]
        private void EnableInTurnPanel(InTurnOperation operation, Tile tile)
        {
            if (operation == InTurnOperation.Discard) return;
            MahjongManager.Instance.InTurnOperationPanel.SetActive(true);
            if (!Richi && operation.HasFlag(InTurnOperation.Richi))
            {
                MahjongManager.Instance.RichiButton.gameObject.SetActive(true);
                ReplaceListener(MahjongManager.Instance.RichiButton, () => { isRichiing = !isRichiing; });
            }

            if (operation.HasFlag(InTurnOperation.Tsumo))
            {
                MahjongManager.Instance.TsumoButton.gameObject.SetActive(true);
                ReplaceListener(MahjongManager.Instance.TsumoButton, () =>
                {
                    DisableInTurnPanel();
                    int bonusTime = MahjongManager.Instance.TimerController.StopCountDown();
                    connectionToServer.Send(MessageConstants.InTurnOperationMessageId,
                        new InTurnOperationMessage
                        {
                            PlayerIndex = PlayerIndex,
                            Operation = InTurnOperation.Tsumo,
                            Meld = new Meld(false, tile),
                            BonusTurnTime = bonusTime
                        });
                });
            }

            if (operation.HasFlag(InTurnOperation.ConcealedKong) || operation.HasFlag(InTurnOperation.AddedKong))
            {
                MahjongManager.Instance.InTurnKongButton.gameObject.SetActive(true);
                ReplaceListener(MahjongManager.Instance.InTurnKongButton, () =>
                {
                    // todo -- add listener for kong
                });
            }
        }

        [Client]
        private void DisableInTurnPanel()
        {
            MahjongManager.Instance.InTurnOperationPanel.SetActive(false);
        }

        [Client]
        private void EnableOutTurnPanel(ISet<Meld> chows, ISet<Meld> pongs, ISet<Meld> kongs, bool rong,
            Tile discardTile)
        {
            MahjongManager.Instance.SkipButton.gameObject.SetActive(true);
            ReplaceListener(MahjongManager.Instance.SkipButton,
                () =>
                {
                    DisableOutTurnPanel();
                    int bonusTime = MahjongManager.Instance.TimerController.StopCountDown();
                    SendOutTurnOperationMessage(new OutTurnOperationMessage
                    {
                        PlayerIndex = PlayerIndex,
                        Operation = OutTurnOperation.Skip,
                        BonusTurnTime = bonusTime
                    });
                });
            if (rong)
            {
                MahjongManager.Instance.RongButton.gameObject.SetActive(true);
                ReplaceListener(MahjongManager.Instance.RongButton, () =>
                {
                    DisableOutTurnPanel();
                    int bonusTime = MahjongManager.Instance.TimerController.StopCountDown();
                    SendOutTurnOperationMessage(new OutTurnOperationMessage
                    {
                        PlayerIndex = PlayerIndex,
                        Operation = OutTurnOperation.Rong,
                        BonusTurnTime = bonusTime,
                        Meld = new Meld(true, discardTile)
                    });
                });
            }

            if (chows.Count > 0)
                HandleOpenMeldOperations(MahjongManager.Instance.ChowButton, chows, OutTurnOperation.Chow);

            if (pongs.Count > 0)
                HandleOpenMeldOperations(MahjongManager.Instance.PongButton, pongs, OutTurnOperation.Pong);

            if (kongs.Count > 0)
                HandleOpenMeldOperations(MahjongManager.Instance.OutTurnKongButton, kongs, OutTurnOperation.Kong);

            MahjongManager.Instance.OutTurnOperationPanel.SetActive(true);
        }

        [Client] // todo -- skip selecting from MeldSelector when there is only one option
        private void HandleOpenMeldOperations(Button button, ISet<Meld> melds, OutTurnOperation operation)
        {
            button.gameObject.SetActive(true);
            ReplaceListener(button, () =>
            {
                if (melds.Count == 1)
                {
                    DisableInTurnPanel();
                    int bonusTime = MahjongManager.Instance.TimerController.StopCountDown();
                    SendOutTurnOperationMessage(new OutTurnOperationMessage
                    {
                        PlayerIndex = PlayerIndex,
                        Operation = operation,
                        BonusTurnTime = bonusTime,
                        Meld = melds.First()
                    });
                }

                MahjongManager.Instance.MeldSelector.gameObject.SetActive(true);
                MahjongManager.Instance.MeldSelector.ResetMelds();
                MahjongManager.Instance.MeldSelector.AddMelds(melds, meld =>
                {
                    DisableOutTurnPanel();
                    int bonusTime = MahjongManager.Instance.TimerController.StopCountDown();
                    SendOutTurnOperationMessage(new OutTurnOperationMessage
                    {
                        PlayerIndex = PlayerIndex,
                        Operation = operation,
                        BonusTurnTime = bonusTime,
                        Meld = meld
                    });
                });
            });
        }

        [Client]
        private void DisableOutTurnPanel()
        {
            MahjongManager.Instance.OutTurnOperationPanel.SetActive(false);
            MahjongManager.Instance.MeldSelector.gameObject.SetActive(false);
        }

        [Client]
        private void ReplaceListener(Button button, UnityAction call)
        {
            button.onClick.RemoveAllListeners();
            button.onClick.AddListener(call);
        }

        [ClientRpc] // todo -- complete this
        internal void RpcPerformChow(Meld meld)
        {
            ClientPerformChow(meld);
        }

        [Client]
        private void ClientPerformChow(Meld meld)
        {
        }
    }
}